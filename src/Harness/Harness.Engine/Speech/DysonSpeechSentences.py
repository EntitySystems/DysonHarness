"""Sentence split for the Python Qwen and Chatterbox bridges (stdlib only).

speak(text, generate, send, low_latency): Reliable (low_latency false) is one generate of the full text.
Low latency generates and sends one sentence at a time so the first PCM leaves after the first sentence.
Run directly with a JSON object on stdin ({"split": [text], "speak": [{"text", "lowLatency", "failOn"}]})
for the Harness.Tests check; that path uses a fake generate and imports nothing else.
"""
import json
import sys

_TERMINATORS = ".?!"
_CLOSERS = "\"')\u201d\u2019"
_ABBREVIATIONS = {"mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "eg", "ie"}


def _period_keeps(text, i):
    """True when the single '.' at text[i] is not a sentence end."""
    k = i
    while k > 0 and text[k - 1].isalpha():
        k -= 1
    word = text[k:i].lower()
    return word in _ABBREVIATIONS or len(word) == 1


def split_sentences(text):
    # ponytail: English heuristic, not a tokenizer. "I went to the U.S. Then I left." stays one piece because
    # U / S are single letters; "Ph.D." and "3.14" stay whole because no whitespace follows the inner period.
    # Upgrade path is a real sentence tokenizer, which we are not adding.
    text = text.strip()
    pieces, start, i, n = [], 0, 0, len(text)
    while i < n:
        if text[i] not in _TERMINATORS:
            i += 1
            continue
        j = i
        while j < n and text[j] in _TERMINATORS:
            j += 1
        run_len = j - i
        while j < n and text[j] in _CLOSERS:
            j += 1
        ends = j == n or text[j].isspace()
        if ends and (run_len > 1 or text[i] != "." or not _period_keeps(text, i)):
            piece = text[start:j].strip()
            if piece:
                pieces.append(piece)
            start = j
        i = j
    tail = text[start:].strip()
    if tail:
        pieces.append(tail)
    return pieces


def speak_sentences(text, generate, send):
    # ponytail: each sentence is an independent generate, so intonation does not carry
    # across the boundary. Upgrade path is a library generate that yields audio.
    # Do not insert silence or a crossfade here. Low latency only.
    for sentence in split_sentences(text):
        wav = generate(sentence)
        if wav is None:
            return False
        send(wav)
    return True


def speak(text, generate, send, low_latency):
    if low_latency:
        return speak_sentences(text, generate, send)
    wav = generate(text)
    if wav is None:
        return False
    send(wav)
    return True


def _check(req):
    out = {"split": [split_sentences(t) for t in req.get("split", [])], "speak": []}
    for case in req.get("speak", []):
        calls, sent = [], []

        def generate(piece, fail_on=case.get("failOn")):
            calls.append(piece)
            return None if piece == fail_on else piece

        ok = speak(case["text"], generate, sent.append, case.get("lowLatency") is True)
        out["speak"].append({"calls": calls, "sent": sent, "ok": ok})
    return out


if __name__ == "__main__":
    sys.stdout.write(json.dumps(_check(json.loads(sys.stdin.read()))))
