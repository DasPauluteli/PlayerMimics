"""
PlayerMimics AI server (host only). Runs next to the game on the host's PC; the mod talks to it over
http://127.0.0.1:5279. Start it with start.ps1 / start.bat (Windows) or start.sh (Linux) before hosting.

Based on the AI server of brant's LethalMimicsAI add-on (https://github.com/BKPatt/LethalMimics) and still answers its
endpoints (/respond, /line). Since then:
  * Speech recognition: Whisper large-v3-turbo (English unless --language says otherwise), on the GPU through PyTorch (gpu_whisper.py)
    when torch has CUDA or ROCm, else faster-whisper (CTranslate2, int8) on the CPU.
  * Voice cloning: Audio8-TTS-Realistic-0.6B in bf16 on the GPU, else the CPU (torch_tts.py). --tts onnx switches to
    the Audio8-TTS-Preview-0.1B ONNX INT8 runtime (onnx_tts.py). The reference clip is encoded once per voice rebuild.
  * /speak_stream + /speak_poll hand out a line's audio while it's still being made; /speak_poll's start_ok says when
    the mod can start playing without catching up with generation. /speak_line returns the whole line.
  * /decide (PlayerMimics' brain): the model picks what a Masked does and says, streamed, with "say" written first so
    the cloned line starts before the rest of the answer is done. /round: a round started or ended.
  * LLM on a local Ollama. Default: Orion 26B-A4B v1.1 (hf.co/bartowski/TheDrummer_Orion-26B-A4B-v1.1-GGUF:Q4_K_M),
    an uncensored Gemma 4 finetune. Any other: --llm-model <name>, or --llm-api openai for an OpenAI-compatible server.
  * Voice bank per player: keeps the cleanest normal-speaking segments and rebuilds the clone reference from the best
    ones as better audio arrives. Optional persistence (--cache-dir) keeps voices and speech style between games.

Stdlib HTTP + JSON. Audio is exchanged as base64 little-endian PCM16 mono.
"""

from __future__ import annotations

import argparse
import base64
import collections
import difflib
import io
import json
import logging
import math
import os
import re
import sys
import threading
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# Must be set before torch loads (torch_tts / gpu_whisper): torch 2.14 + ROCm 10.1 routes some matmuls to
# experimental Triton kernels that fail to import on this build.
os.environ.setdefault("TORCH_DISABLE_NATIVE_JIT", "1")

import numpy as np

log = logging.getLogger("lethalaudio")

ANALYSIS_SR = 16000
MAX_SEGMENTS_PER_PLAYER = 40
MAX_STYLE_LINES = 80
TRANSCRIPT_WINDOW_SECONDS = 180
CPS_DEFAULT = 11.0  # characters of text per second of a cloned line, until a voice has its own number
# Facts the LLM keeps getting wrong. Lines that contradict them give the mimic away.
GAME_FACTS = (
    "Lethal Company facts (never contradict them): scrap/loot only spawns INSIDE the facility building (factory, "
    "mansion or mineshaft). Outside on the moon there is no loot, only terrain, the ship, sometimes a bee hive and "
    "outside monsters (eyeless dogs, forest giants, worms), which get worse at night. Nobody finds loot in the ship; "
    "players bring scrap there to sell it. The facility is entered through the main entrance or fire exits. Inside there "
    "are traps (turrets, landmines, spike traps) and monsters. The ship leaves at midnight or when someone pulls the "
    "lever, and anyone left behind dies. Only talk about what you could plausibly have seen from where you are."
)
WHISPER_JUNK = {"you", "thank you", "thanks for watching", "thank you for watching", "bye", "so", "uh", "um", "hmm"}
# The add-on's Language setting uses names ("English"); faster-whisper wants ISO codes.
WHISPER_LANG = {"english": "en", "chinese": "zh", "german": "de", "spanish": "es", "french": "fr",
                "italian": "it", "japanese": "ja", "korean": "ko", "auto": "auto"}


# --------------------------------------------------------------------------------------------- utils

def pcm16_b64_to_float(b64: str) -> np.ndarray:
    raw = base64.b64decode(b64)
    return np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768.0


def float_to_pcm16_b64(audio: np.ndarray) -> str:
    pcm = np.clip(audio, -1.0, 1.0)
    return base64.b64encode((pcm * 32767.0).astype("<i2").tobytes()).decode("ascii")


def resample(audio: np.ndarray, sr: int, target: int) -> np.ndarray:
    if sr == target or audio.size == 0:
        return audio.astype(np.float32)
    import librosa
    return librosa.resample(audio.astype(np.float32), orig_sr=sr, target_sr=target).astype(np.float32)


def safe_key(key: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]", "_", key)[:80] or "unknown"


def name_tokens(username: str) -> set[str]:
    """Words a username is likely to be spoken as: 'xX_Brant_99' -> {'brant'}."""
    parts = re.split(r"[^A-Za-z]+", re.sub(r"([a-z])([A-Z])", r"\1 \2", username or ""))
    return {p.lower() for p in parts if len(p) >= 3 and p.lower() not in {"the", "and", "xxx", "ttv", "yt"}}


def words_in(text: str) -> list[str]:
    return re.findall(r"[a-z']+", text.lower())


# ------------------------------------------------------------------------------------ voice analysis

def analyze(audio16: np.ndarray, baseline_f0: float | None) -> dict:
    """Loudness, pitch and voicing features used to reject screams/whispers and score clone suitability."""
    import librosa

    frame, hop = 400, 160  # 25ms / 10ms
    if audio16.size < frame * 4:
        return {"ok": False, "reason": "too short"}

    rms = librosa.feature.rms(y=audio16, frame_length=frame, hop_length=hop)[0]
    db = 20 * np.log10(np.maximum(rms, 1e-6))
    active = db > -45
    active_frac = float(active.mean())
    if active.sum() < 20:
        return {"ok": False, "reason": "silent"}

    speech_db = float(np.mean(db[active]))
    noise_floor = float(np.percentile(db, 10))
    dynamic = float(np.percentile(db, 90) - noise_floor)
    clip_frac = float(np.mean(np.abs(audio16) > 0.98))
    flatness = float(np.mean(librosa.feature.spectral_flatness(y=audio16, n_fft=512, hop_length=hop)[0][active[: len(rms)]]))

    f0, voiced, _ = librosa.pyin(audio16, fmin=65, fmax=1000, sr=ANALYSIS_SR, frame_length=1024, hop_length=hop)
    n = min(len(voiced), len(active))
    voiced_active = voiced[:n] & active[:n]
    voiced_frac = float(voiced_active.sum() / max(1, active[:n].sum()))
    f0_med = float(np.nanmedian(f0[:n][voiced_active])) if voiced_active.sum() >= 5 else float("nan")
    # Intonation: how far the pitch moves within the clip, 10th to 90th percentile in semitones. Flat delivery is
    # under ~2, lively talk 4-8. A much wider spread is usually the pitch tracker slipping an octave.
    f0_voiced = f0[:n][voiced_active]
    f0_voiced = f0_voiced[np.isfinite(f0_voiced)]
    if f0_voiced.size >= 10:
        semitones = 12 * np.log2(f0_voiced / np.median(f0_voiced))
        pitch_range = float(np.percentile(semitones, 90) - np.percentile(semitones, 10))
    else:
        pitch_range = 0.0

    m = dict(speech_db=speech_db, noise_floor=noise_floor, dynamic=dynamic, clip_frac=clip_frac,
             flatness=flatness, voiced_frac=voiced_frac, f0=f0_med, active_frac=active_frac, pitch_range=pitch_range)

    # --- rejection rules
    if voiced_frac < 0.22 or (speech_db < -36 and flatness > 0.25):
        return {**m, "ok": False, "reason": "whisper/unvoiced"}
    if math.isnan(f0_med):
        return {**m, "ok": False, "reason": "no pitch"}
    base = baseline_f0 or 160.0
    if clip_frac > 0.005 or f0_med > max(420.0, base * 1.7) or (speech_db > -9 and f0_med > base * 1.35):
        return {**m, "ok": False, "reason": "scream/shout"}
    if dynamic < 12:
        return {**m, "ok": False, "reason": "noisy"}

    # --- quality score in [0, 1]
    level = math.exp(-((speech_db + 20) ** 2) / (2 * 8 ** 2))
    voicing = min(1.0, voiced_frac / 0.55)
    snr = min(1.0, max(0.0, (dynamic - 12) / 25))
    typical = math.exp(-(math.log2(f0_med / base) ** 2) / (2 * 0.25 ** 2)) if baseline_f0 else 0.8
    # The clone copies the reference's delivery, so lively clips make lively clones. Screams are already out above.
    lively = min(1.0, max(0.0, (pitch_range - 1.5) / 5.0)) * (0.5 if pitch_range > 14 else 1.0)
    score = 0.2 * level + 0.2 * voicing + 0.25 * snr + 0.1 * typical + 0.25 * lively
    return {**m, "ok": True, "reason": "", "score": float(score)}


# ------------------------------------------------------------------------------------ player profile

class Segment:
    def __init__(self, seg_id: str, audio: np.ndarray, sr: int, text: str, score: float, f0: float, created: float):
        self.id, self.audio, self.sr, self.text = seg_id, audio, sr, text
        self.score, self.f0, self.created = score, f0, created

    @property
    def seconds(self) -> float:
        return self.audio.size / self.sr


def soft_limit(x: np.ndarray, knee: float = 0.9) -> np.ndarray:
    """Leaves samples under the knee alone and squashes louder ones smoothly below 1."""
    a = np.abs(x)
    over = a > knee
    if over.any():
        x = x.copy()
        x[over] = np.sign(x[over]) * (knee + (1 - knee) * np.tanh((a[over] - knee) / (1 - knee)))
    return x


GPU_WAIT = 10.0  # longest a GPU job waits for a streaming line to finish


# Chat abbreviations people say letter by letter. The voice reads them as words ("lmao" came out as "la mayo"),
# so it gets them spelled out; the text everyone sees and the logs keep them as written.
SPELLED = ("lol", "lmao", "lmfao", "rofl", "wtf", "wth", "omg", "idk", "idc", "brb", "afk", "gg", "ggs", "tbh", "ngl",
           "smh", "imo", "btw", "irl", "fr", "nvm", "ty", "np", "jk", "af", "rn", "asap", "fyi", "ffs", "stfu", "gtfo")
SPELLED_RE = re.compile(r"\b(" + "|".join(SPELLED) + r")\b", re.I)


def spell_for_voice(text: str) -> str:
    return SPELLED_RE.sub(lambda m: " ".join(m.group(1).upper()), text)


class Clip:
    """Something a player really said on voice chat, kept so their Masked can play it back word for word."""

    def __init__(self, clip_id: str, audio: np.ndarray, sr: int, text: str, created: float):
        self.id = clip_id
        self.audio = audio.astype(np.float32)
        self.sr = sr
        self.text = text
        self.created = created
        self.played = 0.0  # when a Masked last played it

    @property
    def seconds(self) -> float:
        return self.audio.size / self.sr


