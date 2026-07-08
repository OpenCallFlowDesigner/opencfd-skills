"""Tests for the shared 3CX prompt WAV format check."""
import os
import sys
import wave
import struct
import tempfile

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "scripts"))
from wav_format import check_wav  # noqa: E402


def _write_wav(path, rate=8000, channels=1, sampwidth=2):
    with wave.open(path, "w") as f:
        f.setnchannels(channels)
        f.setsampwidth(sampwidth)
        f.setframerate(rate)
        f.writeframes(struct.pack("<4h", 0, 0, 0, 0))


def test_conformant_wav_passes():
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, "ok.wav")
        _write_wav(p)  # 8kHz mono 16-bit
        assert check_wav(p) is None


def test_wrong_rate_and_channels_flagged():
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, "bad.wav")
        _write_wav(p, rate=44100, channels=2)
        msg = check_wav(p)
        assert msg and "44100Hz" in msg and "stereo" in msg


def test_24bit_flagged():
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, "wide.wav")
        _write_wav(p, sampwidth=3)  # 24-bit
        msg = check_wav(p)
        assert msg and "24-bit" in msg


def test_non_wav_flagged():
    with tempfile.TemporaryDirectory() as d:
        p = os.path.join(d, "fake.wav")
        with open(p, "wb") as f:
            f.write(b"ID3\x03\x00not really a wav")  # MP3-ish header
        msg = check_wav(p)
        assert msg and "not a valid PCM WAV" in msg
