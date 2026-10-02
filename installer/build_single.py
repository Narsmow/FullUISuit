#!/usr/bin/env python3
"""Builds the one-file installers into installer/dist/ (or the given directory):
   FullUI-Installer.cmd  Windows: double-click. Contains install.ps1.
   FullUI-Installer.sh   Linux/macOS: bash FullUI-Installer.sh. Contains install.ps1 + fullui_install.py.
Usage: python3 installer/build_single.py [outdir]
"""
import os
import sys

here = os.path.dirname(os.path.abspath(__file__))
out = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "dist")
os.makedirs(out, exist_ok=True)
ps1 = open(os.path.join(here, "install.ps1"), encoding="utf-8").read().replace("\r\n", "\n")
py = open(os.path.join(here, "fullui_install.py"), encoding="utf-8").read().replace("\r\n", "\n")
for name, txt in (("install.ps1", ps1), ("fullui_install.py", py)):
    bad = [c for c in txt if ord(c) > 127]
    if bad:
        sys.exit("non-ASCII characters in %s: %r" % (name, bad[:5]))

CMD = r"""@echo off
setlocal
title FullUI Installer
set "FULLUI_SELF=%~f0"
set "FULLUI_DIR=%~dp0"
if "%FULLUI_DIR:~-1%"=="\" set "FULLUI_DIR=%FULLUI_DIR:~0,-1%"
set "FULLUI_PS=%TEMP%\FullUI-install-%RANDOM%%RANDOM%.ps1"
set "FULLUI_MARK=%FULLUI_PS%.started"
set "UNATT="
for %%A in (%*) do if /i "%%~A"=="-Unattended" set "UNATT=1"
where powershell >nul 2>nul
if errorlevel 1 goto nops
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t=[IO.File]::ReadAllText($env:FULLUI_SELF); $i=$t.IndexOf(([string][char]10+'#PAYLOAD-BEGIN'),[StringComparison]::Ordinal); $j=$t.IndexOf([char]10,$i+1)+1; [IO.File]::WriteAllText($env:FULLUI_PS,$t.Substring($j),(New-Object Text.UTF8Encoding($true)))"
if not exist "%FULLUI_PS%" goto unpackfail
powershell -NoProfile -ExecutionPolicy Bypass -File "%FULLUI_PS%" -LogDir "%FULLUI_DIR%" %*
set "RC=%ERRORLEVEL%"
del "%FULLUI_PS%" >nul 2>nul
if not exist "%FULLUI_MARK%" goto blocked
del "%FULLUI_MARK%" >nul 2>nul
goto finish
:blocked
echo.
echo The installer could not start. Windows or your antivirus (or a company/school policy) blocked it.
echo   1. Right-click this file, choose Properties, tick "Unblock" if you see it, press OK, then run it again.
echo   2. Try moving it to your Desktop and running it from there.
echo   3. On a company or school computer, ask the person who manages it. Nothing was changed on your Jellyfin.
echo   4. Or install by hand - see docs\INSTALL.md ("Install by hand").
set "RC=1"
goto finish
:unpackfail
echo.
echo Could not unpack the installer into your temporary folder (antivirus or a full disk?).
echo Try moving this file to your Desktop and running it again.
set "RC=1"
goto finish
:nops
echo.
echo Windows PowerShell was not found on this computer, so the installer cannot run.
set "RC=1"
:finish
echo.
if not defined UNATT (
  echo Press any key to close this window...
  pause >nul
)
exit /b %RC%
#PAYLOAD-BEGIN
"""
with open(os.path.join(out, "FullUI-Installer.cmd"), "w", newline="") as f:
    f.write((CMD + ps1).replace("\n", "\r\n"))

SH = r"""#!/usr/bin/env bash
# FullUI one-file installer for Linux / macOS.  Run:  bash FullUI-Installer.sh
# Uses PowerShell (pwsh) if present, else Python 3. It does not install PowerShell or Python for you.
set -u
SELF="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
HERE="$(dirname "$SELF")"
TMP="$(mktemp -d 2>/dev/null || echo /tmp/fullui-$$)"; mkdir -p "$TMP"
trap 'rm -rf "$TMP"' EXIT
extract() { awk -v b="#@@$1-BEGIN" -v e="#@@$1-END" '$0==b{f=1;next} $0==e{f=0} f' "$SELF" > "$2"; }
if [ -z "${FULLUI_FORCE_PY:-}" ] && command -v pwsh >/dev/null 2>&1; then
  extract PS1 "$TMP/install.ps1"
  pwsh -NoProfile -File "$TMP/install.ps1" -LogDir "$HERE" "$@"; exit $?
elif command -v python3 >/dev/null 2>&1; then
  extract PY "$TMP/fullui_install.py"
  python3 "$TMP/fullui_install.py" -LogDir "$HERE" "$@"; exit $?
fi
echo
echo "FullUI installer cannot start: it needs either PowerShell (pwsh) or Python 3 on this computer."
echo "Easiest fix on Debian/Ubuntu:   sudo apt install python3     then run this again."
echo "Nothing was changed."
exit 1
"""
with open(os.path.join(out, "FullUI-Installer.sh"), "w", newline="") as f:
    f.write(SH + "#@@PS1-BEGIN\n" + ps1 + "#@@PS1-END\n#@@PY-BEGIN\n" + py + "#@@PY-END\n")
os.chmod(os.path.join(out, "FullUI-Installer.sh"), 0o755)
print("built", out)
