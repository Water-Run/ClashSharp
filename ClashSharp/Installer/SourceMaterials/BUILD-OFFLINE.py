"""Verify and cross-compile the fixed mihomo inputs without Go downloads."""
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import tarfile
import time


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_inputs(root, manifest):
    if manifest.get("schemaVersion") != 1 or manifest.get("productVersion") != "1.0.0":
        raise RuntimeError("Unsupported source-materials manifest")
    if (manifest.get("repository") != "MetaCubeX/mihomo"
            or re.fullmatch(r"[0-9a-f]{40}", manifest.get("sourceCommit", "")) is None
            or re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+", manifest.get("version", "")) is None
            or re.fullmatch(r"go[0-9]+\.[0-9]+\.[0-9]+", manifest.get("goVersion", "")) is None):
        raise RuntimeError("Invalid source identity or toolchain pin")
    files = manifest.get("files", [])
    expected = {"source.tar.gz", "vendor.tar.gz", "toolchain.tar.gz", "ca-certificates.crt",
                "LICENSE", "README.md", "BUILD-OFFLINE.py"}
    if len(files) != len(expected) or {entry.get("path") for entry in files} != expected:
        raise RuntimeError("Unexpected source-materials input set")
    for entry in files:
        if (type(entry.get("length")) is not int or not 0 < entry["length"] <= 268435456
                or re.fullmatch(r"[0-9a-f]{64}", entry.get("sha256", "")) is None):
            raise RuntimeError("Invalid input digest or length")
        path = root / entry["path"]
        if path.is_symlink() or not path.is_file() or path.stat().st_size != entry["length"]:
            raise RuntimeError("Input is missing, unsafe or has the wrong length: " + entry["path"])
        if digest(path) != entry["sha256"]:
            raise RuntimeError("Input digest mismatch: " + entry["path"])


def run_build(root):
    if sys.version_info < (3, 12) or platform.system() != "Linux" or platform.machine() != "x86_64":
        raise RuntimeError("Use Python 3.12 or later on Linux x86_64")
    manifest_path = root / "SOURCE-MATERIALS.json"
    if manifest_path.is_symlink() or manifest_path.stat().st_size > 65536:
        raise RuntimeError("Unsafe source-materials manifest")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    verify_inputs(root, manifest)
    work = root / "build"
    work.mkdir(mode=0o700, exist_ok=False)
    result = {"state": "running", "sourceCommit": manifest["sourceCommit"],
              "startedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
              "goDownloadsDisabled": True, "binaryExecuted": False,
              "byteForByteReproduction": False}
    result_path = work / "result.json"
    result_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
    try:
        for name, directory in (("source.tar.gz", "source"), ("toolchain.tar.gz", "toolchain")):
            destination = work / directory
            destination.mkdir(mode=0o700)
            with tarfile.open(root / name) as archive:
                archive.extractall(destination, filter="data")
        source = work / "source" / ("mihomo-" + manifest["sourceCommit"])
        with tarfile.open(root / "vendor.tar.gz") as archive:
            archive.extractall(source, filter="data")
        shutil.copyfile(root / "ca-certificates.crt", source / "component/ca/ca-certificates.crt")
        go_root = work / "toolchain/go"
        go = str(go_root / "bin/go")
        env = os.environ.copy()
        env.update({"GOROOT": str(go_root), "GOTOOLCHAIN": "local", "GOENV": "off",
                    "GOWORK": "off", "GOPROXY": "off", "GOSUMDB": "off", "GOMAXPROCS": "2",
                    "GOCACHE": str(work / "go-cache"), "GOPATH": str(work / "go-path"),
                    "GOMODCACHE": str(work / "go-module-cache"), "GOOS": "windows",
                    "GOARCH": "amd64", "GOAMD64": "v1", "CGO_ENABLED": "0"})
        version = subprocess.check_output([go, "version"], env=env, text=True).strip()
        if version != "go version " + manifest["goVersion"] + " linux/amd64":
            raise RuntimeError("Unexpected pinned toolchain version")
        result["toolchainVersion"] = version
        output = work / "mihomo-windows-amd64-v1.exe"
        flags = ("-extldflags --static -X github.com/metacubex/mihomo/constant.Version=" + manifest["version"]
                 + " -X github.com/metacubex/mihomo/constant.BuildTime=source-build -w -s -buildid=")
        command = [go, "build", "-p=2", "-mod=vendor", "-buildvcs=false", "-trimpath",
                   "-tags=with_gvisor", "-ldflags=" + flags, "-o", str(output), "."]
        with (work / "build.log").open("w", encoding="utf-8") as log:
            process = subprocess.Popen(command, cwd=source, env=env, stdout=log,
                                       stderr=subprocess.STDOUT, start_new_session=True)
            try:
                code = process.wait(timeout=900)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
                raise RuntimeError("Owned compilation exceeded fifteen minutes") from None
        if code != 0:
            raise RuntimeError("Compilation failed with exit code " + str(code))
        info = subprocess.check_output([go, "version", "-m", str(output)], env=env, text=True)
        (work / "build-info.txt").write_text(info, encoding="utf-8")
        result.update({"state": "completed", "binaryLength": output.stat().st_size,
                       "binarySha256": digest(output)})
    except Exception as error:
        result.update({"state": "failed", "error": str(error)})
        raise
    finally:
        result["finishedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        result_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
        print(json.dumps(result))


if __name__ == "__main__":
    run_build(Path(__file__).resolve().parent)