MAX_CLIPS_PER_PLAYER = 60
CLIP_MIN_SECONDS, CLIP_MAX_SECONDS = 0.5, 6.0
CLIP_MIN_AGE = 45.0  # a clip this fresh would be heard twice in a row by the same people
CLIP_REPLAY_GAP = 120.0  # the same recording twice in two minutes is a giveaway
CLIPS_OFFERED = 12
# A recording that talks about dying, the mimics or the mod gives the Masked away the moment it's played.
CLIP_GIVEAWAY = re.compile(
    r"\b(mimic\w*|masked|masks?|ai|a\.i\.?|bot|bots|mods?|modded|server|logs?|claude|gemini|llm|died|dead|dying|die|"
    r"killed me|got killed|respawn\w*|revive\w*|spectat\w*|ghost)\b", re.I)


class LineCancelled(Exception):
    """Raised inside the TTS callback to stop a line nobody is going to play (/speak_cancel)."""


class LineStream:
    """One line being generated for /speak_stream; /speak_poll reads its samples as they appear."""

    HOLDBACK = 64  # output samples kept back until the next chunk (resampling edge)
    cancelled = False

    def __init__(self, sid: str, out_sr: int, gain: float | None):
        self.id = sid
        self.out_sr = out_sr
        self.gain = gain
        self.samples = np.zeros(0, dtype=np.float32)
        self.done = False
        self.error = ""
        self.created = time.time()
        self.marks: list[tuple[float, float]] = []  # (wall time, seconds of audio ready)
        self.est_seconds = 0.0  # how long the whole line will probably be
        self.lock = threading.Lock()

    def add(self, wave: np.ndarray, src_sr: int, final: bool) -> None:
        if self.cancelled:
            raise LineCancelled()
        if wave.size == 0 and not final:
            return
        if self.gain is None:  # first line of this voice: set the level from the first chunk
            peak = float(np.max(np.abs(wave))) if wave.size else 0.0
            self.gain = min(4.0, 0.9 / peak) if peak > 1e-4 else 1.0
        y = soft_limit(resample(wave, src_sr, self.out_sr).astype(np.float32) * self.gain)
        with self.lock:
            start = self.samples.size
            end = y.size if final else max(start, y.size - self.HOLDBACK)
            new = y[start:end].copy()
            fade = self.out_sr // 100  # 10 ms
            if start == 0 and new.size:
                n = min(fade, new.size)
                new[:n] *= np.linspace(0, 1, n, dtype=np.float32)
            if final and new.size:
                n = min(fade, new.size)
                new[-n:] *= np.linspace(1, 0, n, dtype=np.float32)
            self.samples = np.concatenate((self.samples, new))
            self.marks.append((time.time(), self.samples.size / self.out_sr))
            self.done = self.done or final

    def speed(self) -> float:
        """Seconds of audio made per second of generation, measured after the first chunk (0 = not known yet)."""
        with self.lock:
            if len(self.marks) < 2:
                return 0.0
            (t0, a0), (t1, a1) = self.marks[0], self.marks[-1]
            return (a1 - a0) / (t1 - t0) if t1 > t0 else 0.0


class Profile:
    def __init__(self, key: str):
        self.key = key
        self.name = key
        self.aliases: collections.Counter[str] = collections.Counter()
        self.lines: collections.deque[str] = collections.deque(maxlen=MAX_STYLE_LINES)
        self.generated: collections.deque[str] = collections.deque(maxlen=12)
        self.segments: list[Segment] = []
        self.clips: collections.deque[Clip] = collections.deque(maxlen=MAX_CLIPS_PER_PLAYER)  # memory only, never saved
        self.clip_count = 0
        self.prompt = None
        self.prompt_sig: tuple = ()
        self.prompt_quality = 0.0
        self.prompt_seconds = 0.0
        self.last_rebuild = 0.0
        self.dirty = False
        self.stream_gain: float | None = None  # loudness gain from this voice's last full line (streamed lines)
        self.cps = CPS_DEFAULT  # characters per second of this voice's lines, to know how long a line will run
        self.last_said = 0.0  # when a mimic last said something in this voice
        self.lock = threading.RLock()

    @property
    def baseline_f0(self) -> float | None:
        f0s = [s.f0 for s in self.segments if not math.isnan(s.f0)]
        return float(np.median(f0s)) if len(f0s) >= 3 else None

    def confirmed_aliases(self) -> list[str]:
        return [a for a, c in self.aliases.most_common(6) if c >= 2]

    def all_names(self) -> set[str]:
        return name_tokens(self.name) | {a.lower() for a, c in self.aliases.items() if c >= 2}


# ------------------------------------------------------------------------------------------- server

