$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

python -m PyInstaller `
  --noconfirm `
  --clean `
  --windowed `
  --onedir `
  --name ClashSharp-Installer `
  --icon .\Logo.png `
  --add-data "ui;ui" `
  --add-data "payload;payload" `
  --add-data "Logo.png;." `
  --exclude-module PyQt5 `
  --exclude-module PyQt6 `
  --exclude-module PySide2 `
  --exclude-module PySide6 `
  .\main.py