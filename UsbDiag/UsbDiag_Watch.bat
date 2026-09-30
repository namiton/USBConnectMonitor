@echo off
rem Copyright (c) 2026 @Namiton
rem SPDX-License-Identifier: MIT
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0UsbDiag.ps1" -Watch
pause
