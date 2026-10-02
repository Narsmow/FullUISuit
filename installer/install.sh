#!/usr/bin/env bash
# FullUI installer for Linux / macOS.
# Uses PowerShell (pwsh) if it is installed, otherwise the bundled Python 3 version.
# It never installs anything by itself. Both do exactly the same steps.
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PS1="$HERE/install.ps1"
PY="$HERE/fullui_install.py"
if [ -z "${FULLUI_FORCE_PY:-}" ] && command -v pwsh >/dev/null 2>&1 && [ -f "$PS1" ]; then
  exec pwsh -NoProfile -File "$PS1" -LogDir "$HERE" "$@"
elif command -v python3 >/dev/null 2>&1 && [ -f "$PY" ]; then
  exec python3 "$PY" -LogDir "$HERE" "$@"
fi
echo
echo "FullUI installer cannot start: it needs either PowerShell (pwsh) or Python 3 on this computer,"
echo "and found neither (or the installer files are incomplete)."
echo "Easiest fix on Debian/Ubuntu:   sudo apt install python3     then run this again."
echo "Nothing was changed."
exit 1
