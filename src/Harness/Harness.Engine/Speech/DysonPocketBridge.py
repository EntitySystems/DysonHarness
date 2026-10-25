"""Pocket TTS bridge: one JSON line in, length-prefixed PCM frames out.

{"text", "voicePath", "lowLatency"} speaks (lowLatency true: one generate per sentence).
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

# Library logs must not share the PCM stream. Save the buffer first.
_pcm_out = sys.stdout.buffer
sys.stdout = sys.stderr
logging.basicConfig(stream=sys.stderr, level=logging.INFO)

from speech_sentences import speak

import torch
from pocket_tts import TTSModel

_voices = {}


def _frame(n, payload=b""):
    _pcm_out.write(struct.pack("<i", n))
    if payload:
        _pcm_out.write(payload)
    _pcm_out.flush()


def _fail(message):
    data = message.encode("utf-8")
    _frame(-len(data), data)


def _s16le(chunk):
    samples = chunk.detach().to(dtype=torch.float32).reshape(-1).clamp(-1, 1)
    pcm = (samples * 32767.0).to(torch.int16).cpu().contiguous()
    return pcm.numpy().tobytes()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    args = parser.parse_args()
    model = TTSModel.load_model(config=args.config)
    rate = int(model.sample_rate)
    if rate != 24000:
        print(f"sample rate is {rate}, not 24000", file=sys.stderr)
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
            text, voice = req["text"], req["voicePath"]
            state = _voices.get(voice)
            if state is None:
                state = model.get_state_for_audio_prompt(voice)
                _voices[voice] = state

            def generate(piece):
                for chunk in model.generate_audio_stream(state, piece):
                    pcm = _s16le(chunk)
                    if pcm:
                        _frame(len(pcm), pcm)
                return True

            if not speak(text, generate, lambda _wav: None, req.get("lowLatency") is True):
                continue
            _frame(0)
        except Exception as exc:
            _fail(f"{type(exc).__name__}: {exc}")


if __name__ == "__main__":
    main()
