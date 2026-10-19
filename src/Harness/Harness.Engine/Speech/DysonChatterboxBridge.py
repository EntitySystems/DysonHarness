"""Chatterbox Turbo (English only) bridge: one JSON line in, length-prefixed PCM frames out.

{"text", "voicePath", "lowLatency"} speaks with a saved voice prompt (Conditionals .pt; lowLatency true: one generate per sentence). {"op": "enroll", "audioPath",
"outPath"} saves a voice prompt from a clip longer than 5 seconds (no weights change). Every output carries
Resemble's Perth implicit watermark. Frames: >0 PCM s16le 24 kHz, 0 end, <0 UTF-8 error.
"""
import argparse
import json
import logging
import os
import struct
import sys

os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "1")
os.environ.setdefault("TQDM_DISABLE", "1")

# Library logs (Perth prints "loaded PerthNet") must not share the PCM stream. Save the buffer first.
_pcm_out = sys.stdout.buffer
sys.stdout = sys.stderr
logging.basicConfig(stream=sys.stderr, level=logging.INFO)

import librosa
import numpy as np
import torch
from chatterbox.tts_turbo import ChatterboxTurboTTS, Conditionals
from speech_sentences import speak

SAMPLE_RATE = 24000
MIN_CLIP_SECONDS = 5.0
_model = None
_conds = {}


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
    # from_local moves modules with .to(device). Only the ROCm torch sets cuda:0.
    return "cuda:0" if os.environ.get("DYSON_SPEECH_TORCH_DEVICE") == "cuda:0" else "cpu"


def _load(model_dir):
    global _model
    if _model is None:
        _model = ChatterboxTurboTTS.from_local(model_dir, _device())
    return _model


def _voice(path):
    conds = _conds.get(path)
    if conds is None:
        conds = Conditionals.load(path).to(_device())
        _conds[path] = conds
    return conds


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
            if req.get("op") == "enroll":
                # Upstream asserts on short clips; answer with an error frame instead.
                if librosa.get_duration(path=req["audioPath"]) <= MIN_CLIP_SECONDS:
                    _fail("Chatterbox Turbo needs a clip longer than 5 seconds.")
                    continue
                model = _load(args.model)
                model.prepare_conditionals(req["audioPath"])
                model.conds.save(req["outPath"])
                _conds.pop(req["outPath"], None)
            else:
                model = _load(args.model)
                if int(model.sr) != SAMPLE_RATE:
                    _fail(f"sample rate is {model.sr}, not {SAMPLE_RATE}")
                    continue
                model.conds = _voice(req["voicePath"])
                speak(req["text"], lambda piece: model.generate(piece).detach().cpu().numpy(), _send_pcm,
                      req.get("lowLatency") is True)
            _frame(0)
        except Exception as exc:
            _fail(f"{type(exc).__name__}: {exc}")


if __name__ == "__main__":
    main()
