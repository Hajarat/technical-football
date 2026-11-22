"""Fetches the CMU motion capture files that tools/anim/clips.toml lists, and nothing else.

For every clip's trial "<S>_<TT>" it downloads the subject's skeleton (<S>.asf) and the trial's motion (<S>_<TT>.amc) into the
git-ignored cache tools/anim/.cache/. Files already in the cache are skipped. One request per file, sequential, with a short pause
between requests; no page is crawled.

mocap.cs.cmu.edu serves an incomplete TLS certificate chain, so certificate verification is switched off for that host only (the
files are checked by parsing them afterwards).

    python3 tools/anim/cmu.py            # fetch what is missing
    python3 tools/anim/cmu.py --list     # print the files clips.toml needs
    python3 tools/anim/cmu.py --config tools/anim/attacker_clips.toml   # another clip list (the attacker's)
"""
from __future__ import annotations

import argparse
import ssl
import sys
import time
import tomllib
import urllib.parse
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
CACHE = HERE / ".cache"
HOST = "mocap.cs.cmu.edu"
BASE = f"https://{HOST}/subjects"
PAUSE_S = 1.0


def load_clips(path: Path = HERE / "clips.toml") -> list[dict]:
    with open(path, "rb") as f:
        return tomllib.load(f)["clip"]


def trial_parts(trial: str) -> tuple[str, str]:
    """'141_05' -> ('141', '05'); subject numbers are written without padding here (the URLs pad subjects 1-9, see needed_files), trials with two digits."""
    s, t = trial.split("_")
    return str(int(s)), f"{int(t):02d}"


def needed_files(clips: list[dict]) -> list[tuple[str, str]]:
    """(cache file name, url) for every distinct ASF and AMC the clips need, in a stable order."""
    out: dict[str, str] = {}
    for c in clips:
        if "trial" not in c:   # a clip baked from the procedural keeper (tools/anim/bake_procedural.py) has no trial
            continue
        s, t = trial_parts(c["trial"])
        p = f"{int(s):02d}"   # the site pads subjects 1-9 to two digits (/subjects/09/09_01.amc); the cache keeps the plain number
        out.setdefault(f"{s}.asf", f"{BASE}/{p}/{p}.asf")
        out.setdefault(f"{s}_{t}.amc", f"{BASE}/{p}/{p}_{t}.amc")
    return sorted(out.items())


def _context_for(url: str) -> ssl.SSLContext:
    host = urllib.parse.urlparse(url).hostname
    if host != HOST:
        raise ValueError(f"refusing to fetch from {host}: only {HOST} is allowed")
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE   # this host only: its chain is broken
    return ctx


def fetch(url: str, dest: Path) -> int:
    req = urllib.request.Request(url, headers={"User-Agent": "football-sim-anim-fetch/1"})
    with urllib.request.urlopen(req, context=_context_for(url), timeout=60) as r:
        data = r.read()
    if not data or data.lstrip()[:1] in (b"<",):
        raise RuntimeError(f"{url}: not a motion file (got {len(data)} bytes starting {data[:40]!r})")
    tmp = dest.with_suffix(dest.suffix + ".part")
    tmp.write_bytes(data)
    tmp.replace(dest)
    return len(data)


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--list", action="store_true", help="print the needed files and whether they are cached")
    ap.add_argument("--config", type=Path, default=HERE / "clips.toml", help="clip list to fetch the trials of (default: the keeper's clips.toml)")
    args = ap.parse_args(argv)
    files = needed_files(load_clips(args.config))
    CACHE.mkdir(exist_ok=True)
    if args.list:
        for name, url in files:
            print(("cached " if (CACHE / name).exists() else "missing ") + url)
        return 0
    first, failed = True, 0
    for name, url in files:
        dest = CACHE / name
        if dest.exists():
            continue
        if not first:
            time.sleep(PAUSE_S)
        first = False
        try:
            n = fetch(url, dest)
            print(f"fetched {url} ({n} bytes)")
        except Exception as e:  # keep going: report every missing file
            failed += 1
            print(f"FAILED {url}: {e}", file=sys.stderr)
    print(f"{len(files)} files needed, {failed} failed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