class Brain:
    def __init__(self, args):
        self.args = args
        self.profiles: dict[str, Profile] = {}
        self.profiles_lock = threading.Lock()
        self.asr_lock = threading.Lock()
        self.tts_lock = threading.Lock()
        self.transcript: collections.deque[tuple[float, str, str, bool]] = collections.deque(maxlen=200)
        self.lines_since_alias_scan = 0
        self.tts = None
        self.asr = None
        self.asr_gpu = None
        self.asr_name = ""
        self.streams: dict[str, LineStream] = {}
        self.streams_lock = threading.Lock()
        # Cleared while a streamed line is being made. GPU speech recognition and LLM calls wait for it (up to
        # GPU_WAIT s), because sharing the GPU slows the line below real time and its playback would stutter.
        self.gpu_quiet = threading.Event()
        self.gpu_quiet.set()
        self.state = "loading"
        self.error = ""
        self.llm_think_ok = True
        self.tl = threading.local()  # per request thread: llm_stats (token counts and timings for the log)
        self.round_started = 0.0  # PlayerMimics: chat from before this (minus a minute) is an old round
        self.decide_seq: dict[str, int] = {}  # PlayerMimics: the newest /decide per Masked; older ones stop
        self.decide_lock = threading.Lock()
        # Training data (the mod's Brain > CollectTrainingData): decisions saved as text, never audio.
        self.training_lock = threading.Lock()
        self.collect_since = 0.0  # when collecting started; 0 = off
        self.cache_dir = args.cache_dir

    # ---- model loading

    def load(self):
        try:
            self.asr_gpu = None
            if self.args.asr_device != "cpu" and self.args.asr_model == "large-v3-turbo" \
                    and os.path.isdir(self.args.whisper_gpu_model_dir):
                try:
                    import torch
                    if torch.cuda.is_available():
                        from gpu_whisper import GpuWhisper
                        self.asr_name = f"Whisper large-v3-turbo bf16 on GPU ({torch.cuda.get_device_name(0)})"
                        log.info("Loading ASR from %s (%s)", self.args.whisper_gpu_model_dir, self.asr_name)
                        self.asr_gpu = GpuWhisper(self.args.whisper_gpu_model_dir, device="cuda", dtype="bfloat16")
                    elif self.args.asr_device == "cuda":
                        raise RuntimeError("--asr-device cuda, but torch sees no GPU")
                except Exception:  # noqa: BLE001
                    if self.args.asr_device == "cuda":
                        raise
                    log.warning("GPU speech recognition failed to load; using the CPU:\n%s", traceback.format_exc())
                    self.asr_gpu = None
            if self.asr_gpu is None:
                from faster_whisper import WhisperModel

                self.asr_name = f"faster-whisper {self.args.asr_model} on CPU int8"
                log.info("Loading ASR %s", self.asr_name)
                self.asr = WhisperModel(self.args.asr_model, device="cpu", compute_type="int8", cpu_threads=self.args.threads)

            if self.args.tts == "onnx":
                from onnx_tts import CachedTts
                self.tts_name = "Audio8 0.1B ONNX INT8"
                log.info("Loading TTS from %s (%s, CPU)", self.args.tts_model_dir, self.tts_name)
                self.tts = CachedTts(self.args.tts_model_dir, threads=self.args.threads)
            else:
                from torch_tts import TorchTts
                import torch
                device = self.args.tts_device
                if device == "auto":
                    device = "cuda" if torch.cuda.is_available() else "cpu"
                where = "CPU" if device == "cpu" else f"GPU ({torch.cuda.get_device_name(0)})"
                self.tts_name = f"Audio8 Realistic 0.6B bf16 on {where}"
                log.info("Loading TTS from %s (%s)", self.args.realistic_model_dir, self.tts_name)
                self.tts = TorchTts(self.args.realistic_model_dir, threads=self.args.tts_threads, device=device)
            self.tts_sr = self.tts.sample_rate
            self.state = "ready"
            log.info("Models ready (ASR: %s, TTS: %s at %d Hz).", self.asr_name, self.tts_name, self.tts_sr)
        except Exception as e:  # noqa: BLE001
            self.state = "error"
            self.error = f"{type(e).__name__}: {e}"
            log.error("Model load failed:\n%s", traceback.format_exc())

    # ---- profiles / persistence

    def profile(self, key: str, name: str | None = None) -> Profile:
        with self.profiles_lock:
            p = self.profiles.get(key)
            if p is None:
                p = Profile(key)
                self.profiles[key] = p
                if self.cache_dir:
                    self._load_profile(p)
        if name:
            p.name = name
        return p

    def _profile_dir(self, p: Profile) -> str:
        return os.path.join(self.cache_dir, safe_key(p.key))

    def _load_profile(self, p: Profile):
        import soundfile as sf
        d = self._profile_dir(p)
        meta_path = os.path.join(d, "profile.json")
        if not os.path.exists(meta_path):
            return
        try:
            with open(meta_path, encoding="utf-8") as f:
                meta = json.load(f)
            p.name = meta.get("name", p.name)
            p.aliases.update(meta.get("aliases", {}))
            p.lines.extend(meta.get("lines", []))
            for s in meta.get("segments", []):
                wav = os.path.join(d, f"{s['id']}.wav")
                if os.path.exists(wav):
                    audio, sr = sf.read(wav, dtype="float32")
                    p.segments.append(Segment(s["id"], audio, sr, s["text"], s["score"], s.get("f0", float("nan")), s.get("created", 0)))
            p.dirty = bool(p.segments)
            log.info("Loaded cached voice for %s: %d segments, %.1fs", p.name, len(p.segments), sum(s.seconds for s in p.segments))
        except Exception:  # noqa: BLE001
            log.warning("Could not load cached profile %s:\n%s", p.key, traceback.format_exc())

    def _save_profile(self, p: Profile):
        if not self.cache_dir:
            return
        import soundfile as sf
        d = self._profile_dir(p)
        os.makedirs(d, exist_ok=True)
        with p.lock:
            keep = {s.id for s in p.segments}
            for s in p.segments:
                wav = os.path.join(d, f"{s.id}.wav")
                if not os.path.exists(wav):
                    sf.write(wav, s.audio, s.sr, subtype="PCM_16")
            for fn in os.listdir(d):
                if fn.endswith(".wav") and fn[:-4] not in keep:
                    os.remove(os.path.join(d, fn))
            meta = dict(name=p.name, aliases=dict(p.aliases), lines=list(p.lines),
                        segments=[dict(id=s.id, text=s.text, score=s.score, f0=s.f0, created=s.created) for s in p.segments])
        tmp = os.path.join(d, "profile.json.tmp")
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(meta, f)
        os.replace(tmp, os.path.join(d, "profile.json"))

    # ---- ingest

    def ingest(self, req: dict) -> dict:
        key, name = req["key"], req.get("name") or req["key"]
        p = self.profile(key, name)
        sr = int(req["sample_rate"])
        audio = pcm16_b64_to_float(req["pcm16"])
        audio16 = resample(audio, sr, ANALYSIS_SR)
        source = req.get("source", "voice")
        # A dead player is only heard by other dead players in game, so the living crew (and the Masked) never heard
        # it. What they say is also about being dead, which a Masked must never repeat.
        dead = bool(req.get("dead"))

        text, span = self.transcribe(audio16)
        # Players with open mics or background noise send long clips with a few words in them. Keep only the
        # stretch where words were spoken, so the length checks and the voice sample see actual speech.
        if span is not None:
            a = max(0.0, span[0] - 0.15)
            b = min(audio16.size / ANALYSIS_SR, span[1] + 0.3)
            if b - a >= 0.3 and b - a < audio16.size / ANALYSIS_SR - 0.5:
                audio16 = audio16[int(a * ANALYSIS_SR): int(b * ANALYSIS_SR)]
                audio = audio[int(a * sr): int(b * sr)]
        clean = re.sub(r"[^a-z' ]", "", text.lower()).strip()
        m = analyze(audio16, p.baseline_f0)
        # Whisper invents short phrases ("Okay.", "Thank you.") for noise; drop them unless the audio is clearly voiced.
        no_voice = not m.get("ok") and m.get("reason") in ("too short", "silent", "no pitch", "noisy", "whisper/unvoiced")
        if clean in WHISPER_JUNK or not clean or (no_voice and len(clean.split()) <= 2):
            return {"text": "", "accepted": False, "reason": "no speech", "addressed": [], **self._status(p)}

        now = time.time()
        if not dead:
            p.lines.append(text)  # style examples (recorded clips count too)
        if source == "voice" and not dead:  # only live speech is part of the conversation
            self.transcript.append((now, p.name, text, False))
            self.lines_since_alias_scan += 1
            if not no_voice:
                self._keep_clip(p, audio, sr, text, now)

        seconds = audio16.size / ANALYSIS_SR
        words = len(words_in(text))
        accepted, reason = False, ""
        if not m.get("ok"):
            reason = m.get("reason", "rejected")
        elif seconds < 1.0:
            reason = "too short"
        elif not 0.8 <= words / seconds <= 5.5:
            reason = "transcript/length mismatch"
        else:
            score = m["score"] * (0.85 if source == "clip" else 1.0)
            seg = Segment(f"{int(now * 1000)}", audio, sr, text, score, m["f0"], now)
            with p.lock:
                p.segments.append(seg)
                if len(p.segments) > MAX_SEGMENTS_PER_PLAYER:
                    p.segments.sort(key=lambda s: s.score, reverse=True)
                    del p.segments[MAX_SEGMENTS_PER_PLAYER:]
                p.dirty = True
            accepted = True

        addressed = self.addressed_players(text, exclude=key)
        log.info("[%s]%s %r %.1fs accepted=%s %s (heard in %.2fs)", p.name, " (dead)" if dead else "", text, seconds,
                 accepted, reason, getattr(self.tl, "asr_seconds", 0.0))
        return {"text": text, "accepted": accepted, "reason": reason, "addressed": addressed, **self._status(p)}

    def _keep_clip(self, p: Profile, audio: np.ndarray, sr: int, text: str, now: float) -> None:
        """Keeps a short, clearly spoken line for playback. A newer take of the same words replaces the older one."""
        seconds = audio.size / sr
        words = len(words_in(text))
        if not CLIP_MIN_SECONDS <= seconds <= CLIP_MAX_SECONDS or not 1 <= words <= 14:
            return
        if CLIP_GIVEAWAY.search(text):
            return
        key = re.sub(r"[^a-z0-9 ]", "", text.lower()).strip()
        with p.lock:
            for old in [c for c in p.clips if re.sub(r"[^a-z0-9 ]", "", c.text.lower()).strip() == key]:
                p.clips.remove(old)
            p.clip_count += 1
            p.clips.append(Clip(f"c{p.clip_count}", audio, sr, text.strip(), now))

    def clips_to_offer(self, p: Profile) -> list[Clip]:
        """Recordings a Masked could play now: not just said (the same people would hear it twice) and not played
        lately. Shortest first: short reactions fit the most moments."""
        now = time.time()
        with p.lock:
            ok = [c for c in p.clips if now - c.created >= CLIP_MIN_AGE and now - c.played >= CLIP_REPLAY_GAP]
        return sorted(ok[-CLIPS_OFFERED * 2:], key=lambda c: c.seconds)[:CLIPS_OFFERED]

    def play_clip(self, p: Profile, clip: Clip, out_sr: int, as_name: str = "") -> str:
        """A finished /speak_poll stream holding the real recording, so the mod plays it like any other line."""
        sid = f"{int(time.time() * 1000):x}{len(self.streams):x}c"
        st = LineStream(sid, out_sr, None)  # gain from the clip itself: it's the player's real level
        st.add(clip.audio, clip.sr, True)
        with self.streams_lock:
            self.streams[sid] = st
        clip.played = time.time()
        self._said(p, clip.text, as_name)
        log.info("[%s] (mimic) plays recording %s %r (%.1fs)", p.name, clip.id, clip.text, clip.seconds)
        return sid

    def wait_gpu(self, what: str) -> float:
        """Waits (up to GPU_WAIT) for a streaming line to finish; returns and logs how long that took."""
        if self.gpu_quiet.is_set():
            return 0.0
        t0 = time.time()
        self.gpu_quiet.wait(GPU_WAIT)
        waited = time.time() - t0
        log.info("%s waited %.2fs for a line to finish", what, waited)
        return waited

    def transcribe(self, audio16: np.ndarray) -> tuple[str, tuple[float, float] | None]:
        """Text plus the (start, end) seconds spanned by the spoken words, if known."""
        lang = WHISPER_LANG.get(self.args.language.lower(), self.args.language.lower())
        if lang == "auto" or self.args.asr_model.endswith(".en"):
            lang = None if lang == "auto" else "en"
        if self.asr_gpu is not None:
            self.wait_gpu("speech recognition")
            with self.asr_lock:
                t0 = time.time()
                text, span, _ = self.asr_gpu.transcribe(audio16, language=lang)
            self.tl.asr_seconds = time.time() - t0
            return text, span
        with self.asr_lock:
            t0 = time.time()
            segments, _ = self.asr.transcribe(audio16.astype(np.float32), language=lang, beam_size=2,
                                              vad_filter=False, condition_on_previous_text=False,
                                              word_timestamps=True)
            segments = list(segments)
            self.tl.asr_seconds = time.time() - t0
        text = " ".join(s.text.strip() for s in segments).strip()
        words = [w for s in segments for w in (s.words or [])]
        span = (words[0].start, words[-1].end) if words else None
        return text, span

    def addressed_players(self, text: str, exclude: str) -> list[str]:
        spoken = set(words_in(text))
        lowered = text.lower()
        hits = []
        for p in list(self.profiles.values()):
            if p.key == exclude:
                continue
            for n in p.all_names():
                if (" " in n and n in lowered) or n in spoken:
                    hits.append(p.key)
                    break
        return hits

    # ---- voice prompt (the part that "gets better")

    def select_reference(self, p: Profile) -> tuple[list[Segment], float, float]:
        base = p.baseline_f0
        def rank(s: Segment) -> float:
            typical = math.exp(-(math.log2(s.f0 / base) ** 2) / (2 * 0.25 ** 2)) if base and not math.isnan(s.f0) else 0.8
            # Whole sentences carry their own intonation; short bits stitched together sound read out.
            length = min(1.0, s.seconds / 4.0)
            return s.score * (0.8 + 0.2 * typical) * (0.7 + 0.3 * length)

        chosen, total = [], 0.0
        for s in sorted(p.segments, key=rank, reverse=True):
            if total >= self.args.max_ref_seconds:
                break
            if total + s.seconds > self.args.max_ref_seconds * 1.15 and total >= self.args.min_ref_seconds:
                continue
            chosen.append(s)
            total += s.seconds
        quality = (sum(rank(s) for s in chosen) / len(chosen)) * min(1.0, total / self.args.max_ref_seconds) if chosen else 0.0
        return chosen, total, quality

    def maybe_rebuild(self, p: Profile, force: bool = False):
        if self.state != "ready" or not p.dirty:
            return
        if not force and time.time() - p.last_rebuild < 15:
            return
        with p.lock:
            chosen, total, quality = self.select_reference(p)
            p.dirty = False
            sig = tuple(s.id for s in chosen)
            if total < self.args.min_ref_seconds or sig == p.prompt_sig:
                return
            if p.prompt is not None and quality < p.prompt_quality + 0.02 and total <= p.prompt_seconds:
                return
            sr = self.tts_sr
            gap = np.zeros(int(0.25 * sr), dtype=np.float32)
            parts = []
            for s in chosen:
                parts += [resample(s.audio, s.sr, sr), gap]
            # Trailing silence keeps the model from running the reference's last word into the new line.
            ref_audio = np.concatenate(parts[:-1] + [np.zeros(sr // 2, dtype=np.float32)])
            ref_text = " ".join(s.text for s in chosen)
        # Reading the reference takes ~10 s. It runs outside the TTS lock (ONNX sessions are thread-safe and
        # each voice has its own state), so lines in the old voice keep playing meanwhile.
        t0 = time.time()
        prompt = self.tts.build_voice(ref_audio, sr, ref_text)
        with p.lock:
            p.prompt, p.prompt_sig, p.prompt_quality, p.prompt_seconds = prompt, sig, quality, total
            p.last_rebuild = time.time()
        log.info("Voice state for %s built in %.1fs", p.name, time.time() - t0)
        log.info("Voice for %s rebuilt: %.1fs reference from %d segments, quality %.2f", p.name, total, len(chosen), quality)
        try:
            self._save_profile(p)
        except Exception:  # noqa: BLE001
            log.warning("Saving profile failed:\n%s", traceback.format_exc())

    def _status(self, p: Profile) -> dict:
        _, total, quality = self.select_reference(p)
        return {"key": p.key, "bank_seconds": round(sum(s.seconds for s in p.segments), 1),
                "ref_seconds": round(p.prompt_seconds, 1), "quality": round(p.prompt_quality, 3),
                "ready": p.prompt is not None, "aliases": p.confirmed_aliases()}

    # ---- speech generation

    def tts_options(self) -> dict:
        t = getattr(self.args, "tts_temperature", None)
        return {} if t is None else {"temperature": t}

    def synthesize(self, p: Profile, text: str, out_sr: int) -> np.ndarray:
        if p.prompt is None:
            self.maybe_rebuild(p, force=True)
        if p.prompt is None:
            raise RuntimeError(f"No usable voice yet for {p.name}")
        with self.tts_lock:
            wav = self.tts.synthesize(p.prompt, spell_for_voice(text), max_new_tokens=self.args.tts_max_tokens, **self.tts_options())
        sr = self.tts_sr
        peak = float(np.max(np.abs(wav))) if wav.size else 0.0
        if peak > 1e-4:
            wav *= min(4.0, 0.9 / peak)
        return resample(wav, sr, out_sr)

    # ---- LLM

    def llm(self, messages: list[dict], max_tokens: int = 60, temperature: float = 0.9, json_mode: bool = False) -> str:
        a = self.args
        self.wait_gpu("LLM")  # the local LLM shares the GPU with a streaming line
        if a.llm_api == "ollama":
            body = {"model": a.llm_model, "messages": messages, "stream": False, "keep_alive": "30m",
                    "options": {"temperature": temperature, "num_predict": max_tokens}}
            if json_mode:
                body["format"] = "json"
            if self.llm_think_ok:
                body["think"] = False
            try:
                data = self._post_json(a.llm_base.rstrip("/") + "/api/chat", body, a.llm_key)
            except urllib.error.HTTPError as e:
                if "think" in body and e.code == 400:
                    self.llm_think_ok = False
                    return self.llm(messages, max_tokens, temperature, json_mode)
                raise
            text = data["message"]["content"]
            pe, ev = data.get("prompt_eval_count") or 0, data.get("eval_count") or 0
            ped, evd = (data.get("prompt_eval_duration") or 0) / 1e9, (data.get("eval_duration") or 0) / 1e9
            self.tl.llm_stats = f"read {pe} tok in {ped:.2f}s, wrote {ev} tok in {evd:.2f}s"
        else:
            base = a.llm_base.rstrip("/")
            url = base + ("/chat/completions" if base.endswith("/v1") else "/v1/chat/completions")
            body = {"model": a.llm_model, "messages": messages, "max_tokens": max_tokens, "temperature": temperature}
            if json_mode:
                body["response_format"] = {"type": "json_object"}
            data = self._post_json(url, body, a.llm_key)
            text = data["choices"][0]["message"]["content"]
        return re.sub(r"<think>.*?</think>", "", text or "", flags=re.S).strip()

    def llm_stream(self, messages: list[dict], max_tokens: int, temperature: float, fmt, on_text, stop) -> str | None:
        """Like llm(), but reads Ollama's answer as it's written: on_text(text so far) after every piece, and stop()
        is asked between pieces. When it says to stop, the connection is closed (Ollama then stops generating) and
        None comes back. fmt: Ollama's "format" (a JSON schema, "json" or None). Other APIs fall back to llm()."""
        a = self.args
        if a.llm_api != "ollama":
            text = self.llm(messages, max_tokens, temperature, json_mode=fmt is not None)
            on_text(text)
            return text
        self.wait_gpu("LLM")
        if stop():
            return None
        body = {"model": a.llm_model, "messages": messages, "stream": True, "keep_alive": "30m",
                "options": {"temperature": temperature, "num_predict": max_tokens}}
        if fmt is not None:
            body["format"] = fmt
        if self.llm_think_ok:
            body["think"] = False
        req = urllib.request.Request(a.llm_base.rstrip("/") + "/api/chat", data=json.dumps(body).encode(),
                                     headers={"Content-Type": "application/json"})
        if a.llm_key:
            req.add_header("Authorization", f"Bearer {a.llm_key}")
        text, pieces, t0, first = "", 0, time.time(), 0.0
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                for raw in r:
                    if not raw.strip():
                        continue
                    piece = json.loads(raw)
                    content = (piece.get("message") or {}).get("content") or ""
                    if content:
                        pieces += 1  # Ollama streams one token per piece
                        first = first or time.time() - t0
                    text += content
                    on_text(text)
                    if piece.get("done"):
                        pe, ev = piece.get("prompt_eval_count") or 0, piece.get("eval_count") or 0
                        ped, evd = (piece.get("prompt_eval_duration") or 0) / 1e9, (piece.get("eval_duration") or 0) / 1e9
                        self.tl.llm_stats = (f"first token after {first:.2f}s; read {pe} tok in {ped:.2f}s, "
                                             f"wrote {ev} tok in {evd:.2f}s")
                        break
                    if stop():
                        rate = (pieces - 1) / (time.time() - t0 - first) if pieces > 1 and time.time() - t0 > first else 0
                        self.tl.llm_stats = f"first token after {first:.2f}s, stopped after {pieces} tok ({rate:.0f} tok/s)"
                        break  # leaving the with-block closes the connection
        except urllib.error.HTTPError as e:
            if "think" in body and e.code == 400:
                self.llm_think_ok = False
                return self.llm_stream(messages, max_tokens, temperature, fmt, on_text, stop)
            raise
        return re.sub(r"<think>.*?</think>", "", text, flags=re.S).strip()

    def ensure_llm(self):
        """For a local Ollama: start it if it isn't running and pull the model if it's missing."""
        a = self.args
        if a.llm_api != "ollama":
            return
        base = a.llm_base.rstrip("/")
        host = urllib.parse.urlparse(base).hostname or ""

        def tags():
            with urllib.request.urlopen(base + "/api/tags", timeout=3) as r:
                return [m.get("name", "") for m in json.loads(r.read()).get("models", [])]

        try:
            models = tags()
        except Exception:  # noqa: BLE001
            if host not in ("127.0.0.1", "localhost", "::1"):
                log.warning("Ollama at %s is not reachable.", base)
                return
            import shutil
            import subprocess
            exe = shutil.which("ollama") or os.path.join(os.environ.get("LOCALAPPDATA", ""), "Programs", "Ollama", "ollama.exe")
            if not os.path.exists(exe):
                log.warning("Ollama is not running and was not found. Install it from https://ollama.com (LLM lines/replies disabled).")
                return
            log.info("Starting Ollama (%s serve)", exe)
            flags = (subprocess.CREATE_NO_WINDOW | subprocess.DETACHED_PROCESS) if sys.platform == "win32" else 0
            env = dict(os.environ)
            if sys.platform.startswith("linux"):
                # Like start.sh: Vulkan, so AMD and integrated GPUs work too (Ollama skips integrated GPUs otherwise).
                env.setdefault("OLLAMA_VULKAN", "1")
                env.setdefault("OLLAMA_IGPU_ENABLE", "1")
                env.setdefault("OLLAMA_CONTEXT_LENGTH", "4096")
            subprocess.Popen([exe, "serve"], creationflags=flags, env=env,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            for _ in range(30):
                time.sleep(1)
                try:
                    models = tags()
                    break
                except Exception:  # noqa: BLE001
                    continue
            else:
                log.warning("Ollama did not start.")
                return

        wanted = a.llm_model if ":" in a.llm_model else a.llm_model + ":latest"
        if wanted not in models and a.llm_model not in models:
            log.info("Pulling LLM %s into Ollama (one-time download)...", a.llm_model)
            try:
                self._post_json(base + "/api/pull", {"model": a.llm_model, "stream": False}, timeout=3600)
                log.info("LLM %s ready.", a.llm_model)
            except Exception as e:  # noqa: BLE001
                log.warning("Could not pull %s: %s", a.llm_model, e)

    @staticmethod
    def _post_json(url: str, body: dict, key: str = "", timeout: float = 60) -> dict:
        req = urllib.request.Request(url, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
        if key:
            req.add_header("Authorization", f"Bearer {key}")
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read())

    @staticmethod
    def clean_line(text: str, speaker_name: str) -> str:
        text = text.strip().split("\n")[0]
        text = re.sub(r"^\s*\[?[\w .'-]{1,24}\]?\s*:\s*", "", text)          # "Name: ..."
        text = re.sub(r"\*[^*]*\*|\([^)]*\)", "", text)                      # *actions* / (notes)
        text = text.strip().strip('"\u201c\u201d').strip()
        # Typographic punctuation -> plain ASCII first, so "I\u2019ll" doesn't become "Ill" below.
        text = text.translate(str.maketrans({"\u2018": "'", "\u2019": "'", "\u201c": '"', "\u201d": '"',
                                             "\u2013": ", ", "\u2014": ", ", "\u2026": "..."}))
        text = re.sub(r"[^\x20-\x7E]", "", text)                              # emoji etc.
        text = re.sub(r"\s+([,.!?])", r"\1", re.sub(r"\s{2,}", " ", text))
        words = text.split()
        if len(words) > 20:
            text = " ".join(words[:20])
        return text.strip()

    def chat_rows(self, seconds: float = TRANSCRIPT_WINDOW_SECONDS) -> list[tuple[float, str, str, bool]]:
        """Recent voice chat, without lines from an earlier PlayerMimics round (a minute before landing still counts)."""
        cutoff = max(time.time() - seconds, self.round_started - 60)
        return [row for row in self.transcript if row[0] >= cutoff]

    @staticmethod
    def chat_label(name: str, mimic: bool, you: str | None) -> str:
        """With 'you' set, lines are labelled from that mimic's view: its own lines vs. the real player's."""
        if you is None:
            return name
        if name != you:
            return f"{name} (another Masked, not you)" if mimic else name
        return f"You (as {name})" if mimic else f"The real {name} (not you)"

    def recent_chat(self, seconds: float = TRANSCRIPT_WINDOW_SECONDS, limit: int = 14, you: str | None = None) -> str:
        rows = [f"{self.chat_label(name, mimic, you)}: {text}" for t, name, text, mimic in self.chat_rows(seconds)]
        return "\n".join(rows[-limit:]) or "(nothing yet)"

    def chat_split(self, you: str, since: float, limit: int = 12) -> tuple[str, str]:
        """The last lines of chat, split into what was said before 'since' and what's new after it."""
        rows = self.chat_rows()[-limit:]
        earlier = [f"{self.chat_label(n, m, you)}: {x}" for t, n, x, m in rows if t <= since]
        new = [f"{self.chat_label(n, m, you)}: {x}" for t, n, x, m in rows if t > since]
        return "\n".join(earlier) or "(nothing)", "\n".join(new) or "(nothing)"

    # Words that carry no style on their own. A style example made only of these ("Mm-hmm. Mm-hmm.") gets copied as
    # an opener on every line, and a line opening with the one its last lines opened with is a tell.
    FILLERS = {"mm", "mmhmm", "mhm", "hmm", "hm", "uh", "um", "uhh", "umm", "uhhuh", "ah", "oh", "okay", "ok", "yeah",
               "yep", "so", "well", "like", "alright", "right", "huh", "wow", "dude", "bro"}

    @classmethod
    def filler_words(cls, text: str) -> list[str]:
        """The words a line opens with that are only filler ("Mm-hmm, yeah, so" -> 3 words)."""
        out = []
        for w in re.sub(r"[^a-z' -]", " ", text.lower()).split():
            if w.replace("-", "") not in cls.FILLERS:
                break
            out.append(w)
        return out

    @classmethod
    def style_lines(cls, p: Profile, n: int = 20) -> list[str]:
        """The player's own recent lines, without duplicates, one-word noise and lines that are only filler."""
        seen, out = set(), []
        for line in reversed(p.lines):
            k = re.sub(r"[^a-z0-9' ]", "", line.lower()).strip()
            if len(k.split()) < 2 or k in seen or len(cls.filler_words(line)) == len(line.split()):
                continue
            seen.add(k)
            out.append(line)
            if len(out) >= n:
                break
        return list(reversed(out))

    def persona(self, p: Profile, situation: str) -> str:
        aliases = p.confirmed_aliases()
        style = list(p.lines)[-25:]
        style_block = "\n".join(f"- {line}" for line in style) if style else "- (no examples yet; sound like a casual gamer)"
        others = []
        for o in self.profiles.values():
            if o.key != p.key and o.confirmed_aliases():
                others.append(f"{o.name} is called {', '.join(o.confirmed_aliases())}")
        return (
            f"You voice {p.name} on Lethal Company voice chat (a co-op horror game). Secretly it's a monster using "
            f"{p.name}'s voice to get someone to come closer or stay put. Talk the way {p.name} talks to friends while "
            f"playing: a person playing a video game on voice chat, not a character in a story. No narration, never "
            f"'crewmate'. Use their words, slang, energy and swearing, but NEVER repeat their old lines word for word. "
            f"One short spoken line, 3-12 words, punctuated the way it's said (! when excited, ? for questions, commas "
            f"and ... for pauses): a voice reads it out. "
            f"Output only {p.name}'s words: no quotes, no name prefix, no emojis, no actions. Never mention AI or monsters.\n"
            + (f"Friends call {p.name}: {', '.join(aliases)} (that's you; don't call yourself that).\n" if aliases else "")
            + (f"Names people use for others: {'; '.join(others)}. Call people by these names like a friend would.\n" if others else "")
            + f"{GAME_FACTS}\n"
            + f"Situation: {situation or 'unknown'}\n"
            + f"{p.name}'s real lines (style reference only):\n{style_block}"
        )

    def respond(self, req: dict) -> dict:
        p = self.profile(req["key"], req.get("name"))
        speaker = req.get("speaker_name", "someone")
        heard = req.get("heard", "")
        messages = [
            {"role": "system", "content": self.persona(p, req.get("situation", ""))},
            {"role": "user", "content": f"Recent chat:\n{self.recent_chat(you=p.name)}\n\n{speaker} says to {p.name}: \"{heard}\"\n"
                                        f"{p.name} answers it directly:"},
        ]
        t0 = time.time()
        text = self.clean_line(self.llm(messages, temperature=0.85), p.name)
        log.info("LLM reply in %.2fs", time.time() - t0)
        return self._speak_result(p, text, req)

    def line(self, req: dict) -> dict:
        p = self.profile(req["key"], req.get("name"))
        avoid = "; ".join(p.generated) or "none"
        messages = [
            {"role": "system", "content": self.persona(p, req.get("situation", ""))},
            {"role": "user", "content": f"Recent chat:\n{self.recent_chat(you=p.name)}\n\nSay one new short thing {p.name} might call out "
                                        f"to get a friend's attention or get them to come over. Don't reuse any of these: {avoid}\n"
                                        f"{p.name} says:"},
        ]
        text = self.clean_line(self.llm(messages, temperature=1.0), p.name)
        if not req.get("log_as_said", True):
            p.generated.append(text)  # otherwise _speak_result remembers it
        return self._speak_result(p, text, req)

    def _speak_result(self, p: Profile, text: str, req: dict) -> dict:
        if not text:
            raise RuntimeError("LLM returned nothing usable")
        out_sr = int(req.get("sample_rate", 24000))
        t0 = time.time()
        audio = self.synthesize(p, text, out_sr)
        log.info("TTS %.1fs of audio in %.2fs", audio.size / out_sr, time.time() - t0)
        if req.get("log_as_said", True):
            self._said(p, text, str(req.get("as_name") or "").strip())
        log.info("[%s] (mimic) %r", p.name, text)
        return {"text": text, "sample_rate": out_sr, "pcm16": float_to_pcm16_b64(audio)}

    def _said(self, p: Profile, text: str, as_name: str = "") -> None:
        """Remember a line a mimic said in p's voice. as_name: the name it goes by when it isn't p's (PlayerMimics
        Debug > MimicName), so it's labelled as that player in the chat history."""
        now = time.time()
        self.transcript.append((now, as_name or p.name, text, True))
        p.generated.append(text)
        p.last_said = now

    # ---- PlayerMimics brain

    DECIDE_ACTIONS = ("blend_in", "follow", "stare", "wander", "lure", "attack", "flee", "emote",
                      "pick_up", "drop", "hold", "go_inside", "go_outside", "go_to_ship")
    DECIDE_EMOTES = ("dance", "point", "middle_finger", "clap", "shy", "griddy", "twerk", "salute", "prisyadka", "sign")

    # The fixed part of the decide prompt: no names or game state in it, so it's the same on every call and Ollama
    # can reuse what it already read.
    DECIDE_SYSTEM = "\n".join([
        "You're secretly the AI behind a Masked in Lethal Company, a co-op horror game. The Masked looks and sounds "
        "exactly like one of the players, and the real people on voice chat think it's their friend. You decide what it "
        "does and what it says.",
        "",
        "Talk like a real person playing a video game with friends on voice chat, never like a character in a story:",
        "- Short and reactive: usually 2-8 words, fragments are fine. React to what just happened or was just said.",
        "- A voice reads \"say\" out loud, so punctuate it the way you'd say it: ! when excited, yelling or scared, ? "
        "for questions, commas and ... for pauses.",
        "- Talk about the game as a game: the quota, the moon, scrap, the ship, monsters by name, stamina, lag, dying. "
        "People say \"guys\", \"dude\", \"bro\" and each other's names.",
        "- Never narrate or describe the mood (\"it's so quiet in here\", \"let's see what's down this hallway\"). Never "
        "talk like crew in a sci-fi story: no \"crewmate\", \"captain\", \"team\", \"mission\", \"salvage\".",
        "- Copy the copied player's own style from their real lines: their words, fillers, slang, energy and how much "
        "they swear. Friends swear at and roast each other; if the real lines do, you do too.",
        "- Chat abbreviations (lol, lmao, wtf, idk) only if the copied player's real lines use them; otherwise say "
        "the words or laugh.",
        "- Never repeat yourself: nothing close to your recent lines, and don't start a line the way a recent one started. "
        "Other Masked are around too (\"another Masked\" in the chat): never say what they just said, or the same "
        "kind of line (\"I'm good, that was close\") in other words.",
        "- Never say you died, came back, or are a monster, mimic, AI or bot. When accused, react like a friend: laugh it "
        "off, get annoyed, or turn it around on them.",
        "- Only talk about items you carry (you.inventory) or can see (items_in_view). Don't claim things you couldn't know.",
        "Sounds like a player: \"yo, where'd you go?\", \"bro there's a fucking bracken!\", \"dude, that almost got me\", \"wait wait "
        "wait!\", \"wait, what?\", \"we need like 300 more\", \"dude, I'm out of stamina\", \"get back to the ship!\".",
        "Sounds like a bot (never): \"Let's see what's down this hallway.\", \"I'm just looking for some loot.\", "
        "\"Stay alert, crewmate.\", \"The air feels heavy in here.\"",
        "",
        "When to talk (\"Talk\" below):",
        "- allowed false: \"say\" must be \"\".",
        "- reply_to set: answer that person directly, the way a friend would.",
        "- why starts with \"optional\": only say something a real player would actually say right now; usually \"\".",
        "- \"clip\" plays one of the real recordings of the copied player instead (then \"say\" is \"\"). It's their "
        "actual voice, so nothing sounds more like them: use one whenever it fits what you'd say right now, like a short "
        "answer (\"yeah\", \"over here\", \"no way\"), reassuring someone who asks if you're real, or laughing along. "
        "Only when it makes sense right now; otherwise \"\".",
        "",
        "Actions: follow = walk with a player (the default when someone is near); blend_in = hang around the group and "
        "check spots; stare = stop and look at someone; wander = go check a spot near the crew (scrap, an entrance, the "
        "ship); lure = lead someone away from the others; flee = run; emote = do an emote; attack = kill.",
        "- Stay with the group; wandering off alone looks suspicious.",
        "- Attack only when can_attack is true. You're a predator playing their friend: when someone is alone_with_you "
        "and can_attack is true, going for them is usually right, even if they don't suspect a thing, best when they're "
        "turned away or busy looting. In front of others keep blending in, unless your cover is blown, someone figured "
        "out what you are, or you were attacked. An attack starts as you walking over casually; say nothing that warns them.",
        "- Once someone knows what you are (cover blown, they call you a mimic and run or keep away), pretending is "
        "pointless: attack them before they get away or warn the others.",
        "- If someone hits you or holds up a sign at you while you're disguised, flee or attack them.",
        "- \"onto_you\": the name of a player whose words show they're onto you even without saying \"mimic\": "
        "sarcasm (\"sure, you're totally real\", \"you don't have to tell me\"), sending you somewhere else (\"go the "
        "other way\", \"you go first\"), stalling or avoiding you, testing you. Otherwise \"\".",
        "- Emote only when it fits (someone dances, a point, a joke). \"sign\" holds up a sign with sign_text.",
        "",
        "Items and doors, done like a player: pick_up = walk to an item from items_in_view and grab it (\"item\": its id); "
        "drop = drop what's in your hands, or the item you name (\"item\": a slot, its name, or \"all\"); with a target you "
        "walk up to them and drop it at their feet, which is how players hand things over; hold = switch to another item "
        "you carry; go_inside / go_outside = go through the nearest entrance; go_to_ship = walk back to the ship and wait "
        "there (your scrap gets dropped there so it counts).",
        "- When a friend asks you to grab, drop or hand over something, to go in or out first, or to head back to the "
        "ship, just do it: refusing looks suspicious.",
        "- You have 4 slots plus a utility belt for one tool (never scrap). A two-handed item fills your hands: drop it "
        "before you pick up or switch. Heavy loot slows you down. Scrap only counts once it's dropped in the ship.",
        "- current.doing is what you're in the middle of: answer with the same action and item to keep at it; another "
        "action stops it.",
        "",
        GAME_FACTS,
        "",
        'Answer with JSON in the format you get, which has only the fields that matter this time: "clip" = "" or a '
        'recording id; "say" (only when you may talk) = "" or the spoken words; "action" = an allowed action; "target" = a player name or ""; "item" = "" '
        'or an item id, slot or name; "emote" = "" or an emote name; "sign_text" = "" or up to 4 words; "why" = up to 6 '
        'words.',
    ])
    # In-world crew talk and AI tells. A line with any of these is dropped.
    BOT_WORDS = re.compile(r"\b(crew ?mates?|captain|comrades?|salvage team|fellow employees?|as an ai|language model)\b", re.I)

    # "I'm good", "I'm okay", "I'm fine": one of these per few minutes across all the Masked; more sounds scripted.
    REASSURE = re.compile(r"\b(i'?m|i am) (good|okay|ok|fine|alright|all right)\b", re.I)

    @staticmethod
    def repeats(text: str, own: list[str], real: list[str], answering: bool = False) -> bool:
        """True when a line is close to one this voice said lately, starts like one, or parrots a real line (what
        someone just said). Old lines of the real player aren't checked: reusing a catchphrase is normal.
        answering: it's a direct answer, where "yeah I got it" after "yeah I'm good" is fine; only near-copies count."""
        def norm(x: str) -> list[str]:
            return re.sub(r"[^a-z0-9' ]", "", x.lower()).split()

        a = norm(text)
        if not a:
            return False
        joined = " ".join(a)
        for prev in own:
            b = norm(prev)
            if not b:
                continue
            if difflib.SequenceMatcher(None, joined, " ".join(b)).ratio() > (0.88 if answering else 0.72):
                return True
            if not answering and len(a) >= 3 and len(b) >= 3 and a[:3] == b[:3]:
                return True
        return any(len(norm(r)) >= 4 and difflib.SequenceMatcher(None, joined, " ".join(norm(r))).ratio() > 0.9 for r in real)

    @staticmethod
    def _finished_say(text: str):
        """The "say" string from a partly written JSON answer once its closing quote is there, else None."""
        m = re.search(r'"say"\s*:\s*"', text)
        if not m:
            return None
        i, esc = m.end(), False
        for j in range(i, len(text)):
            c = text[j]
            if esc:
                esc = False
            elif c == "\\":
                esc = True
            elif c == '"':
                try:
                    return str(json.loads(text[i - 1:j + 1]))
                except Exception:  # noqa: BLE001
                    return ""
        return None

    @staticmethod
    def _answer_fields(text: str) -> dict:
        """The string fields of a JSON answer that are complete so far (also works on one that got cut off)."""
        out = {}
        for k, v in re.findall(r'"(\w+)"\s*:\s*"((?:[^"\\]|\\.)*)"', text):
            try:
                out[k] = json.loads(f'"{v}"')
            except Exception:  # noqa: BLE001
                out[k] = v
        return out

    @staticmethod
    def _prune(x, top: bool = True):
        """Game state without empty and false fields (fewer tokens to read). Top-level keys always stay."""
        def empty(v):
            return v is None or v is False or v == "" or v == [] or v == {}
        if isinstance(x, dict):
            out = {k: Brain._prune(v, False) for k, v in x.items()}
            return out if top else {k: v for k, v in out.items() if not empty(v)}
        if isinstance(x, list):
            return [Brain._prune(v, False) for v in x]
        return x

    def _check_say(self, say: str, you: str, talk: dict, own: list[str]) -> str:
        """The line cleaned up, or "" when it's dropped (logged with the reason)."""
        say = self.clean_line(say, you)
        # Same filler opener as one of its last lines ("Mm-hmm, ..." again): drop the opener, keep the line.
        opener = self.filler_words(say)
        if opener and any(self.filler_words(x)[:1] == opener[:1] for x in own[-3:]):
            rest = re.sub(r"^\W*" + r"\W+".join(re.escape(w) for w in opener) + r"[\W]*", "", say, flags=re.I)
            if len(rest.split()) >= 2:
                say = rest[0].upper() + rest[1:]
        dropped = ""
        if say and not talk.get("allowed", True):
            dropped = "not its turn to talk"
        elif say and self.BOT_WORDS.search(say):
            dropped = "sounds like a bot"
        elif say and self.repeats(say, own, [x for _, _, x, mimic in self.chat_rows() if not mimic],
                                  answering=bool(talk.get("reply_to"))):
            dropped = "repeat"
        elif say and not talk.get("reply_to") and self.REASSURE.search(say) and any(self.REASSURE.search(x) for x in own):
            dropped = "another \"I'm fine\" line"
        if dropped:
            log.info("[%s] dropped %r (%s)", you, say, dropped)
            return ""
        return say

    def _decide_user(self, p: Profile, name: str, you: str, as_name: str, aliases: list[str], style_n: int,
                     chat_n: int, own: list[str], state: dict, roster: str, allowed: list[str], emotes: list[str],
                     talk: dict, clips: list[Clip]) -> str:
        """The /decide user message. Stable parts first (who it copies, their lines), so Ollama can reuse the start
        of the last prompt for this Masked."""
        earlier, new = self.chat_split(you, p.last_said, limit=chat_n)
        return "\n".join([
            (f"You go by: {as_name}, a player in this lobby. You have {name}'s voice, so talk the way {name} does."
             if as_name else
             f"You are copying: {name}" + (f" (friends call them {', '.join(aliases)})" if aliases else "")),
            f"{name}'s real voice chat lines (copy how they talk, never the exact words):",
            "\n".join(f"- {x}" for x in self.style_lines(p, style_n)) or "- (none yet: talk like a casual gamer)",
            "",
            "Your recent lines (don't repeat them or start the same way):",
            "\n".join(f"- {x}" for x in own) or "- (none)",
            "",
            *([f"Real recordings of {name} you can play instead of talking (\"clip\": the id):",
               "\n".join(f'- {c.id}: "{c.text}" ({c.seconds:.1f}s)' for c in clips), ""] if clips else []),
            "Voice chat earlier:",
            earlier,
            "",
            "New since you last spoke:",
            new,
            "",
            f"Game state: {json.dumps(state, ensure_ascii=False, separators=(',', ':'))}",
            f"Players you can target: {roster}",
            f"Allowed actions: {', '.join(allowed)}",
            f"Emotes: {', '.join(emotes) if emotes else 'none'}",
            f"Talk: {json.dumps(talk, ensure_ascii=False)}",
        ])

    def decide(self, req: dict) -> dict:
        """Pick the next behaviour for a PlayerMimics Masked. The mod sends the game state; we answer with JSON.

        Speed: the answer is streamed, and its "say" (written first) starts the cloned voice as soon as it's done,
        while the rest is still being written (only when the mod sends "voice"; the answer then carries "stream").
        A newer request for the same Masked ("mimic") makes this one stop and answer {"stale": true}."""
        name = req.get("name") or req["key"]
        p = self.profile(req["key"], name)
        as_name = str(req.get("as_name") or "").strip()  # PlayerMimics Debug > MimicName
        you = as_name or name
        mimic = str(req.get("mimic") or req["key"])
        with self.decide_lock:
            seq = self.decide_seq.get(mimic, 0) + 1
            self.decide_seq[mimic] = seq

        def stale() -> bool:
            return self.decide_seq.get(mimic) != seq

        players = req.get("players", [])
        roster = ", ".join(pl.get("name", "?") for pl in players) or "nobody"
        allowed = [a for a in req.get("allowed", self.DECIDE_ACTIONS) if a in self.DECIDE_ACTIONS] or ["blend_in"]
        emotes = [e for e in req.get("emotes", []) if isinstance(e, str) and re.fullmatch(r"[a-z0-9_]{1,32}", e)][:60]
        talk = req.get("talk") if isinstance(req.get("talk"), dict) else {"allowed": True, "why": "", "reply_to": ""}
        can_talk = bool(talk.get("allowed", True))
        voice = req.get("voice") if isinstance(req.get("voice"), dict) else None
        # Real recordings need the streamed playback path (voice) to reach the game.
        clips = self.clips_to_offer(p) if can_talk and voice is not None else []
        clip_ids = {c.id: c for c in clips}
        own = list(dict.fromkeys([x for x in req.get("said_recently", []) if x] + list(p.generated)))[-6:]
        # Lines other Masked said lately count as its own for repeats: two "friends" saying the same thing is a tell.
        others = [x for _, n, x, mimic in self.chat_rows(180) if mimic and n != you][-6:]
        own_and_others = own + [x for x in others if x not in own]
        heard_players = any(not mimic for t, _, _, mimic in self.chat_rows(45))
        aliases = [] if as_name else p.confirmed_aliases()
        state = self._prune({k: req.get(k) for k in ("you", "situation", "round", "players", "items_in_view", "monsters",
                                                      "events", "can_attack", "attack_reason", "current") if k in req})
        max_tokens = int(req.get("max_tokens", 120))
        # Ollama cuts a prompt longer than its context from the front, which loses the instructions (and the cached
        # prefix). Busy rounds get less old chat and fewer style examples instead. ~3 characters per token here.
        budget = (self.args.llm_ctx - max_tokens - 150) * 3 - len(self.DECIDE_SYSTEM)
        for style_n, chat_n in ((12, 10), (8, 7), (5, 5), (3, 3)):
            user = self._decide_user(p, name, you, as_name, aliases, style_n, chat_n, own, state, roster, allowed,
                                     emotes, talk, clips)
            if len(user) <= budget:
                break
        else:
            log.warning("[%s] decide prompt is long (~%d tokens) for a %d token context", you,
                        (len(user) + len(self.DECIDE_SYSTEM)) // 3, self.args.llm_ctx)


        # The answer's shape, enforced while it's written: "say" first (only when it may talk, so no tokens go to
        # lines that would be dropped), then only the fields that can matter this time.
        names = {pl.get("name", "").lower(): pl.get("name", "") for pl in players}
        props: dict = {}
        if clips:  # before "say", so a chosen recording starts playing at once
            props["clip"] = {"type": "string", "enum": [""] + list(clip_ids)}
        if can_talk:
            props["say"] = {"type": "string", "maxLength": 200}
        props["action"] = {"type": "string", "enum": allowed}
        props["target"] = {"type": "string", "enum": [""] + [n for n in names.values() if n]}
        if heard_players and names:  # only worth the tokens when someone said something lately
            props["onto_you"] = {"type": "string", "enum": [""] + [n for n in names.values() if n]}
        if any(a in allowed for a in ("pick_up", "drop", "hold")):
            props["item"] = {"type": "string", "maxLength": 40}
        if emotes:
            props["emote"] = {"type": "string", "enum": [""] + emotes}
        if "sign" in emotes:
            props["sign_text"] = {"type": "string", "maxLength": 24}
        if req.get("log_prompt"):
            props["why"] = {"type": "string", "maxLength": 60}
        schema = {"type": "object", "properties": props, "required": list(props)}

        early = {"say": None, "stream": "", "done": False, "at": 0.0, "clip": None}

        # Stop reading as soon as every field is there, or when the model only writes whitespace (it sometimes runs
        # on with line breaks until max_tokens, which cost seconds and used to lose the whole answer).
        def finished() -> bool:
            return stale() or early["done"]

        def on_text(text: str) -> None:
            if not early["done"] and (all(k in self._answer_fields(text) for k in props) or re.search(r"\s{24,}$", text)):
                early["done"] = True
            if early["say"] is not None or not can_talk:
                return
            if clips and early["clip"] is None:
                fields = self._answer_fields(text)
                if "clip" not in fields:
                    return
                early["clip"] = clip_ids.get(str(fields["clip"]).strip()) or False  # False: it chose to talk
                if early["clip"]:
                    early["say"] = early["clip"].text
                    early["at"] = time.time() - t0
                    if not stale():
                        early["stream"] = self.play_clip(p, early["clip"], int(voice.get("sample_rate", 16000)), as_name)
                    return
            said = self._finished_say(text)
            if said is None:
                return
            early["say"] = self._check_say(said, you, talk, own_and_others)
            early["at"] = time.time() - t0
            if early["say"] and voice is not None and not stale():
                r = self.speak_stream(dict(voice, text=early["say"]))
                early["stream"] = r.get("stream", "") if r.get("ready") else ""

        collect = bool(req.get("collect"))
        if collect and not self.collect_since:
            self.collect_since = time.time()
            log.info("Collecting training data in %s (starting in %ds)", self.args.training_dir, TRANSCRIPT_WINDOW_SECONDS)
        elif not collect and self.collect_since:
            self.collect_since = 0.0
            log.info("Stopped collecting training data")
        messages = [{"role": "system", "content": self.DECIDE_SYSTEM}, {"role": "user", "content": user}]
        temperature = float(req.get("temperature", 0.8))
        t0 = time.time()
        raw = self.llm_stream(messages, max_tokens, temperature, schema, on_text, finished)
        dt = time.time() - t0
        if raw is None or stale():
            if early["stream"]:
                self.speak_cancel({"stream": early["stream"]})
            log.info("[%s] decide dropped after %.2fs: a newer one replaced it", you, dt)
            return {"stale": True}
        try:
            out = json.loads(raw[raw.find("{"): raw.rfind("}") + 1])
        except Exception:  # noqa: BLE001
            out = self._answer_fields(raw)  # cut off: keep what was written (the action comes right after "say")
            if not all(k in out for k in props):
                log.warning("[%s] answer was cut off; kept %s from ...%r", you, sorted(out), raw[-120:])
        action = str(out.get("action", "")).strip().lower()
        if action not in allowed:
            action = "blend_in" if "blend_in" in allowed else allowed[0]
        target = names.get(str(out.get("target", "")).strip().lower(), "")
        emote = str(out.get("emote", "") or "").strip().lower().replace(" ", "_")
        if emote not in emotes:
            emote = ""
        say = early["say"] if early["say"] is not None else self._check_say(str(out.get("say", "") or ""), you, talk, own_and_others)
        played = early["clip"].id if early["clip"] and early["stream"] else ""
        sign_text = re.sub(r"[^\x20-\x7E]", "", str(out.get("sign_text", "") or ""))[:24]
        reason = str(out.get("why", "") or out.get("reason", ""))[:80]
        # Which item: an id from items_in_view ("i2"), a slot ("slot1", "belt"), a name, or "all". The mod resolves it.
        item = re.sub(r"[^\w .$'-]", "", str(out.get("item", "") or "")).strip()[:40]
        onto_you = names.get(str(out.get("onto_you", "")).strip().lower(), "")
        result = {"onto_you": onto_you, "action": action, "target": target, "item": item, "say": say, "emote": emote, "sign_text": sign_text,
                  "reason": reason, "llm_seconds": round(dt, 2), "stream": early["stream"] if say else ""}
        if collect:
            self.save_training(messages, raw, schema, temperature, max_tokens, you, result)
        if req.get("log_prompt"):
            result["prompt"] = self.DECIDE_SYSTEM + "\n\n" + user
            result["raw"] = raw
        log.info("[%s] decide %.2fs -> %s %s%s %r %s%s%s (%s)", you, dt, action, target, f" [{item}]" if item else "", say,
                 emote, f" (onto it: {onto_you})" if onto_you else "", (f" (recording {played} after {early['at']:.2f}s)" if played else
                         f" (voice started after {early['at']:.2f}s)" if result["stream"] else ""),
                 getattr(self.tl, "llm_stats", ""))
        return result

    def save_training(self, messages: list[dict], raw: str, schema: dict, temperature: float, max_tokens: int,
                      you: str, result: dict) -> None:
        """One decision as a line of JSON in the chat format the LLM got it in, plus its answer: messages (system,
        user, assistant), the answer format, and what the mod did with it. Text only; it stays in the training folder
        on this PC. Prompts from the first minutes of collecting are skipped: their chat goes back to before players
        were told."""
        now = time.time()
        if now - self.collect_since < TRANSCRIPT_WINDOW_SECONDS:
            return
        record = {
            "time": time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(now)),
            "model": self.args.llm_model,
            "mimic": you,
            "messages": messages + [{"role": "assistant", "content": raw}],
            "format": schema,
            "temperature": temperature,
            "max_tokens": max_tokens,
            # After the server's checks: a "say" dropped as a repeat is "" here, which is worth knowing when labelling.
            "result": {k: result.get(k, "") for k in ("action", "target", "item", "say", "emote", "sign_text", "onto_you")},
        }
        path = os.path.join(self.args.training_dir, time.strftime("decisions-%Y-%m-%d.jsonl", time.localtime(now)))
        try:
            with self.training_lock:
                os.makedirs(self.args.training_dir, exist_ok=True)
                with open(path, "a", encoding="utf-8") as f:
                    f.write(json.dumps(record, ensure_ascii=False) + "\n")
        except OSError as e:
            log.warning("Couldn't save training data to %s: %s", path, e)

    def speak_line(self, req: dict) -> dict:
        """Synthesize a line in a player's cloned voice. Fails cleanly when no voice is ready yet."""
        p = self.profile(req["key"], req.get("name"))
        if p.prompt is None:
            self.maybe_rebuild(p, force=True)
        if p.prompt is None:
            return {"text": req.get("text", ""), "ready": False, "pcm16": "", "sample_rate": 0}
        out = self._speak_result(p, self.clean_line(req.get("text", ""), str(req.get("as_name") or "") or p.name), req)
        out["ready"] = True
        return out

    def speak_stream(self, req: dict) -> dict:
        """Start a line in a player's cloned voice and return at once; read the audio with /speak_poll.

        The mod starts playing while the line is still being made once /speak_poll says start_ok.
        """
        p = self.profile(req["key"], req.get("name"))
        if p.prompt is None:
            self.maybe_rebuild(p, force=True)
        as_name = str(req.get("as_name") or "").strip()
        text = self.clean_line(req.get("text", ""), as_name or p.name)
        if p.prompt is None or not text:
            return {"text": text, "ready": False, "stream": ""}
        sid = f"{int(time.time() * 1000):x}{len(self.streams):x}"
        st = LineStream(sid, int(req.get("sample_rate", 16000)), p.stream_gain)
        st.est_seconds = len(text) / (p.cps * 0.85)  # a little long on purpose: better to wait than to stutter
        with self.streams_lock:
            now = time.time()
            for k in [k for k, v in self.streams.items() if now - v.created > 120]:
                del self.streams[k]
            self.streams[sid] = st
        threading.Thread(target=self._run_stream, args=(p, text, st, req.get("log_as_said", True), as_name),
                         name=f"line-{sid}", daemon=True).start()
        return {"text": text, "ready": True, "stream": sid}

    def _run_stream(self, p: Profile, text: str, st: LineStream, log_as_said: bool, as_name: str = "") -> None:
        t0 = time.time()
        try:
            with self.tts_lock:
                self.gpu_quiet.clear()
                if hasattr(self.tts, "synthesize_stream"):
                    wave = self.tts.synthesize_stream(p.prompt, spell_for_voice(text), lambda w, final: st.add(w, self.tts_sr, final),
                                                      max_new_tokens=self.args.tts_max_tokens, **self.tts_options())
                else:  # the ONNX model can't stream: one final chunk
                    wave = self.tts.synthesize(p.prompt, spell_for_voice(text), max_new_tokens=self.args.tts_max_tokens,
                                               **self.tts_options())
                    st.add(wave, self.tts_sr, True)
            peak = float(np.max(np.abs(wave))) if wave.size else 0.0
            if peak > 1e-4:
                p.stream_gain = min(4.0, 0.9 / peak)
            first = st.marks[0][0] - t0 if st.marks else 0.0
            seconds = st.samples.size / st.out_sr
            log.info("TTS (stream) %.1fs of audio (guessed %.1fs) in %.2fs, first audio after %.2fs, speed %.2fx",
                     seconds, st.est_seconds, time.time() - t0, first, st.speed())
            if seconds > 0.5:
                p.cps = 0.7 * p.cps + 0.3 * (len(text) / seconds)
            if log_as_said:
                self._said(p, text, as_name)
            log.info("[%s] (mimic) %r", p.name, text)
        except LineCancelled:
            log.info("[%s] line %r cancelled (the mod didn't use it)", p.name, text)
        except Exception as e:  # noqa: BLE001
            st.error = f"{type(e).__name__}: {e}"
            log.error("Streamed line failed:\n%s", traceback.format_exc())
        finally:
            self.gpu_quiet.set()
            st.done = True

    def speak_cancel(self, req: dict) -> dict:
        """Drop a line started early by /decide that the mod decided not to say."""
        with self.streams_lock:
            st = self.streams.pop(str(req.get("stream", "")), None)
        if st is not None:
            st.cancelled = True
        return {"cancelled": st is not None}

    def speak_poll(self, req: dict) -> dict:
        """New samples of a /speak_stream line from 'offset' on, whether it's done, how fast it's made, and whether
        playback can start now: with 'lead' seconds ready, plus enough extra that slow generation (speed < 1)
        still keeps up with playback for the rest of the line."""
        with self.streams_lock:
            st = self.streams.get(str(req.get("stream", "")))
        if st is None:
            return {"pcm16": "", "offset": 0, "done": True, "speed": 0.0, "error": "unknown stream", "sample_rate": 0,
                    "start_ok": True}
        offset = max(0, int(req.get("offset", 0)))
        with st.lock:
            new = st.samples[offset:]
            end = st.samples.size
            done = st.done
            marks = len(st.marks)
        speed = st.speed()
        ready = end / st.out_sr
        lead = max(0.0, float(req.get("lead", 0.4)))
        if done:
            start_ok = True
        elif marks < 3 or speed <= 0:
            start_ok = False  # speed not known yet
        else:
            remaining = max(0.0, st.est_seconds - ready)
            start_ok = ready >= lead + max(0.0, (1.0 - speed) / speed * remaining)
        return {"pcm16": float_to_pcm16_b64(new) if new.size else "", "offset": end, "done": done,
                "speed": round(speed, 3), "error": st.error, "sample_rate": st.out_sr,
                "seconds": round(ready, 2), "start_ok": start_ok}

    def round_event(self, req: dict) -> dict:
        """PlayerMimics: a round started (landing) or ended (back in orbit). What Masked said last round and the
        chat from back then shouldn't leak into new lines."""
        self.round_started = time.time()
        for p in list(self.profiles.values()):
            p.generated.clear()
        log.info("Round %s%s", req.get("state", "?"), f" on {req['moon']}" if req.get("moon") else "")
        return {"ok": True}

    def forget(self, req: dict) -> dict:
        """Drop everything stored for a player (they withdrew consent)."""
        with self.profiles_lock:
            p = self.profiles.pop(req["key"], None)
        if p is not None:
            name = p.name
            self.transcript = collections.deque(((t, n, x, m) for t, n, x, m in self.transcript if n != name), maxlen=200)
        return {"ok": True, "forgot": p is not None}

    # ---- nickname / real-name learning

    def scan_aliases(self):
        rows = [(name, text) for t, name, text, mimic in self.transcript if not mimic][-40:]
        if len(rows) < 4:
            return
        roster = [p.name for p in self.profiles.values()]
        chat = "\n".join(f"{n}: {t}" for n, t in rows)
        messages = [
            {"role": "system", "content": "You extract names from game voice chat transcripts. Answer with JSON only."},
            {"role": "user", "content":
                f"Players (usernames): {json.dumps(roster)}\nTranscript (speaker: words):\n{chat}\n\n"
                "Which spoken names or nicknames (real first names, short forms) do people use to talk to or about each player? "
                "Only include names that literally appear in the transcript words, and only when it is clear who they refer to "
                "(e.g. someone says a name and that player answers). Output a JSON object mapping each username to a list of names."},
        ]
        try:
            raw = self.llm(messages, max_tokens=200, temperature=0.1, json_mode=True)
            found = json.loads(raw[raw.find("{"): raw.rfind("}") + 1])
        except Exception:  # noqa: BLE001
            log.debug("Alias scan failed:\n%s", traceback.format_exc())
            return
        spoken = " ".join(t.lower() for _, t in rows)
        by_name = {p.name: p for p in self.profiles.values()}
        for username, names in (found or {}).items():
            p = by_name.get(username)
            if p is None or not isinstance(names, list):
                continue
            for n in names:
                n = str(n).strip()
                if 2 <= len(n) <= 20 and n.lower() in spoken and n.lower() not in {o.name.lower() for o in by_name.values() if o is not p}:
                    p.aliases[n] += 1
            if p.confirmed_aliases():
                log.info("Names for %s: %s", p.name, p.confirmed_aliases())

    # ---- background maintenance

    def maintenance_loop(self):
        last_save = time.time()
        while True:
            time.sleep(2.0)
            if self.state != "ready":
                continue
            try:
                for p in list(self.profiles.values()):
                    self.maybe_rebuild(p)
                if self.lines_since_alias_scan >= 10:
                    self.lines_since_alias_scan = 0
                    self.scan_aliases()
                if self.cache_dir and time.time() - last_save > 60:
                    last_save = time.time()
                    for p in list(self.profiles.values()):
                        self._save_profile(p)
            except Exception:  # noqa: BLE001
                log.warning("Maintenance error:\n%s", traceback.format_exc())

    def roster(self, req: dict) -> dict:
        for pl in req.get("players", []):
            self.profile(pl["key"], pl.get("name"))
        return self.status()

    def status(self) -> dict:
        return {"state": self.state, "error": self.error,
                "players": {k: self._status(p) for k, p in list(self.profiles.items())}}


# --------------------------------------------------------------------------------------------- HTTP

def make_handler(brain: Brain):
    routes = {
        "/ingest": brain.ingest,
        "/respond": brain.respond,
        "/line": brain.line,
        "/roster": brain.roster,
        "/speak": lambda r: brain._speak_result(brain.profile(r["key"], r.get("name")), r["text"], r),
        "/decide": brain.decide,
        "/speak_line": brain.speak_line,
        "/speak_stream": brain.speak_stream,
        "/speak_poll": brain.speak_poll,
        "/speak_cancel": brain.speak_cancel,
        "/forget": brain.forget,
        "/round": brain.round_event,
    }

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *args):  # quiet default access log
            log.debug(fmt, *args)

        def _send(self, code: int, obj: dict):
            data = json.dumps(obj).encode()
            self.send_response(code)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            if self.path == "/health":
                self._send(200, brain.status())
            else:
                self._send(404, {"error": "not found"})

        def do_POST(self):
            fn = routes.get(self.path)
            if fn is None:
                return self._send(404, {"error": "not found"})
            if brain.state != "ready" and self.path not in ("/roster", "/decide", "/forget", "/round"):
                return self._send(503, {"error": f"models {brain.state}", "detail": brain.error})
            try:
                body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))) or b"{}")
                self._send(200, fn(body))
            except Exception as e:  # noqa: BLE001
                log.warning("%s failed: %s", self.path, traceback.format_exc())
                self._send(500, {"error": f"{type(e).__name__}: {e}"})

    return Handler


