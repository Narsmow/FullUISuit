#!/usr/bin/env bash
# Proves the built plugin only uses Jellyfin APIs that exist, unchanged, in every supported server version.
# usage: tools/abi-matrix.sh [plugin.dll] [versions...]   (defaults: freshly built Release dll, 10.11.6..10.11.11)
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DLL="${1:-$ROOT/server/Jellyfin.Plugin.FullUI/bin/Release/net9.0/Jellyfin.Plugin.FullUI.dll}"
shift || true
VERSIONS=("$@"); [ ${#VERSIONS[@]} -eq 0 ] && VERSIONS=(10.11.6 10.11.7 10.11.8 10.11.9 10.11.10 10.11.11)
WORK="${ABI_WORK:-$(mktemp -d)}"
dotnet build "$ROOT/tools/abicheck" -c Release -o "$WORK/tool" >/dev/null
fail=0
for v in "${VERSIONS[@]}"; do
  d="$WORK/jf-$v"; mkdir -p "$d"
  cat > "$d/p.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework><OutputType>Library</OutputType><CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies></PropertyGroup>
<ItemGroup><PackageReference Include="Jellyfin.Controller" Version="$v" /></ItemGroup></Project>
XML
  dotnet build "$d/p.csproj" -c Release -o "$d/out" >/dev/null 2>&1 || { echo "Jellyfin $v: could not restore package"; fail=1; continue; }
  printf 'Jellyfin %s: ' "$v"
  dotnet "$WORK/tool/AbiCheck.dll" "$DLL" "$d/out" || fail=1
done
exit $fail
