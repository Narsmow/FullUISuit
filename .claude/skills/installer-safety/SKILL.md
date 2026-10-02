---
name: installer-safety
description: Rules for the FullUI one-file installer (installer/install.ps1, fullui_install.py, install.sh, Install-FullUI.cmd, release workflow, manifest) that a non-coder runs against a live Jellyfin. Use whenever you edit the installer or its tests, the release/CI workflows, manifest generation, or docs/INSTALL.md.
---

# Installer safety rules

The user will run ONE file on their real server with no coding knowledge. Assume every step can fail and every message will be read by a beginner.

## Behaviour that real Jellyfin enforces (the mock must too)
- `POST /Users/AuthenticateByName` needs the header `Authorization: MediaBrowser Client="...", Device="...", DeviceId="...", Version="..."` (no Token). Without it the server returns HTTP 500. A mock that accepts header-less logins hides this.
- `GET /Plugins` lists every installed version; superseded old versions stay listed (status `Superseded`) next to the new one. Pick the highest version (prefer Active/Restart) for checks; uninstall must delete every matching entry.
- A POST that follows an HTTP redirect becomes a GET. After probing, use the final redirected URL as the base.
- Repositories can be changed with `GET/POST /Repositories`; avoid read-modify-write of all `/System/Configuration`.
- Minimum supported server: 10.11.6 (older: refuse with plain-English text, offer force).

## Non-negotiables
- Passwords, API keys and TMDB keys are never printed, logged, or put in temp files; logs are scrubbed. Prefer `FULLUI_*` environment variables to command-line secrets.
- Every failure prints: what happened in plain English, the most likely fix in 1-2 sentences, and that re-running is safe. Full technical detail goes only to the timestamped log file.
- Idempotent: a second run changes nothing and restarts nothing.
- Never upgrade or remove a plugin the user installed for other reasons (File Transformation is shared) without asking.
- TLS validation stays on; `-Insecure` must be explicit, scoped to the Jellyfin host.
- Disclose what is added: the third-party plugin repository, and anything downloaded and executed (Ollama).

## Windows PowerShell 5.1 pitfalls (cannot be tested in the Linux sandbox; be conservative)
No PS7-only syntax; `$ErrorActionPreference='Stop'` turns native stderr into terminating errors (wrap native calls); set `$ProgressPreference='SilentlyContinue'` before big downloads; enable TLS 1.2; `ConvertTo-Json -Depth` explicit; `.cmd` files need CRLF.

## Testing
`bash installer/tests/run-tests.sh` runs the PowerShell and Python installers against `mock_jellyfin.py`. Add a scenario for every real-server behaviour above and check it FAILS on the unfixed code. Keep installer GUID and plugin-config property names in sync with `Plugin.cs`/`PluginConfiguration.cs` (a consistency test guards this).