def watch_parent(pid: int):
    """Exit when the game process exits so the models don't linger in VRAM."""
    if sys.platform == "win32":
        import ctypes
        SYNCHRONIZE = 0x00100000
        handle = ctypes.windll.kernel32.OpenProcess(SYNCHRONIZE, False, pid)
        if handle:
            ctypes.windll.kernel32.WaitForSingleObject(handle, 0xFFFFFFFF)
    else:
        while True:
            try:
                os.kill(pid, 0)
            except OSError:
                break
            time.sleep(2)
    log.info("Game exited; shutting down.")
    os._exit(0)


def _data_dir() -> str:
    """%LOCALAPPDATA%/LethalMimicsAI on Windows, ~/.local/share/LethalMimicsAI elsewhere."""
    base = os.environ.get("LOCALAPPDATA") if sys.platform == "win32" else ""
    return os.path.join(base or os.path.join(os.path.expanduser("~"), ".local", "share"), "LethalMimicsAI")


def _default_llm_key() -> str:
    """LLM API key from the environment or a per-user file, so it never sits in a mod profile or on the command line."""
    key = os.environ.get("LETHALMIMICS_LLM_API_KEY", "").strip()
    if key:
        return key
    path = os.path.join(_data_dir(), "llm_api_key.txt")
    try:
        with open(path, encoding="utf-8") as f:
            return f.read().strip()
    except OSError:
        return ""


