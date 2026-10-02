#!/bin/bash
# Installs what FullUI needs in a Claude Code cloud session: .NET SDK (apt; dot.net hosts are blocked in the sandbox),
# web npm packages, and NuGet restore. Idempotent, non-interactive. Cloud sessions only.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

cd "${CLAUDE_PROJECT_DIR:-$(pwd)}"

if ! command -v dotnet >/dev/null 2>&1; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq
  apt-get install -y -qq dotnet-sdk-10.0
fi

if [ -f web/package.json ]; then
  (cd web && npm install --no-audit --no-fund --loglevel=error)
fi

if [ -f server/FullUI.slnx ]; then
  (cd server && dotnet restore FullUI.slnx --verbosity quiet)
fi

# Playwright's Chromium is pre-installed; never download browsers.
echo 'export PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1' >> "${CLAUDE_ENV_FILE:-/dev/null}"
echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1' >> "${CLAUDE_ENV_FILE:-/dev/null}"
