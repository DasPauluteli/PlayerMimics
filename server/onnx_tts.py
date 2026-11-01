"""Audio8 TTS 0.1B (ONNX INT8, CPU) with per-voice prompt caching.

The model reads its whole prompt one step at a time, and the reference clip makes up
most of that prompt. The reference always comes before the target text, so we read it
once when a voice is built and reuse the model state for every line in that voice.
"""
from __future__ import annotations

import json
import sys
import tempfile
from dataclasses import dataclass
from math import gcd
from pathlib import Path

import numpy as np
import onnxruntime as ort
from scipy.signal import resample_poly

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE / "onnx/repo/onnx_runtime_0_1b_int8"))
from arktts_runtime.prompt import clean_text, format_reference_text  # noqa: E402
from arktts_runtime.runtime import ArkTtsRuntime, _sample  # noqa: E402

DEFAULT_MODEL_DIR = HERE / "onnx/model"


@dataclass
class Voice:
    state: dict[str, np.ndarray]  # model state after the reference; copied per line
    logits: np.ndarray
    hidden: np.ndarray
    length: int  # prompt positions already consumed


class CachedTts:
    def __init__(self, model_dir: Path = DEFAULT_MODEL_DIR, threads: int = 4):
        model_dir = Path(model_dir)
        self._voices_tmp = tempfile.mkdtemp(prefix="lethalmimics-voices-")  # unused, required by the runtime
        self.rt = ArkTtsRuntime(model_dir, Path(self._voices_tmp), threads=threads)
        self.m = self.rt.manifest
        self.sample_rate = int(self.m["sample_rate"])
        self.num_codebooks = int(self.m["num_codebooks"])
        reg = json.loads((model_dir / "registration/registration_manifest.json").read_text())
        opts = ort.SessionOptions()
        opts.log_severity_level = 3
        opts.intra_op_num_threads = max(1, threads)
        self.encoder = ort.InferenceSession(str(model_dir / "registration" / reg["encoder"]),
                                            sess_options=opts, providers=["CPUExecutionProvider"])
        enc_in = self.encoder.get_inputs()[0]
        self._enc_name = enc_in.name
        self._enc_dtype = np.float16 if enc_in.type == "tensor(float16)" else np.float32
        tok = self.rt.prompt_builder.encode_text
        self._suffix_head = tok("<|im_end|>\n") + tok("<|im_start|>user\n")
        self._suffix_tail = tok("<|im_end|>\n") + tok("<|im_start|>assistant\n<|voice|>")
        self._tok = tok

    # --- voice building -------------------------------------------------------------
    def encode(self, audio: np.ndarray, sr: int) -> np.ndarray:
        """Mono float audio -> reference codes [num_codebooks, T]."""
        audio = np.asarray(audio, dtype=np.float32).reshape(-1)
        if sr != self.sample_rate:
            g = gcd(int(sr), self.sample_rate)
            audio = resample_poly(audio, self.sample_rate // g, int(sr) // g).astype(np.float32)
        pad = (-audio.size) % int(self.m["codec_hop_length"])
        if pad:
            audio = np.pad(audio, (0, pad))
        codes = np.asarray(self.encoder.run(None, {self._enc_name: audio.reshape(1, 1, -1).astype(self._enc_dtype)})[0])
        if codes.ndim == 3:
            codes = codes[0]
        if codes.shape[0] != self.num_codebooks or codes.shape[1] == 0:
            raise RuntimeError(f"encoder returned invalid codes: {codes.shape}")
        return codes.astype(np.int64)

    def build_voice(self, audio: np.ndarray, sr: int, reference_text: str) -> Voice:
        codes = self.encode(audio, sr)
        prefix = [t for part in ("<|im_start|>system\n",
                                 "convert the provided text to speech reference to the following:\n\nText:\n",
                                 format_reference_text(reference_text), "\n\nSpeech:\n")
                  for t in self._tok(part)]
        n = len(prefix) + codes.shape[1]
        cols = np.zeros((self.num_codebooks + 1, n), dtype=np.int64)
        cols[0, :len(prefix)] = prefix
        cols[0, len(prefix):] = codes[0] + int(self.m["semantic_begin_id"])
        cols[1:, len(prefix):] = codes
        state = self.rt._empty_slow_state()
        logits = hidden = None
        for pos in range(n):
            logits, hidden = self.rt._slow_step(cols[np.newaxis, :, pos:pos + 1], pos, state)
        return Voice(state, logits, hidden, n)

    # --- synthesis ------------------------------------------------------------------
    def synthesize(self, voice: Voice, text: str, max_new_tokens: int = 512, temperature: float = 0.7,
                   top_p: float = 0.9, top_k: int = 50, seed: int | None = None) -> np.ndarray:
        target = clean_text(text)
        if not target:
            raise ValueError("text must not be empty")
        rt, m = self.rt, self.m
        suffix = self._suffix_head + self._tok(target) + self._suffix_tail
        state = {k: v.copy() for k, v in voice.state.items()}
        pos = voice.length
        logits, hidden = voice.logits, voice.hidden
        for tok in suffix:
            col = np.zeros((1, self.num_codebooks + 1, 1), dtype=np.int64)
            col[0, 0, 0] = tok
            logits, hidden = rt._slow_step(col, pos, state)
            pos += 1
        max_new_tokens = min(int(max_new_tokens), int(m["max_seq_len"]) - pos)

        rng = np.random.default_rng(seed)
        begin, stop, size = int(m["semantic_begin_id"]), int(m["im_end_id"]), int(m["codebook_size"])
        previous: list[int] = []
        frames: list[np.ndarray] = []
        for step in range(max_new_tokens):
            semantic = rt._sample_semantic(logits, previous, temperature, top_p, top_k, rng)
            if semantic == stop:
                break
            previous = (previous + [semantic])[-10:]
            caches = rt._empty_fast_caches()
            rt._fast_step(hidden, 0, True, 0, caches)
            token = semantic - begin
            if not 0 <= token < size:
                raise ValueError(f"semantic token {semantic} outside codebook")
            frame = [token]
            for fp in range(1, self.num_codebooks):
                token = _sample(rt._fast_step(hidden, token, False, fp, caches), temperature, top_p, top_k, rng)
                frame.append(token)
            frames.append(np.asarray(frame, dtype=np.int64))
            if step + 1 >= max_new_tokens:
                break
            col = np.concatenate([[semantic], frames[-1]]).reshape(1, -1, 1)
            logits, hidden = rt._slow_step(col, pos, state)
            pos += 1
        if not frames:
            raise RuntimeError("model produced no audio")
        return rt.decode_codes(np.stack(frames, axis=1))
