@echo off
rem Copyright (c) 2026 @Namiton
rem SPDX-License-Identifier: MIT
rem Double-click: diagnose.  UsbDiag.bat -Watch : live monitor
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0UsbDiag.ps1" %*
pause
