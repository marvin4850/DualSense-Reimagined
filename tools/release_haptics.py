"""Copies haptics/ into a release package (tools/release.ps1).

Left out: bank/*/preview (listening reels, ~300 MB), *.md reports, *.bak, *.jsonl test journals.
Bank WAVs: 32-bit float -> 16-bit PCM (half the size; DsBridge reads both, Dsp.DecodeWav). 16-bit = -96 dB
quantisation, far below anything an actuator shows.

usage: python -I release_haptics.py SRC_HAPTICS DST_HAPTICS
"""
import array
import os
import shutil
import struct
import sys

SKIP_DIRS = {"preview"}
SKIP_EXT = {".md", ".bak", ".jsonl"}


def float_wav_to_pcm16(data: bytes) -> bytes | None:
    """32-bit float WAV -> 16-bit PCM WAV; None if the file is not a float WAV (copied unchanged)."""
    if data[:4] != b"RIFF" or data[8:12] != b"WAVE":
        return None
    o, fmt, ch, rate, bits, pcm = 12, None, 0, 0, 0, None
    while o + 8 <= len(data):
        cid, size = data[o:o + 4], struct.unpack_from("<I", data, o + 4)[0]
        body = o + 8
        if cid == b"fmt ":
            fmt, ch, rate = struct.unpack_from("<HHI", data, body)
            bits = struct.unpack_from("<H", data, body + 14)[0]
            if fmt == 0xFFFE and size >= 26:
                fmt = struct.unpack_from("<H", data, body + 24)[0]
        elif cid == b"data":
            pcm = data[body:body + size]
        o = body + size + (size & 1)
    if fmt != 3 or bits != 32 or pcm is None:
        return None
    f = array.array("f")
    f.frombytes(pcm[: len(pcm) // 4 * 4])
    if sys.byteorder != "little":
        f.byteswap()
    h = array.array("h", (max(-32768, min(32767, round(v * 32767.0))) for v in f))
    out = h.tobytes()
    hdr = b"RIFF" + struct.pack("<I", 36 + len(out)) + b"WAVE"
    hdr += b"fmt " + struct.pack("<IHHIIHH", 16, 1, ch, rate, rate * ch * 2, ch * 2, 16)
    hdr += b"data" + struct.pack("<I", len(out))
    return hdr + out


def main() -> int:
    src, dst = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
    n_files = n_wav = 0
    size_in = size_out = 0
    for d, dirs, files in os.walk(src):
        dirs[:] = [x for x in dirs if x not in SKIP_DIRS]
        rel = os.path.relpath(d, src)
        os.makedirs(os.path.join(dst, rel), exist_ok=True)
        for name in files:
            if os.path.splitext(name)[1].lower() in SKIP_EXT:
                continue
            s, t = os.path.join(d, name), os.path.join(dst, rel, name)
            if name.lower().endswith(".wav"):
                with open(s, "rb") as fh:
                    raw = fh.read()
                conv = float_wav_to_pcm16(raw)
                with open(t, "wb") as fh:
                    fh.write(conv if conv is not None else raw)
                size_in += len(raw)
                size_out += len(conv if conv is not None else raw)
                n_wav += 1
            else:
                shutil.copy2(s, t)
            n_files += 1
    print(f"haptics: {n_files} files ({n_wav} wav: {size_in / 1e6:.1f} MB -> {size_out / 1e6:.1f} MB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
