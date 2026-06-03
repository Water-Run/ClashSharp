$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

cargo build --release
