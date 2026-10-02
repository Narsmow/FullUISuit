#!/usr/bin/env python3
"""Tests installer/make_release.py: zip contents, md5 checksum, manifest regeneration."""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
MR = os.path.join(ROOT, "installer", "make_release.py")


def run(*a):
    r = subprocess.run([sys.executable, MR] + list(a), capture_output=True, text=True)
    if r.returncode != 0:
        raise SystemExit("make_release failed: " + r.stderr + r.stdout)
    return r.stdout


def main():
    tmp = tempfile.mkdtemp()
    try:
        b = os.path.join(tmp, "bin")
        os.makedirs(b)
        open(os.path.join(b, "Jellyfin.Plugin.FullUI.dll"), "wb").write(b"MZfake-dll")
        open(os.path.join(b, "Jellyfin.Controller.dll"), "wb").write(b"must-not-ship")  # provided by Jellyfin
        open(os.path.join(b, "Newtonsoft.Json.dll"), "wb").write(b"must-not-ship")      # provided by Jellyfin
        open(os.path.join(b, "Jellyfin.Plugin.FullUI.pdb"), "wb").write(b"symbols")
        mf = os.path.join(tmp, "manifest.json")
        shutil.copy(os.path.join(ROOT, "manifest.json"), mf)
        run("pack", "--tag", "v1.2.3", "--bin-dir", b, "--out-dir", tmp, "--manifest", mf)
        z = os.path.join(tmp, "FullUI_1.2.3.0.zip")
        names = sorted(zipfile.ZipFile(z).namelist())
        assert names == ["Jellyfin.Plugin.FullUI.dll", "meta.json"], names
        meta = json.loads(zipfile.ZipFile(z).read("meta.json"))
        assert meta["guid"] == "7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57" and meta["version"] == "1.2.3.0" and meta["targetAbi"] == "10.11.6.0"
        run("manifest", "--manifest", mf, "--tag", "v1.2.3", "--zip", z, "--changelog", "first release")
        run("manifest", "--manifest", mf, "--tag", "v1.3.0", "--zip", z, "--changelog", "second")
        run("manifest", "--manifest", mf, "--tag", "v1.3.0", "--zip", z, "--changelog", "second again")  # re-run: no duplicate
        d = json.load(open(mf))
        p = d[0]
        assert p["guid"] == "7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57" and p["name"] == "FullUI"
        assert [v["version"] for v in p["versions"]] == ["1.3.0.0", "1.2.3.0"], p["versions"]
        v = p["versions"][1]
        assert v["checksum"] == hashlib.md5(open(z, "rb").read()).hexdigest()
        assert v["targetAbi"] == "10.11.6.0" and v["changelog"] == "first release"
        assert v["sourceUrl"] == "https://github.com/Narsmow/FullUISuit/releases/download/v1.2.3/FullUI_1.2.3.0.zip", v["sourceUrl"]
        assert v["timestamp"].endswith("Z")
        # B-93: an unexpected dependency DLL in the build output must stop the release, not be silently dropped
        open(os.path.join(b, "Dapper.dll"), "wb").write(b"MZdapper")
        stray = subprocess.run([sys.executable, MR, "pack", "--tag", "v1.2.3", "--bin-dir", b, "--out-dir", tmp, "--manifest", mf], capture_output=True, text=True)
        assert stray.returncode != 0 and "Dapper.dll" in (stray.stdout + stray.stderr), "unaccounted DLL must fail the pack"
        os.remove(os.path.join(b, "Dapper.dll"))
        # tag -> version mapping, incl. prereleases that sort below their final release (B-91)
        ver = lambda t: run("version", "--tag", t).strip()
        assert ver("v1.2.3") == "1.2.3.0" and ver("v1.2.3.4") == "1.2.3.4"
        assert ver("v1.2.3-rc1") == "1.2.2.9001" and ver("v1.3.0-rc2") == "1.2.9999.9002" and ver("v2.0.0-beta") == "1.9999.9999.9001"
        key = lambda v: tuple(int(x) for x in v.split("."))
        assert key(ver("v1.2.3-rc1")) < key(ver("v1.2.3")) and key(ver("v1.2.3-rc1")) > key(ver("v1.2.2")) and ver("v1.2.3-rc1") != ver("v1.2.3")
        assert key(ver("v1.2.3-rc1")) < key(ver("v1.2.3-rc2"))
        for badtag in ("nightly", "v1.2", "v1.2.3.4-rc1", "v0.0.0-rc1"):
            assert subprocess.run([sys.executable, MR, "version", "--tag", badtag], capture_output=True).returncode != 0, badtag
        bad = subprocess.run([sys.executable, MR, "pack", "--tag", "nightly", "--bin-dir", b, "--manifest", mf], capture_output=True)
        assert bad.returncode != 0
        print("PASS release helpers (pack, md5 checksum, manifest versions, idempotent re-run, guid kept)")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    main()