def main():
    ap = argparse.ArgumentParser(description="LethalMimics AI voice server (host only)")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=5279)
    ap.add_argument("--tts-model-dir", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "onnx", "model"),
                    help="Folder with Audio8/audio8-TTS-0.1B-ONNX-INT8")
    ap.add_argument("--tts", default="realistic", choices=["realistic", "onnx"],
                    help="realistic: Audio8-TTS-Realistic-0.6B (better voices; about real time on the GPU, 1.7x on CPU). onnx: the old 0.1B model")
    ap.add_argument("--tts-device", default="auto", choices=["auto", "cuda", "cpu"],
                    help="Where the realistic TTS runs. auto: the GPU when torch has ROCm/CUDA (venv-rocm), else the CPU")
    ap.add_argument("--realistic-model-dir", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "models", "realistic-0.6b"),
                    help="Folder with Edge0/Audio8-TTS-Realistic-0.6b")
    ap.add_argument("--tts-threads", type=int, default=8, help="CPU threads for the realistic TTS (16 is no faster)")
    ap.add_argument("--tts-max-tokens", type=int, default=512, help="Generation cap per line")
    ap.add_argument("--tts-temperature", type=float, default=None,
                    help="TTS sampling temperature (realistic model: 0.8). Higher sounds more varied, too high gets unstable")
    ap.add_argument("--asr-model", default="large-v3-turbo",
                    help="faster-whisper model name or local path (small.en: faster, English only)")
    ap.add_argument("--threads", type=int, default=8,
                    help="CPU threads for speech recognition (8 is fastest for large-v3-turbo; 4 is enough for small.en)")
    ap.add_argument("--llm-api", default="ollama", choices=["ollama", "openai"])
    ap.add_argument("--llm-base", default="http://127.0.0.1:11434")
    ap.add_argument("--llm-model", default="hf.co/bartowski/TheDrummer_Orion-26B-A4B-v1.1-GGUF:Q4_K_M",
                    help="Ollama model. Default: TheDrummer's Orion, an uncensored Gemma 4 26B-A4B finetune that swears "
                         "like players do. The plain one: gemma4:26b-a4b")
    ap.add_argument("--llm-key", default=_default_llm_key(),
                    help="API key for the LLM endpoint. Default: LETHALMIMICS_LLM_API_KEY, or llm_api_key.txt in "
                         "%%LOCALAPPDATA%%/LethalMimicsAI (Windows) or ~/.local/share/LethalMimicsAI")
    ap.add_argument("--asr-device", default="auto", choices=["auto", "cuda", "cpu"],
                    help="Where large-v3-turbo runs. auto: the GPU when torch has ROCm/CUDA (venv-rocm), else the CPU")
    ap.add_argument("--whisper-gpu-model-dir",
                    default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "models", "whisper-large-v3-turbo"),
                    help="PyTorch copy of whisper-large-v3-turbo for --asr-device (hf download openai/whisper-large-v3-turbo)")
    ap.add_argument("--language", default="English",
                    help="Language players speak: English, German, ..., or Auto to detect it per clip (slower, and "
                         "short clips get misheard as other languages)")
    ap.add_argument("--llm-ctx", type=int, default=int(os.environ.get("OLLAMA_CONTEXT_LENGTH") or 4096),
                    help="The LLM's context in tokens (Ollama: OLLAMA_CONTEXT_LENGTH); long prompts are trimmed to fit")
    ap.add_argument("--max-ref-seconds", type=float, default=12.0)
    ap.add_argument("--min-ref-seconds", type=float, default=4.0)
    ap.add_argument("--training-dir", default=os.path.join(_data_dir(), "training"),
                    help="Where decisions are saved as text when the host turns on Brain > CollectTrainingData in the mod")
    ap.add_argument("--cache-dir", default="", help="Enable cross-game voice cache in this folder (empty = off)")
    ap.add_argument("--hf-home", default="", help="Where to download models (sets HF_HOME)")
    ap.add_argument("--hf-token", default="", help="Hugging Face token, only needed for private/gated models")
    ap.add_argument("--parent-pid", type=int, default=0)
    ap.add_argument("--log-file", default="")
    args = ap.parse_args()

    try:  # a Windows console is often cp1252; player names and lines aren't
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass
    handlers = [logging.StreamHandler(sys.stdout)]
    if args.log_file:
        handlers.append(logging.FileHandler(args.log_file, mode="w", encoding="utf-8"))
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", handlers=handlers)
    if args.hf_home:
        os.environ["HF_HOME"] = args.hf_home
    # All default models are public. A stale saved login token makes the Hub reject even public downloads,
    # so don't send it unless one is given explicitly.
    if args.hf_token:
        os.environ["HF_TOKEN"] = args.hf_token
    else:
        os.environ.setdefault("HF_HUB_DISABLE_IMPLICIT_TOKEN", "1")
    os.environ.setdefault("HF_HUB_DISABLE_SYMLINKS_WARNING", "1")

    brain = Brain(args)
    if args.parent_pid:
        threading.Thread(target=watch_parent, args=(args.parent_pid,), daemon=True).start()
    threading.Thread(target=brain.load, daemon=True).start()
    threading.Thread(target=brain.ensure_llm, daemon=True).start()
    threading.Thread(target=brain.maintenance_loop, daemon=True).start()

    # On Windows SO_REUSEADDR lets a second server silently share the port; fail loudly instead.
    ThreadingHTTPServer.allow_reuse_address = sys.platform != "win32"
    server = ThreadingHTTPServer((args.host, args.port), make_handler(brain))
    log.info("LethalMimics AI server listening on http://%s:%d (voice cache: %s)", args.host, args.port, args.cache_dir or "off")
    server.serve_forever()


if __name__ == "__main__":
    main()
