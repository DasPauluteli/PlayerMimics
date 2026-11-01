"""Whisper (large-v3-turbo) on the GPU through PyTorch, for ROCm builds where faster-whisper can't use the GPU.

transcribe() gives the same things server.py reads from faster-whisper: the text, the (start, end) seconds the
words span (from Whisper's token timestamps) and the detected language. Clips longer than 30 s are cut to 30 s;
the mod sends at most 12 s.
"""
from __future__ import annotations

import threading
from pathlib import Path

import os

import numpy as np

# torch 2.14 + ROCm 10.1 routes some matmuls to experimental Triton kernels that fail to import on this build.
os.environ.setdefault("TORCH_DISABLE_NATIVE_JIT", "1")
import torch  # noqa: E402

HERE = Path(__file__).resolve().parent
DEFAULT_MODEL_DIR = HERE / "models" / "whisper-large-v3-turbo"
SR = 16000


class GpuWhisper:
    def __init__(self, model_dir: Path = DEFAULT_MODEL_DIR, device: str = "cuda", dtype: str = "float16"):
        from transformers import WhisperForConditionalGeneration, WhisperProcessor

        self.device = torch.device(device)
        if self.device.type != "cpu":
            # MIOpen is slower than plain torch for small convolutions here and very chatty (same as torch_tts).
            torch.backends.cudnn.enabled = False
        self.dtype = getattr(torch, dtype)
        self.processor = WhisperProcessor.from_pretrained(str(model_dir))
        # sdpa: the eager attention path hits a broken Triton matmul kernel in torch 2.14 + ROCm 10.1.
        self.model = WhisperForConditionalGeneration.from_pretrained(str(model_dir), dtype=self.dtype,
                                                                     attn_implementation="sdpa")
        self.model.to(self.device).eval()
        self.tokenizer = self.processor.tokenizer
        self.id_to_lang = {v: k.strip("<|>") for k, v in self.model.generation_config.lang_to_id.items()}
        self.first_text_id = self.tokenizer.convert_tokens_to_ids("<|endoftext|>")  # ids below are text tokens
        self._lock = threading.Lock()
        self.transcribe(np.zeros(SR, dtype=np.float32))  # warm-up: loads the GPU kernels

    def transcribe(self, audio16: np.ndarray, language: str | None = None, beams: int = 1
                   ) -> tuple[str, tuple[float, float] | None, str]:
        audio16 = np.asarray(audio16, dtype=np.float32).reshape(-1)[: 30 * SR]
        fe = self.processor.feature_extractor(audio16, sampling_rate=SR, return_tensors="pt", return_attention_mask=True)
        feats = fe.input_features.to(self.device, self.dtype)
        mask = fe.attention_mask.to(self.device)
        with self._lock, torch.inference_mode():
            # Run the encoder once and share it: generate() would otherwise encode again for language detection.
            enc = self.model.get_encoder()(feats)
            if language is None:
                lang_id = int(self.model.detect_language(encoder_outputs=enc)[0])
                language = self.id_to_lang.get(lang_id, "en")
            out = self.model.generate(encoder_outputs=enc, attention_mask=mask, language=language, task="transcribe",
                                      num_beams=beams, return_token_timestamps=True, return_dict_in_generate=True,
                                      max_new_tokens=220)
        get = out.get if isinstance(out, dict) else (lambda k: getattr(out, k, None))
        seq = get("sequences")[0].tolist()
        tt = get("token_timestamps")
        times = tt[0].float().cpu().tolist() if tt is not None else []
        lang = language
        text_idx = [i for i, t in enumerate(seq) if t < self.first_text_id]
        text = self.tokenizer.decode([seq[i] for i in text_idx], skip_special_tokens=True).strip()
        span = None
        if text_idx and len(times) == len(seq):
            # token_timestamps[i] is when token i starts; the last word ends where the token after it starts.
            start = times[text_idx[0]]
            end = times[text_idx[-1] + 1] if text_idx[-1] + 1 < len(times) else times[text_idx[-1]] + 0.3
            dur = audio16.size / SR
            span = (max(0.0, min(start, dur)), max(0.0, min(end, dur)))
        return text, span, lang
