#!/usr/bin/env python3
"""The installer talks to the plugin by GUID and by configuration property NAME. If the C# source changes and the
installer does not follow, the real install silently breaks (the mock cannot notice). This reads the plugin source:

  server/Jellyfin.Plugin.FullUI/Plugin.cs                          -> the plugin GUID
  server/Jellyfin.Plugin.FullUI/Configuration/PluginConfiguration.cs -> the configuration property names

and checks install.ps1, fullui_install.py, make_release.py, manifest.json and the mock agree with them.
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))


def read(*p):
    with open(os.path.join(ROOT, *p), encoding="utf-8") as f:
        return f.read()


def main():
    bad = []
    plugin_cs = read("server", "Jellyfin.Plugin.FullUI", "Plugin.cs")
    m = re.search(r'Guid\s+Id\s*=>\s*Guid\.Parse\("([0-9a-fA-F-]{36})"\)', plugin_cs)
    if not m:
        print("FAIL could not find the plugin GUID in Plugin.cs")
        sys.exit(1)
    guid = m.group(1).lower()

    ps1 = read("installer", "install.ps1")
    py = read("installer", "fullui_install.py")
    mr = read("installer", "make_release.py")
    mock = read("installer", "tests", "mock_jellyfin.py")
    manifest = json.loads(read("manifest.json"))
    found = {
        "install.ps1": re.search(r"\$FullUIGuid\s*=\s*'([0-9a-fA-F-]{36})'", ps1),
        "fullui_install.py": re.search(r'FULLUI_GUID\s*=\s*"([0-9a-fA-F-]{36})"', py),
        "make_release.py": re.search(r'GUID\s*=\s*"([0-9a-fA-F-]{36})"', mr),
        "mock_jellyfin.py": re.search(r'FULLUI_GUID\s*=\s*"([0-9a-fA-F-]{36})"', mock),
    }
    for name, mm in found.items():
        if not mm:
            bad.append("%s: no FullUI GUID found" % name)
        elif mm.group(1).lower() != guid:
            bad.append("%s: GUID %s != Plugin.cs %s" % (name, mm.group(1), guid))
    if manifest[0].get("guid", "").lower() != guid:
        bad.append("manifest.json: guid %s != Plugin.cs %s" % (manifest[0].get("guid"), guid))

    cfg_cs = read("server", "Jellyfin.Plugin.FullUI", "Configuration", "PluginConfiguration.cs")
    props = set(re.findall(r"public\s+[\w\[\]<>?.]+\s+(\w+)\s*\{\s*get;\s*set;\s*\}", cfg_cs))
    if not {"ServerName", "TmdbApiKey", "OllamaEnabled", "OllamaUrl"} <= props:
        bad.append("PluginConfiguration.cs: could not read the property names (got %s)" % sorted(props))
    used_ps = set(re.findall(r"(?:Set|Get)-Prop \$cfg '(\w+)'", ps1))
    used_py = set(re.findall(r'(?:setprop|prop)\(cfg, "(\w+)"', py))
    for name, used in (("install.ps1", used_ps), ("fullui_install.py", used_py)):
        if not used:
            bad.append("%s: no plugin configuration names found (pattern out of date?)" % name)
        for u in sorted(used - props):
            bad.append("%s uses configuration property %r which PluginConfiguration.cs does not have" % (name, u))
    if used_ps != used_py:
        bad.append("install.ps1 and fullui_install.py use different property names: %s vs %s" % (sorted(used_ps), sorted(used_py)))

    # the Fire TV download address must be the one firetv.yml publishes
    for name, text, pat in (("install.ps1", ps1, r"\$ApkUrl = '([^']+)'"), ("fullui_install.py", py, r'"apkurl": \("str", "([^"]+)"\)')):
        mm = re.search(pat, text)
        if not mm or not mm.group(1).endswith("/releases/download/firetv-latest/FullUI-release.apk"):
            bad.append("%s: ApkUrl is not .../releases/download/firetv-latest/FullUI-release.apk (%s)" % (name, mm.group(1) if mm else "missing"))

    if bad:
        print("FAIL installer/plugin consistency")
        for b in bad:
            print("     " + b)
        sys.exit(1)
    print("PASS installer/plugin consistency (GUID %s; %d configuration names: %s)" % (guid, len(used_ps), ", ".join(sorted(used_ps))))


if __name__ == "__main__":
    main()
