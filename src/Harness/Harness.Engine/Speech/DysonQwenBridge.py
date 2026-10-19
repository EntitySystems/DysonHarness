"""Qwen3-TTS bridge: one JSON line in, length-prefixed PCM frames out.

{"text", "voicePath", "lowLatency"} speaks with a saved voice prompt (lowLatency true: one generate per sentence). {"op": "enroll", "audioPath", "outPath"} saves a
speaker-embedding voice prompt (x-vector only; no weights change). Frames: >0 PCM s16le 24 kHz, 0 end,
<0 UTF-8 error.
"""
import argparse
import dataclasses
import json
import logging
import os
import struct
import sys

os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "1")
os.environ.setdefault("TQDM_DISABLE", "1")

# Library logs must not share the PCM stream. Save the buffer first.
_pcm_out = sys.stdout.buffer
sys.stdout = sys.stderr
logging.basicConfig(stream=sys.stderr, level=logging.INFO)

import numpy as np
import torch
from qwen_tts import Qwen3TTSModel, VoiceClonePromptItem
from speech_sentences import speak

SAMPLE_RATE = 24000
_model = None
_prompts = {}


def _frame(n, payload=b""):
    _pcm_out.write(struct.pack("<i", n))
    if payload:
        _pcm_out.write(payload)
    _pcm_out.flush()


def _fail(message):
    data = message.encode("utf-8")
    _frame(-len(data), data)


def _send_pcm(wav):
    samples = np.clip(np.asarray(wav, dtype=np.float32).reshape(-1), -1.0, 1.0)
    pcm = (samples * 32767.0).astype("<i2")
    for start in range(0, len(pcm), SAMPLE_RATE):
        data = pcm[start:start + SAMPLE_RATE].tobytes()
        if data:
            _frame(len(data), data)


def _device():
    # qwen-tts 0.1.1 documents device_map="cuda:0". Anything else stays CPU float32.
    return "cuda:0" if os.environ.get("DYSON_SPEECH_TORCH_DEVICE") == "cuda:0" else "cpu"


def _load(model_dir):
    global _model
    if _model is None:
        _model = Qwen3TTSModel.from_pretrained(model_dir, device_map=_device(), dtype=torch.float32)
    return _model


def _prompt(path):
    items = _prompts.get(path)
    if items is None:
        data = torch.load(path, map_location=_device(), weights_only=True)
        items = [VoiceClonePromptItem(**d) for d in data["items"]]
        _prompts[path] = items
    return items


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    args = parser.parse_args()
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
            model = _load(args.model)
            if req.get("op") == "enroll":
                items = model.create_voice_clone_prompt(ref_audio=req["audioPath"], x_vector_only_mode=True)
                torch.save({"items": [dataclasses.asdict(i) for i in items]}, req["outPath"])
                _prompts.pop(req["outPath"], None)
            else:
                prompt = _prompt(req["voicePath"])

                def generate(piece):
                    wavs, rate = model.generate_voice_clone(text=piece, language="Auto", voice_clone_prompt=prompt)
                    if int(rate) != SAMPLE_RATE:
                        _fail(f"sample rate is {rate}, not {SAMPLE_RATE}")
                        return None
                    return wavs[0]

                if not speak(req["text"], generate, _send_pcm, req.get("lowLatency") is True):
                    continue
            _frame(0)
        except Exception as exc:
            _fail(f"{type(exc).__name__}: {exc}")


if __name__ == "__main__":
    main()
