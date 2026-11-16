"""Collect only uniquely named, complete Velopack feeds and installers."""
import json
import shutil
import sys
from pathlib import Path

source, target, version = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3]
rids = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
target.mkdir(parents=True, exist_ok=True)
collected = set(path.name for path in target.iterdir())
for rid in rids:
    directory = source / f"velopack-{rid}"
    app_id = f"EMCLDesktop-{rid}"
    full_packages = list(directory.glob(f"{app_id}-{version}*-full.nupkg"))
    feed = directory / f"releases.{rid}.json"
    extension = ".exe" if rid.startswith("win-") else ".AppImage" if rid.startswith("linux-") else ".pkg"
    installers = [p for p in directory.glob(f"{app_id}*{extension}") if p.is_file()]
    if len(full_packages) != 1 or not full_packages[0].stat().st_size or not feed.is_file() or len(installers) != 1 or not installers[0].stat().st_size:
        raise SystemExit(f"Incomplete Velopack package for {rid}: {installers}")
    full = full_packages[0]
    data = json.loads(feed.read_text(encoding="utf-8"))
    if not any(asset.get("FileName") == full.name and asset.get("PackageId") == app_id
               and asset.get("Type") == "Full" and asset.get("Version") == version
               for asset in data.get("Assets", [])):
        raise SystemExit(f"Feed for {rid} does not reference {full.name}")
    for path in (full, feed, *installers):
        if path.name in collected:
            raise SystemExit(f"Duplicate release asset: {path.name}")
        collected.add(path.name)
        shutil.copy2(path, target / path.name)
