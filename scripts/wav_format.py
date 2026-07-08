"""Shared 3CX prompt WAV format check.

3CX Call Flow prompts must be 8kHz, mono, 16-bit PCM WAV. A wrong-format file
(e.g. 44.1kHz stereo, 24-bit, or an MP3 renamed .wav) passes reference checks
and packages fine, then fails or plays garbled at runtime. This catches it.
"""
from __future__ import annotations
import wave

EXPECTED = "8000Hz mono 16-bit PCM"
REQ_RATE, REQ_CHANNELS, REQ_SAMPWIDTH = 8000, 1, 2  # 2 bytes = 16-bit


def check_wav(path) -> str | None:
    """Return a human-readable problem string if `path` is not a valid 3CX prompt
    WAV, or None if it conforms to 8kHz/mono/16-bit PCM."""
    try:
        with wave.open(str(path)) as f:
            rate, channels, sampwidth, comp = (
                f.getframerate(), f.getnchannels(), f.getsampwidth(), f.getcomptype())
    except wave.Error as e:
        return f"not a valid PCM WAV ({e}); expected {EXPECTED}"
    except EOFError:
        return f"truncated or empty WAV; expected {EXPECTED}"

    problems = []
    if comp != "NONE":
        problems.append(f"compressed ({comp})")
    if rate != REQ_RATE:
        problems.append(f"{rate}Hz")
    if channels != REQ_CHANNELS:
        problems.append("stereo" if channels == 2 else f"{channels}ch")
    if sampwidth != REQ_SAMPWIDTH:
        problems.append(f"{sampwidth * 8}-bit")
    if problems:
        return f"is {', '.join(problems)}; expected {EXPECTED}"
    return None
