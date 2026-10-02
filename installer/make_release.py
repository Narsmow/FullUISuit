#!/usr/bin/env python3
"""Release helpers used by .github/workflows/release.yml (also runnable locally).

  pack      Build FullUI_<version>.zip (plugin dll + optional extra dlls + meta.json).
  manifest  Add a version entry to manifest.json (keeps guid/name/etc., keeps older versions).
  version   Print the 4-part plugin version for a tag (used by release.yml so there is one mapping only).

Tag -> version: v1.2.3 -> 1.2.3.0.  A prerelease v1.2.3-rc1 must sort BELOW the final 1.2.3.0 and must not equal it, so it
maps to the previous patch with a high 4th number: 1.2.2.9001 (v1.3.0-rc2 -> 1.2.9999.9002, v2.0.0-rc1 -> 1.9999.9999.9001).

Examples:
  python3 installer/make_release.py pack --tag v1.2.3 --bin-dir server/Jellyfin.Plugin.FullUI/bin/Release/net9.0 --out-dir out
  python3 installer/make_release.py manifest --manifest manifest.json --tag v1.2.3 --zip out/FullUI_1.2.3.0.zip \
        --repo Narsmow/FullUISuit --changelog "What changed"
"""
import argparse
import fnmatch
import hashlib
import json
import os
import re
import sys
import zipfile
from datetime import datetime, timezone

GUID = "7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57"
DLL = "Jellyfin.Plugin.FullUI.dll"
DEFAULT_ABI = "10.11.6.0"  # oldest Jellyfin the plugin is built and checked against
# Libraries Jellyfin does NOT already provide to plugins and that must therefore ship inside the zip.
# Today there are none: the plugin is a class library, so NuGet DLLs are not copied to the build output, and
# Newtonsoft.Json is provided by Jellyfin itself. If the build output ever contains another DLL, `pack` FAILS
# until it is added here (ship it) or to PROVIDED_BY_JELLYFIN (deliberately not shipped).
EXTRA_DLLS = []
PROVIDED_BY_JELLYFIN = ["Jellyfin.*.dll", "MediaBrowser.*.dll", "Emby.*.dll", "Newtonsoft.Json.dll", "Microsoft.*.dll", "System.*.dll"]


def version_from_tag(tag):
    m = re.match(r"^v?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:-([0-9A-Za-z.\-]+))?(?:\+[0-9A-Za-z.\-]+)?$", tag.strip())
    if not m:
        sys.exit("Tag %r is not like v1.2.3 or v1.2.3-rc1" % tag)
    major, minor, patch = int(m.group(1)), int(m.group(2)), int(m.group(3))
    rev = int(m.group(4) or 0)
    pre = m.group(5)
    if not pre:
        return "%d.%d.%d.%d" % (major, minor, patch, rev)
    if m.group(4) is not None:
        sys.exit("Prerelease tags must look like v1.2.3-rc1 (no 4th number), got %r" % tag)
    digits = re.search(r"\d+", pre)
    n = min(int(digits.group(0)), 999) if digits else 1
    if patch > 0:
        patch -= 1
    elif minor > 0:
        minor, patch = minor - 1, 9999
    elif major > 0:
        major, minor, patch = major - 1, 9999, 9999
    else:
        sys.exit("A prerelease of 0.0.0 cannot be mapped to a version: %r" % tag)
    return "%d.%d.%d.%d" % (major, minor, patch, 9000 + n)


def md5_of(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def load_manifest(path):
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    if not isinstance(data, list) or not data:
        sys.exit("manifest.json must be a JSON list with one plugin entry")
    return data


def cmd_pack(a):
    ver = version_from_tag(a.tag)
    base = load_manifest(a.manifest)[0]
    os.makedirs(a.out_dir, exist_ok=True)
    dll = os.path.join(a.bin_dir, DLL)
    if not os.path.isfile(dll):
        sys.exit("Missing %s - build the plugin first" % dll)
    names = [DLL]
    for pat in EXTRA_DLLS:
        for n in sorted(os.listdir(a.bin_dir)):
            if fnmatch.fnmatch(n, pat) and n not in names:
                names.append(n)
    unaccounted = [n for n in sorted(os.listdir(a.bin_dir))
                   if n.lower().endswith(".dll") and n not in names
                   and not any(fnmatch.fnmatch(n, pat) for pat in PROVIDED_BY_JELLYFIN)]
    if unaccounted:
        sys.exit("The build output contains DLL(s) that are neither shipped nor known to be provided by Jellyfin: %s.\n"
                 "Add each to EXTRA_DLLS (ship it in the zip) or to PROVIDED_BY_JELLYFIN in installer/make_release.py." % ", ".join(unaccounted))
    meta = {
        "category": base.get("category", "General"), "guid": GUID, "name": base["name"],
        "description": base.get("description", ""), "overview": base.get("overview", ""),
        "owner": base.get("owner", ""), "targetAbi": a.abi, "timestamp": now(), "version": ver,
        "status": "Active", "autoUpdate": True, "assemblies": names,
    }
    out = os.path.join(a.out_dir, "FullUI_%s.zip" % ver)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for n in names:
            z.write(os.path.join(a.bin_dir, n), n)
        z.writestr("meta.json", json.dumps(meta, indent=2))
    print(out)
    print("md5", md5_of(out))


def cmd_version(a):
    print(version_from_tag(a.tag))


def cmd_manifest(a):
    ver = version_from_tag(a.tag)
    data = load_manifest(a.manifest)
    plugin = data[0]
    if plugin.get("guid") != GUID:
        sys.exit("manifest guid changed - refusing (must stay %s)" % GUID)
    url = a.source_url or "https://github.com/%s/releases/download/%s/%s" % (a.repo, a.tag, os.path.basename(a.zip))
    entry = {
        "version": ver, "changelog": a.changelog or "See the release page.", "targetAbi": a.abi,
        "sourceUrl": url, "checksum": md5_of(a.zip), "timestamp": now(),
    }
    versions = [v for v in plugin.get("versions", []) if v.get("version") != ver]
    plugin["versions"] = [entry] + versions  # newest first
    out = a.output or a.manifest
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, indent=2)
        f.write("\n")
    print("wrote %s with %d version(s); newest %s md5 %s" % (out, len(plugin["versions"]), ver, entry["checksum"]))


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("pack")
    p.add_argument("--tag", required=True)
    p.add_argument("--bin-dir", required=True)
    p.add_argument("--out-dir", default="out")
    p.add_argument("--manifest", default="manifest.json")
    p.add_argument("--abi", default=DEFAULT_ABI)
    p.set_defaults(fn=cmd_pack)
    v = sub.add_parser("version")
    v.add_argument("--tag", required=True)
    v.set_defaults(fn=cmd_version)
    m = sub.add_parser("manifest")
    m.add_argument("--manifest", default="manifest.json")
    m.add_argument("--output")
    m.add_argument("--tag", required=True)
    m.add_argument("--zip", required=True)
    m.add_argument("--repo", default="Narsmow/FullUISuit")
    m.add_argument("--source-url")
    m.add_argument("--changelog", default="")
    m.add_argument("--abi", default=DEFAULT_ABI)
    m.set_defaults(fn=cmd_manifest)
    a = ap.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
