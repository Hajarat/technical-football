"""Encodes the clip gallery's frame dumps (<dir>/video/<clip>/f_NNNN.png) to <dir>/<clip>.mp4 at the clips' 60 fps.

    game/tests/run-bg.sh -- --clip-gallery ../out/clips --video
    uv run --with imageio-ffmpeg python3 tools/anim/encode_video.py out/clips [--keep-frames]

ffmpeg comes from the imageio-ffmpeg wheel (no system install). H.264, yuv420p, so any browser or player opens the files.
"""
from __future__ import annotations

import argparse
import shutil
import subprocess
import sys
from pathlib import Path

import imageio_ffmpeg


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dir", type=Path)
    ap.add_argument("--fps", type=int, default=60)
    ap.add_argument("--keep-frames", action="store_true", help="leave the PNG frames in place")
    args = ap.parse_args(argv)
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    frames_root = args.dir / "video"
    if not frames_root.is_dir():
        print(f"no {frames_root}: run the gallery with --video first", file=sys.stderr)
        return 1
    failed = 0
    for clip in sorted(p for p in frames_root.iterdir() if p.is_dir()):
        out = args.dir / f"{clip.name}.mp4"
        cmd = [ffmpeg, "-y", "-loglevel", "error", "-framerate", str(args.fps), "-i", str(clip / "f_%04d.png"),
               "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "20", "-threads", "4", str(out)]
        r = subprocess.run(cmd)
        if r.returncode != 0:
            failed += 1
            print(f"FAILED {clip.name}", file=sys.stderr)
            continue
        print(f"MP4 {clip.name} {out} ({out.stat().st_size // 1024} KB)")
        if not args.keep_frames:
            shutil.rmtree(clip)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
