#!/usr/bin/env bash
# Runs every installer test against the mock Jellyfin server.
#   bash installer/tests/run-tests.sh           # all implementations that can run here
#   bash installer/tests/run-tests.sh --only happy_path
# Needs python3. PowerShell tests run only if pwsh is installed (set PWSH=/path/to/pwsh otherwise).
# Windows PowerShell 5.1: python installer/tests/run_tests.py --ps-exe powershell   (done by CI on windows-latest)
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
rc=0
cd "$ROOT"

echo "== syntax checks =="
python3 -m py_compile "$ROOT/installer/fullui_install.py" "$ROOT/installer/make_release.py" "$ROOT/installer/build_single.py" "$HERE/mock_jellyfin.py" "$HERE/run_tests.py" || rc=1
bash -n "$ROOT/installer/install.sh" || rc=1
PW="${PWSH:-$(command -v pwsh || true)}"
[ -z "$PW" ] && [ -x /opt/pwsh/pwsh ] && PW=/opt/pwsh/pwsh
if [ -n "$PW" ]; then
  F="$ROOT/installer/install.ps1" "$PW" -NoProfile -Command '$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile($env:F,[ref]$null,[ref]$e); if($e){$e|%{Write-Host $_.Message $_.Extent.StartLineNumber}; exit 1}else{Write-Host "install.ps1 parses OK"}' || rc=1
else
  echo "pwsh not found: PowerShell script NOT parsed/tested here"
fi
python3 - <<'PY' || rc=1
import yaml,sys,glob
for f in glob.glob('.github/workflows/*.yml'):
    yaml.safe_load(open(f)); print('yaml ok', f)
PY

if command -v actionlint >/dev/null 2>&1; then
  actionlint .github/workflows/ci.yml .github/workflows/release.yml .github/workflows/installer-tests.yml && echo "actionlint ok" || rc=1
else
  echo "actionlint not installed: workflows only YAML-parsed here (CI does not run it either; install from github.com/rhysd/actionlint)"
fi

echo "== release helpers =="
python3 "$HERE/test_release.py" || rc=1

echo "== installer vs plugin source consistency =="
python3 "$HERE/test_consistency.py" || rc=1

echo "== mock Jellyfin scenarios =="
python3 "$HERE/run_tests.py" "$@" || rc=1

echo "== single-file bundles =="
python3 "$HERE/test_bundles.py" || rc=1

[ $rc -eq 0 ] && echo "ALL OK" || echo "SOME TESTS FAILED"
exit $rc
