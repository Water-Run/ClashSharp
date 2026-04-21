from __future__ import annotations

import json
import os
import shutil
import threading
import time
import winreg
from pathlib import Path


WEBVIEW2_REG_PATHS = [
    r"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
    r"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
]


def has_webview2_runtime() -> bool:
    hives = [winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER]

    for hive in hives:
        for subkey in WEBVIEW2_REG_PATHS:
            try:
                with winreg.OpenKey(hive, subkey) as key:
                    value, _ = winreg.QueryValueEx(key, "pv")
                    if isinstance(value, str) and value.strip() and value != "0.0.0.0":
                        return True
            except FileNotFoundError:
                continue
            except OSError:
                continue

    return False


class InstallerApi:
    def __init__(self, base_dir: Path):
        self.base_dir = Path(base_dir)
        self.window = None

    def get_initial_state(self) -> dict:
        default_dir = Path(os.environ.get(
            "ProgramFiles", r"C:\Program Files")) / "ClashSharp"
        payload_dir = self.base_dir / "payload"

        return {
            "app_name": "ClashSharp",
            "default_install_dir": str(default_dir),
            "payload_exists": payload_dir.exists(),
        }

    def start_install(self, install_dir: str) -> dict:
        thread = threading.Thread(
            target=self._run_install,
            args=(Path(install_dir),),
            daemon=True,
        )
        thread.start()
        return {"ok": True}

    def _emit(self, event_name: str, payload: dict) -> None:
        if self.window is None:
            return
        data = json.dumps(payload, ensure_ascii=False)
        self.window.evaluate_js(f"window.installer.{event_name}({data})")

    def _run_install(self, install_dir: Path) -> None:
        payload_dir = self.base_dir / "payload"

        if not payload_dir.exists():
            self._emit("onError", {"message": "payload 目录不存在"})
            return

        entries = [
            p for p in payload_dir.iterdir()
            if p.name != ".gitkeep"
        ]

        if not entries:
            self._emit("onError", {"message": "payload 目录为空，请先放入待安装文件"})
            return

        try:
            install_dir.mkdir(parents=True, exist_ok=True)

            total = len(entries)
            for index, entry in enumerate(entries, start=1):
                target = install_dir / entry.name

                self._emit(
                    "onProgress",
                    {
                        "percent": int((index - 1) / total * 100),
                        "text": f"正在复制 {entry.name}",
                    },
                )

                if entry.is_dir():
                    shutil.copytree(entry, target, dirs_exist_ok=True)
                else:
                    shutil.copy2(entry, target)

                time.sleep(0.08)

            self._emit(
                "onProgress",
                {
                    "percent": 100,
                    "text": "安装完成",
                },
            )

            self._emit(
                "onDone",
                {
                    "install_dir": str(install_dir),
                },
            )

        except Exception as exc:
            self._emit("onError", {"message": str(exc)})
