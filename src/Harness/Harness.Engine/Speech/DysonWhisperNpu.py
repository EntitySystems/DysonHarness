"""Whisper speech-to-text on Intel NPU only.

Device is hardcoded to NPU. This script never constructs a CPU or GPU pipeline.
"""
import os
import sys
import wave


def fail(message):
    print(message, file=sys.stderr)
    return 1


def main():
    if len(sys.argv) != 3:
        return fail("usage: whisper_npu.py <wav> <model_dir>")
    wav_path, model_dir = sys.argv[1], sys.argv[2]
    try:
        import numpy as np
        import openvino as ov
        import openvino_genai as ov_genai
    except Exception as exc:
        return fail(
            f"Intel NPU runtime failed to import ({type(exc).__name__}: {exc}). Not CPU and not Vulkan.")

    try:
        devices = list(ov.Core().available_devices)
    except Exception as exc:
        return fail(
            f"Intel NPU device probe failed ({type(exc).__name__}: {exc}). Not CPU and not Vulkan.")
    if "NPU" not in devices:
        listed = ", ".join(devices) if devices else "(none)"
        return fail(
            f"Intel NPU is not in OpenVINO available devices ({listed}). Not CPU and not Vulkan.")
    if not os.path.isdir(model_dir):
        return fail("Intel NPU Whisper model directory is missing. Not CPU and not Vulkan.")

    try:
        with wave.open(wav_path, "rb") as handle:
            channels = handle.getnchannels()
            width = handle.getsampwidth()
            rate = handle.getframerate()
            pcm = handle.readframes(handle.getnframes())
    except Exception as exc:
        return fail(f"Intel NPU could not read the wav ({type(exc).__name__}: {exc}).")
    if channels != 1 or width != 2 or rate != 16000:
        return fail(
            f"Intel NPU Whisper expects 16 kHz mono PCM16, got {rate} Hz, {channels} ch, {width * 8} bit.")

    audio = np.frombuffer(pcm, dtype=np.int16).astype(np.float32) / 32768.0
    cache = os.path.join(model_dir, "cache")
    os.makedirs(cache, exist_ok=True)
    try:
        # Device string is NPU only. Do not pass task=translate.
        pipe = ov_genai.WhisperPipeline(model_dir, "NPU", CACHE_DIR=cache)
        result = pipe.generate(audio)
    except Exception as exc:
        return fail(
            f"Intel NPU WhisperPipeline failed ({type(exc).__name__}: {exc}). Not CPU and not Vulkan.")

    texts = getattr(result, "texts", None)
    if isinstance(result, str):
        text = result
    elif isinstance(texts, (list, tuple)):
        text = " ".join(str(part) for part in texts)
    else:
        text = str(result)
    print(text.strip(), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
