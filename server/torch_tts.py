"""Audio8-TTS-Realistic-0.6B (PyTorch, transformers custom code) with per-voice reference caching.

Same interface as onnx_tts.CachedTts: build_voice() once per player, synthesize() per line. The reference clip is
encoded to codec tokens once and reused, so a line only pays for the prompt read and generation.
"""
from __future__ import annotations

import threading
from dataclasses import dataclass
from math import gcd
from pathlib import Path

import os

import numpy as np

# torch 2.14 + ROCm 10.1 routes some matmuls to experimental Triton kernels that fail to import on this build.
os.environ.setdefault("TORCH_DISABLE_NATIVE_JIT", "1")
import torch  # noqa: E402
from scipy.signal import resample_poly

HERE = Path(__file__).resolve().parent
DEFAULT_MODEL_DIR = HERE / "models" / "realistic-0.6b"


@dataclass
class Voice:
    codes: torch.Tensor  # [num_codebooks, T] reference codec tokens
    text: str            # transcript of the reference


class TorchTts:
    def __init__(self, model_dir: Path = DEFAULT_MODEL_DIR, threads: int = 8, dtype: str = "bfloat16",
                 device: str = "cpu"):
        from transformers import AutoModel, AutoProcessor

        torch.set_num_threads(max(1, threads))
        model_dir = str(model_dir)
        self.processor = AutoProcessor.from_pretrained(model_dir, trust_remote_code=True)
        self.model = AutoModel.from_pretrained(model_dir, trust_remote_code=True,
                                               dtype=getattr(torch, dtype)).eval()
        # "cuda" also means an AMD GPU with a ROCm build of torch.
        self.device = torch.device(device)
        if self.device.type != "cpu":
            # MIOpen (ROCm's cuDNN) is slower than plain torch for the codec's convolutions here,
            # and prints hundreds of debug lines per clip.
            torch.backends.cudnn.enabled = False
        self.model.to(self.device)
        self.model.load_codec(device=self.device)  # on CPU the codec runs in float32
        self.sample_rate = int(self.processor.audio_sampling_rate)
        # generate() keeps its KV caches on the model, so only one line at a time.
        self._gen_lock = threading.Lock()
        if self.device.type != "cpu":
            self._warm_up()

    def _warm_up(self) -> None:
        """Runs the GPU kernels once at load, so the first real line isn't slower."""
        voice = self.build_voice(np.zeros(self.sample_rate, dtype=np.float32), self.sample_rate, "Hi.")
        try:
            self.synthesize(voice, "Okay.", max_new_tokens=24, seed=0)
        except RuntimeError:
            pass  # silence as a reference may give no audio; the kernels still ran

    def build_voice(self, audio: np.ndarray, sr: int, reference_text: str) -> Voice:
        audio = np.asarray(audio, dtype=np.float32).reshape(-1)
        if sr != self.sample_rate:
            g = gcd(int(sr), self.sample_rate)
            audio = resample_poly(audio, self.sample_rate // g, int(sr) // g).astype(np.float32)
        values = torch.from_numpy(audio).view(1, 1, -1).to(self.device)
        lengths = torch.tensor([audio.size], dtype=torch.long, device=self.device)
        with self._gen_lock, torch.inference_mode():  # the codec is shared with decoding
            codes, code_lengths = self.model.encode_audio(values, lengths)
        return Voice(codes[0, :, : int(code_lengths[0])].long().cpu(), reference_text)

    def synthesize(self, voice: Voice, text: str, max_new_tokens: int = 512, temperature: float = 0.8,
                   top_p: float = 0.95, top_k: int = 50, seed: int | None = None) -> np.ndarray:
        inputs = self.processor(text=[text], reference_text=[voice.text], reference_codes=[voice.codes],
                                return_tensors="pt")
        generator = torch.Generator(device=self.device).manual_seed(seed) if seed is not None else None
        with self._gen_lock, torch.inference_mode():
            out = self.model.generate(**inputs, max_new_tokens=max_new_tokens, temperature=temperature,
                                      top_p=top_p, top_k=top_k, do_sample=True, return_dict_in_generate=True,
                                      generator=generator)
            if out.codes.shape[-1] == 0:
                raise RuntimeError("model produced no audio")
            wave, lengths = self.model.decode_audio(out.codes)
        return wave[0, : int(lengths[0])].float().cpu().numpy()

    # Frames decoded before the first chunk, then the gap grows by 2 each time (6, 14, 24, 36, ...), so a line
    # needs only a handful of decodes. One frame is 2048 samples, about 46 ms.
    FIRST_CHUNK_FRAMES = 6
    SAMPLES_PER_FRAME = 2048

    def synthesize_stream(self, voice: Voice, text: str, on_audio, max_new_tokens: int = 512,
                          temperature: float = 0.8, top_p: float = 0.95, top_k: int = 50,
                          seed: int | None = None) -> np.ndarray:
        """Like synthesize(), but calls on_audio(wave_so_far, final) while generating.

        The codec is causal, so decoding all frames so far gives the same samples as the final decode for every
        frame except the newest; wave_so_far leaves that one out until final. Returns the whole line.
        """
        inputs = self.processor(text=[text], reference_text=[voice.text], reference_codes=[voice.codes],
                                return_tensors="pt")
        generator = torch.Generator(device=self.device).manual_seed(seed) if seed is not None else None
        model = self.model
        eos = model.config.eos_token_id
        frames: list[torch.Tensor] = []
        state = {"semantic": None, "next": self.FIRST_CHUNK_FRAMES, "gap": 8}
        sample_semantic = model._sample_semantic
        generate_codebooks = model._generate_codebooks

        def decode(n: int) -> np.ndarray:
            codes = torch.stack(frames[:n], dim=-1)  # [1, num_codebooks, n]
            wave, lengths = model.decode_audio(codes)
            return wave[0, : int(lengths[0])].float().cpu().numpy()

        def on_semantic(*args, **kwargs):
            semantic = sample_semantic(*args, **kwargs)
            state["semantic"] = semantic
            return semantic

        def on_codebooks(*args, **kwargs):
            codebooks = generate_codebooks(*args, **kwargs)
            if int(state["semantic"][0]) != eos:
                frames.append(codebooks[:1])
                if len(frames) >= state["next"]:
                    wave = decode(len(frames))
                    on_audio(wave[: (len(frames) - 1) * self.SAMPLES_PER_FRAME], False)
                    state["next"] += state["gap"]
                    state["gap"] += 2
            return codebooks

        with self._gen_lock, torch.inference_mode():
            # Hooks on this model instance only, removed again below; _gen_lock keeps other lines out meanwhile.
            model._sample_semantic = on_semantic
            model._generate_codebooks = on_codebooks
            try:
                model.generate(**inputs, max_new_tokens=max_new_tokens, temperature=temperature, top_p=top_p,
                               top_k=top_k, do_sample=True, return_dict_in_generate=True, generator=generator)
            finally:
                del model._sample_semantic
                del model._generate_codebooks
            if not frames:
                raise RuntimeError("model produced no audio")
            wave = decode(len(frames))
        on_audio(wave, True)
        return wave
