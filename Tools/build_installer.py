#!/usr/bin/env python3
"""
Builds a desktop distribution package for Clash#.

The script creates an ICO from the repository logo, publishes the WinUI app as a
self-contained win-x64 executable, and archives the publish directory as a
ClashSharp-Installer zip package.
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

try:
    from PIL import Image
except ImportError as exc:  # pragma: no cover - exercised by users without Pillow
    raise SystemExit("Pillow is required. Install it with: py -m pip install pillow") from exc


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "ClashSharp" / "ClashSharp" / "ClashSharp.csproj"
DEFAULT_OUTPUT = ROOT / "artifacts" / "installer"
LOGO_CANDIDATES = [
    ROOT / "Logo.png",
    ROOT / "ClashSharp" / "ClashSharp" / "Assets" / "Logo.png",
]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build the Clash# desktop installer package.")
    parser.add_argument("--configuration", default="Release", choices=["Debug", "Release"])
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--version", default="1.0.0.0")
    parser.add_argument("--no-clean", action="store_true", help="Do not remove the output directory before building.")
    return parser.parse_args()


def resolve_logo() -> Path:
    for candidate in LOGO_CANDIDATES:
        if candidate.exists():
            return candidate
    raise FileNotFoundError("No logo PNG was found for icon generation.")


def build_icon(output_dir: Path) -> Path:
    output_dir.mkdir(parents=True, exist_ok=True)
    icon_path = output_dir / "ClashSharp.ico"
    logo_path = resolve_logo()
    image = Image.open(logo_path).convert("RGBA")
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    image.save(icon_path, format="ICO", sizes=sizes)
    return icon_path


def publish_app(configuration: str, publish_dir: Path, icon_path: Path) -> None:
    publish_dir.mkdir(parents=True, exist_ok=True)
    command = [
        "dotnet",
        "publish",
        str(PROJECT),
        "-c",
        configuration,
        "-p:Platform=x64",
        "-r",
        "win-x64",
        "--self-contained",
        "true",
        f"-p:ApplicationIcon={icon_path}",
        f"-p:PublishDir={publish_dir}{os.sep}",
    ]
    subprocess.run(command, cwd=ROOT, check=True)


def write_install_notes(publish_dir: Path) -> None:
    notes = publish_dir / "README-INSTALL.txt"
    notes.write_text(
        "Clash# desktop package\n"
        "\n"
        "Run ClashSharp.exe from this directory. Keep the Binaries directory next to the executable.\n",
        encoding="utf-8",
    )


def create_zip(publish_dir: Path, output_dir: Path, version: str) -> Path:
    archive_path = output_dir / f"ClashSharp-Installer-{version}-win-x64.zip"
    if archive_path.exists():
        archive_path.unlink()
    with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(publish_dir.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(publish_dir))
    return archive_path


def main() -> int:
    args = parse_args()
    output_dir = args.output.resolve()
    publish_dir = output_dir / "publish"
    icon_dir = output_dir / "icon"

    if output_dir.exists() and not args.no_clean:
        shutil.rmtree(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    icon_path = build_icon(icon_dir)
    publish_app(args.configuration, publish_dir, icon_path)
    shutil.copy2(icon_path, publish_dir / icon_path.name)
    write_install_notes(publish_dir)
    archive_path = create_zip(publish_dir, output_dir, args.version)

    print(f"Built installer package: {archive_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
