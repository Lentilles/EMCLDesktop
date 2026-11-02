"""Package a dotnet publish directory; macOS packaging runs on a Mac."""

import argparse
import json
import plistlib
import re
import shutil
import subprocess
import tarfile
import tempfile
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile


def package(args):
    root = Path(__file__).resolve().parents[2]
    publish = args.publish.resolve()
    output = args.output.resolve()
    emcl = args.emcl.resolve()
    executable = "EMCLDesktop.Desktop" + (".exe" if args.rid.startswith("win-") else "")
    if not (publish / executable).is_file():
        raise FileNotFoundError(publish / executable)
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?", args.version):
        raise ValueError("Expected a version such as 1.2.3 or 1.2.3-rc.1")
    output.mkdir(parents=True, exist_ok=True)
    name = f"EMCLDesktop-{args.version}-{args.rid}"

    with tempfile.TemporaryDirectory(prefix="emcldesktop-package-") as temporary:
        stage = Path(temporary) / name
        stage.mkdir()
        if args.rid.startswith("osx-"):
            app = stage / "EMCL Desktop.app"
            contents = app / "Contents"
            binaries = contents / "MacOS"
            resources = contents / "Resources"
            resources.mkdir(parents=True)
            shutil.copy2(root / "EMCLDesktop/Assets/emcl-desktop.icns", resources / "emcl-desktop.icns")
            shutil.copytree(publish, binaries)
            (binaries / executable).chmod(0o755)
            with (contents / "Info.plist").open("wb") as stream:
                plistlib.dump({
                    "CFBundleExecutable": executable,
                    "CFBundleIdentifier": "com.lentilles.emcldesktop",
                    "CFBundleName": "EMCL Desktop",
                    "CFBundleDisplayName": "EMCL Desktop",
                    "CFBundleIconFile": "emcl-desktop.icns",
                    "CFBundlePackageType": "APPL",
                    "CFBundleInfoDictionaryVersion": "6.0",
                    "CFBundleShortVersionString": args.version.split("-")[0],
                    "CFBundleVersion": args.version.split("-")[0],
                    "NSHighResolutionCapable": True,
                }, stream)
            documentation = resources
        else:
            shutil.copytree(publish, stage, dirs_exist_ok=True)
            if args.rid.startswith("linux-"):
                (stage / executable).chmod(0o755)
                shutil.copy2(root / "EMCLDesktop/Assets/emcl-desktop.png", stage / "emcl-desktop.png")
            documentation = stage

        shutil.copy2(root / "README.md", documentation / "README.md")
        shutil.copy2(root / "LICENSE", documentation / "LICENSE")
        licenses = documentation / "licenses"
        licenses.mkdir()
        shutil.copy2(emcl / "LICENSE", licenses / "EMCL-LICENSE")
        (documentation / "build-info.json").write_text(json.dumps({
            "version": args.version,
            "runtime": args.rid,
            "desktopCommit": subprocess.check_output(
                ["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip(),
            "emclCommit": subprocess.check_output(
                ["git", "-C", str(emcl), "rev-parse", "HEAD"], text=True).strip(),
        }, indent=2) + "\n", encoding="utf-8")

        if args.rid.startswith("osx-"):
            # Ad-hoc signing satisfies local code-integrity requirements. It is
            # not a Developer ID signature and does not provide notarization.
            # MacOS is a code directory: sign every payload file, including
            # managed DLLs and JSON, before the apphost and the enclosing bundle.
            # See https://docs.avaloniaui.net/docs/deployment/macos
            for binary in sorted(binaries.rglob("*")):
                if not binary.is_file() or binary.is_symlink() or binary == binaries / executable:
                    continue
                subprocess.run(["codesign", "--force", "--sign", "-",
                                "--timestamp=none", str(binary)], check=True)
                subprocess.run(["codesign", "--verify", "--strict", str(binary)], check=True)
            subprocess.run(["codesign", "--force", "--sign", "-",
                            "--timestamp=none", str(binaries / executable)], check=True)
            subprocess.run(["codesign", "--force", "--sign", "-",
                            "--timestamp=none", str(app)], check=True)
            subprocess.run(["codesign", "--verify", "--strict", str(app)], check=True)
            (stage / "Applications").symlink_to("/Applications", target_is_directory=True)
            archive = output / f"{name}.dmg"
            subprocess.run(["hdiutil", "create", "-volname", "EMCL Desktop",
                            "-srcfolder", str(stage), "-ov", "-format", "UDZO",
                            str(archive)], check=True)
            subprocess.run(["hdiutil", "verify", str(archive)], check=True)
        elif args.rid.startswith("linux-"):
            archive = output / f"{name}.tar.gz"
            with tarfile.open(archive, "w:gz") as bundle:
                bundle.add(stage, arcname=name)
        else:
            archive = output / f"{name}.zip"
            with ZipFile(archive, "w", compression=ZIP_DEFLATED) as bundle:
                for file in sorted(stage.rglob("*")):
                    if file.is_file():
                        bundle.write(file, arcname=file.relative_to(stage.parent))
    print(archive)
    return archive


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=[
        "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"])
    parser.add_argument("--version", required=True)
    parser.add_argument("--publish", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--emcl", required=True, type=Path)
    package(parser.parse_args())
