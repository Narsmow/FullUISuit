#!/usr/bin/env python3
"""Release helpers used by .github/workflows/release.yml (also runnable locally).

  pack      Build FullUI_<version>.zip (plugin dll + optional extra dlls + meta.json).
  manifest  Add a version entry to manifest.json (keeps guid/name/etc., keeps older versions).

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
DEFAULT_ABI = "10.11.0.0"
# Libraries Jellyfin does NOT already provide to plugins. Edit if the plugin gains new dependencies.
EXTRA_DLLS = ["Dapper.dll"]


def version_from_tag(tag):
    m = re.match(r"^v?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:[-+].*)?$", tag.strip())
    if not m:
        sys.exit("Tag %r is not like v1.2.3" % tag)
    return "%s.%s.%s.%s" % (m.group(1), m.group(2), m.group(3), m.group(4) or "0")


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
