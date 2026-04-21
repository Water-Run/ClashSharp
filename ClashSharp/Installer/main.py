from __future__ import annotations

import ctypes
import sys
from pathlib import Path

import webview

from backend import InstallerApi, has_webview2_runtime


APP_TITLE = "ClashSharp Installer"


def get_base_dir() -> Path:
    if hasattr(sys, "_MEIPASS"):
        return Path(sys._MEIPASS)
    return Path(__file__).resolve().parent


def message_box(text: str, title: str = APP_TITLE) -> None:
    ctypes.windll.user32.MessageBoxW(0, text, title, 0x10)


def main() -> None:
    base_dir = get_base_dir()

    if not has_webview2_runtime():
        message_box(
            "未检测到 Microsoft Edge WebView2 Runtime。\n\n"
            "Windows 11 通常已内置该运行时，但该设备当前不可用。\n"
            "请先安装 WebView2 Runtime 后再运行安装器。"
        )
        raise SystemExit(1)

    api = InstallerApi(base_dir=base_dir)

    window = webview.create_window(
        title=APP_TITLE,
        url=(base_dir / "ui" / "index.html").as_uri(),
        js_api=api,
        width=980,
        height=640,
        resizable=False,
    )

    api.window = window
    webview.start(debug=False)


if __name__ == "__main__":
    main()
