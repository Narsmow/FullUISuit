#!/usr/bin/env python3
"""Runs install.ps1 (if pwsh is available) and fullui_install.py against the mock Jellyfin.

Usage: python3 run_tests.py [--impl ps|py|all] [--only name,name]
Exit code 0 only if every scenario passes.
"""
import argparse
import glob
import json
import os
import shutil
import signal
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
INST = os.path.dirname(HERE)
PW = "Adm1nPass!"
TMDB = "0123456789abcdef0123456789abcdef"


PS_EXE_OVERRIDE = None  # --ps-exe: e.g. "powershell" to test under Windows PowerShell 5.1


def find_pwsh():
    if PS_EXE_OVERRIDE:
        return shutil.which(PS_EXE_OVERRIDE) or PS_EXE_OVERRIDE
    for c in (os.environ.get("PWSH"), shutil.which("pwsh"), "/opt/pwsh/pwsh"):
        if c and os.path.exists(c):
            return c
    return None


def ps_cmd(pwsh, script):
    """Command line start for running a .ps1 with the chosen PowerShell (5.1 needs a policy override)."""
    return [pwsh, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script]


class Mock:
    def __init__(self, modes=""):
        self.p = subprocess.Popen([sys.executable, os.path.join(HERE, "mock_jellyfin.py"), "--modes", modes],
                                  stdout=subprocess.PIPE, text=True)
        self.port = int(self.p.stdout.readline().strip().split("=")[1])
        self.scheme = "https" if "tls" in modes.split(",") else "http"
        self.base = "%s://127.0.0.1:%d" % (self.scheme, self.port)
        self.alt = "%s://localhost:%d" % (self.scheme, self.port)  # same server, different host name
        self.front = None
        if "redirect" in modes.split(","):
            self.front = "http://127.0.0.1:%d" % int(self.p.stdout.readline().strip().split("=")[1])

    def _open(self, req):
        ctx = ssl._create_unverified_context() if self.scheme == "https" else None
        return urllib.request.urlopen(req, context=ctx)

    def state(self):
        return json.load(self._open(self.base + "/__mock/state"))

    def post(self, path, obj=None):
        data = json.dumps(obj or {}).encode()
        self._open(urllib.request.Request(self.base + path, data=data, method="POST", headers={"Content-Type": "application/json"})).read()

    def raw(self, method, path, body=None, headers=None):
        """One bare HTTP request (status, text) - for tests of the mock itself."""
        req = urllib.request.Request(self.base + path, data=body, method=method, headers=headers or {})
        try:
            r = self._open(req)
            return r.status, r.read().decode()
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode()

    def stop(self):
        self.p.terminate()
        self.p.wait()


def common(m, logdir, extra=()):
    base = ["-FileTransformationRepoUrl", m.base + "/ft/manifest.json",
            "-FullUIRepoUrl", m.base + "/fu/manifest.json",
            "-FullUIRepoFallbackUrl", "",
            "-TmdbBaseUrl", m.base + "/3",
            "-ApkUrl", m.base + "/nonexistent.apk",
            "-LogDir", logdir, "-PollSec", "1", "-InstallTimeoutSec", "30"]
    # a step may override any default (PowerShell rejects a parameter given twice)
    given = set(x.lower() for x in extra if x.startswith("-"))
    out, i = [], 0
    while i < len(base):
        if base[i].lower() in given:
            i += 2
            continue
        out += base[i:i + 2]
        i += 2
    return out


def run(impl, pwsh, m, extra, logdir, stdin=None, timeout=150, sigint_after=None):
    if impl == "ps":
        cmd = ps_cmd(pwsh, os.path.join(INST, "install.ps1"))
    else:
        cmd = [sys.executable, os.path.join(INST, "fullui_install.py")]
    cmd += common(m, logdir, extra) + ([] if "-RestartTimeoutSec" in extra else ["-RestartTimeoutSec", "40"]) + ([] if "-Server" in extra else ["-Server", m.base]) + extra
    env = dict(os.environ, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="1")
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, env=env)
    if sigint_after:
        # wait for the restart to begin, then press "Ctrl+C"
        out = ""
        deadline = time.time() + timeout
        while time.time() < deadline:
            line = p.stdout.readline()
            if not line:
                break
            out += line
            if sigint_after in line:
                time.sleep(1)
                p.send_signal(signal.SIGINT)
                break
        rest, _ = p.communicate(timeout=60)
        return p.returncode, out + rest
    try:
        out, _ = p.communicate(stdin, timeout=timeout)
    except subprocess.TimeoutExpired:
        p.kill()
        out = p.communicate()[0] + "\n[TEST HARNESS: TIMEOUT]"
        return 99, out
    return p.returncode, out


def logs_text(logdir):
    t = ""
    for f in glob.glob(os.path.join(logdir, "FullUI-install-*.log")):
        t += open(f, encoding="utf-8", errors="replace").read()
    return t


ADMIN = ["-Unattended", "-Username", "admin", "-Password", PW]


def plug(state, name):
    for p in state["plugins"]:
        if p["Name"] == name:
            return p
    return None


# Each scenario: (name, modes, [step...]) ; a step = dict(args, stdin, expect_code, expect_out(list), forbid_out(list), check(state)->None|str)
def scenarios():
    S = []
    happy_check = lambda s: None if (plug(s, "FullUI") or {}).get("Status") == "Active" and (plug(s, "File Transformation") or {}).get("Status") == "Active" and s["plugin_cfg"]["7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57"]["TmdbApiKey"] == TMDB and s["restarts"] == 1 and len(s["config"]["PluginRepositories"]) == 2 and s["config"].get("SomethingElse") == {"Nested": [1, 2, 3]} else "state wrong: %s" % json.dumps(s["plugins"])
    S.append(("happy_path", "", [dict(args=ADMIN + ["-TmdbKey", TMDB, "-ServerName", "MowFlix"], code=0, out=["[9/9]", "All done!", "File Transformation and FullUI are both Active", "loading the FullUI bundle"], check=happy_check)]))
    S.append(("rerun_idempotent", "", [
        dict(args=ADMIN + ["-TmdbKey", TMDB], code=0, out=["All done!"], check=happy_check),
        dict(args=ADMIN, code=0, out=["already installed and up to date", "no restart is needed", "All done!", "already present"],
             check=lambda s: None if s["restarts"] == 1 and len(s["config"]["PluginRepositories"]) == 2 and len(s["plugins"]) == 2 else "rerun changed state: restarts=%d" % s["restarts"])]))
    S.append(("update_when_current", "", [
        dict(args=ADMIN, code=0, out=["All done!"]),
        dict(args=ADMIN + ["-Update"], code=0, out=["Nothing to update"], check=lambda s: None if s["restarts"] == 1 else "restarted needlessly")]))
    S.append(("uninstall_keeps_filetransformation", "", [
        dict(args=ADMIN, code=0, out=["All done!"]),
        dict(args=ADMIN + ["-Uninstall"], code=0, out=["FullUI removed", "Done. FullUI has been uninstalled", "Your FullUI settings and data were kept"],
             check=lambda s: None if plug(s, "FullUI") is None and plug(s, "File Transformation") is not None else "wrong plugins left: %s" % s["plugins"])]))
    S.append(("uninstall_everything_then_again", "", [
        dict(args=ADMIN, code=0, out=["All done!"]),
        dict(args=ADMIN + ["-Uninstall", "-RemoveFileTransformation"], code=0, out=["File Transformation removed"],
             check=lambda s: None if not s["plugins"] else "plugins remain"),
        dict(args=ADMIN + ["-Uninstall"], code=0, out=["not installed"])]))
    S.append(("wrong_password", "", [dict(args=["-Unattended", "-Username", "admin", "-Password", "nope-nope"], code=1, out=["SOMETHING WENT WRONG", "username or password was not accepted", "safe to run this file again"], forbid=["nope-nope"])]))
    S.append(("non_admin", "", [dict(args=["-Unattended", "-Username", "kid", "-Password", "KidPass1"], code=1, out=["not an administrator", "What to try"], forbid=["KidPass1"])]))
    S.append(("wrong_version_unattended", "wrong_version", [dict(args=ADMIN, code=1, out=["10.10.7", "is not 10.11.x"], check=lambda s: None if not s["plugins"] else "installed despite wrong version")]))
    S.append(("wrong_version_user_declines", "wrong_version", [dict(args=[], stdin="n\n", code=2, out=["STOPPED", "Nothing was changed"], check=lambda s: None if not s["plugins"] else "changed")]))
    S.append(("wrong_version_forced_no_fullui_build", "wrong_version", [dict(args=ADMIN + ["-AllowOtherVersion"], code=1, out=["No version of FullUI is built for Jellyfin 10.10.7"])]))
    S.append(("repo_unreachable", "repo_down", [dict(args=ADMIN, code=1, out=["cannot download the FullUI plugin list", "internet"])]))
    S.append(("ft_repo_unreachable", "ft_repo_down", [dict(args=ADMIN, code=1, out=["cannot download the File Transformation plugin list"])]))
    S.append(("server_cannot_read_repos", "no_packages", [dict(args=ADMIN, code=1, out=["catalog does not list"])]))
    S.append(("install_http_500", "install_500", [dict(args=ADMIN, code=1, out=["Installing File Transformation hit an error on the server side", "Dashboard > Logs"])]))
    S.append(("restart_never_returns", "no_restart_return", [dict(args=ADMIN + ["-RestartTimeoutSec", "12"], code=1, out=["did not come back within 12 seconds", "run this file again"])]))
    S.append(("restart_refused", "restart_refused", [dict(args=ADMIN, code=1, out=["Asking Jellyfin to restart"])]))
    S.append(("slow_restart_ok", "slow_restart", [dict(args=ADMIN, code=0, out=["Jellyfin is back", "All done!"])]))
    S.append(("plugin_malfunctions", "malfunction", [dict(args=ADMIN, code=1, out=["did not become active", "Malfunctioned"])]))
    S.append(("injection_missing_warns", "no_injection", [dict(args=ADMIN, code=0, out=["does NOT load FullUI yet", "needs attention"])]))
    S.append(("tmdb_rejected_unattended", "", [dict(args=ADMIN + ["-TmdbKey", "badbadbadbadbadbadbadbadbadbadXX"], code=0, out=["TMDB rejected the key"], check=lambda s: None if not s["plugin_cfg"]["7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57"]["TmdbApiKey"] else "bad key was saved")]))
    S.append(("jellyfin_not_reachable", "", [dict(args=["-Unattended", "-Server", "127.0.0.1:1"], code=1, out=["could not find a Jellyfin server", "Make sure Jellyfin is running"])]))
    S.append(("interactive_prompts", "", [dict(
        args=[], stdin="\n".join(["admin", "wrongpw", "admin", PW, "y", "My Flix", "tooshort", TMDB, "n"]) + "\n", code=0,
        out=["not accepted", "TMDB said that key is not valid", "TMDB accepted the key", "All done!"],
        forbid_log=[PW, TMDB],
        check=lambda s: None if s["plugin_cfg"]["7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57"]["ServerName"] == "My Flix" and s["plugin_cfg"]["7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57"]["TmdbApiKey"] == TMDB else "interactive config not saved")]))
    S.extend(regression_scenarios())
    S.append(("ctrl_c_during_restart", "slow_restart", [dict(args=ADMIN + ["-RestartTimeoutSec", "60"], sigint="Restarting Jellyfin now", code=None, out=["STOPPED", "safe to run"], forbid=["Traceback", "at <ScriptBlock>"])]))
    return S


FU = "7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57"
FT = "5e87cc92-571a-4d8d-8d98-d2d4147f9f90"


def vers(s, name):
    return sorted((p["Version"], p["Status"]) for p in s["plugins"] if p["Name"] == name)


def used(s, method, path):
    return any(m == method and p == path for m, p in s["log"])


def regression_scenarios():
    """Scenarios that exist because a real-Jellyfin behaviour once slipped past a lenient mock (B-75..B-103)."""
    S = []
    publish02 = lambda m: m.post("/__mock/publish", {"version": "0.2.0.0"})
    # B-76: Jellyfin keeps old plugin versions (Superseded); installer must look at the newest one
    S.append(("update_keeps_old_version_superseded", "", [
        dict(args=ADMIN, code=0, out=["All done!"], check=lambda s: None if vers(s, "FullUI") == [("0.1.0.0", "Active")] else "step1 %s" % vers(s, "FullUI")),
        dict(pre=publish02, args=ADMIN, code=0, out=["Updating FullUI from 0.1.0.0 to 0.2.0.0", "All done!"],
             check=lambda s: None if vers(s, "FullUI") == [("0.1.0.0", "Superseded"), ("0.2.0.0", "Active")] and s["restarts"] == 2 else "after update: %s restarts=%d" % (vers(s, "FullUI"), s["restarts"])),
        dict(args=ADMIN, code=0, out=["0.2.0.0 is already installed and up to date", "no restart is needed"],
             check=lambda s: None if s["restarts"] == 2 and len(vers(s, "FullUI")) == 2 else "rerun after update reinstalled (restarts=%d)" % s["restarts"]),
        dict(args=ADMIN + ["-Update"], code=0, out=["Nothing to update"], check=lambda s: None if s["restarts"] == 2 else "restarted needlessly"),
        dict(args=ADMIN + ["-Uninstall"], code=0, out=["FullUI removed", "Done. FullUI has been uninstalled"],
             check=lambda s: None if not vers(s, "FullUI") and vers(s, "File Transformation") else "uninstall left versions behind: %s" % s["plugins"])]))
    # B-79: HTTP->HTTPS style redirect in front of Jellyfin (POST must go to the final URL)
    S.append(("redirect_in_front_of_jellyfin", "redirect", [dict(args=ADMIN + ["-Server", "{front}"], code=0, out=["All done!"],
             check=lambda s: None if vers(s, "FullUI") == [("0.1.0.0", "Active")] else "not installed through redirect")]))
    # B-78: a stale/empty/foreign primary manifest must not win; the next URL is tried
    S.append(("stale_primary_manifest_falls_back", "", [dict(args=ADMIN + ["-FullUIRepoUrl", "{base}/fu-stale/manifest.json", "-FullUIRepoFallbackUrl", "{base}/fu/manifest.json"],
             code=0, out=["All done!"],
             check=lambda s: None if [r["Url"] for r in s["config"]["PluginRepositories"] if r["Name"] == "FullUI"] and all("stale" not in r["Url"] for r in s["config"]["PluginRepositories"]) else "stale repository was registered")]))
    S.append(("foreign_manifest_rejected", "", [dict(args=ADMIN + ["-FullUIRepoUrl", "{base}/fu-other/manifest.json", "-FullUIRepoFallbackUrl", ""], code=1,
             out=["cannot download the FullUI plugin list"])]))
    # B-83: dedicated /Repositories API, with a fallback only when it does not exist
    S.append(("repositories_use_dedicated_api", "", [dict(args=ADMIN, code=0, out=["All done!"],
             check=lambda s: None if used(s, "POST", "/Repositories") and not used(s, "POST", "/System/Configuration") and len(s["config"]["PluginRepositories"]) == 2 else "did not use POST /Repositories only")]))
    S.append(("repositories_fallback_to_configuration", "no_repositories_endpoint", [dict(args=ADMIN, code=0, out=["All done!"],
             check=lambda s: None if used(s, "POST", "/System/Configuration") and len(s["config"]["PluginRepositories"]) == 2 and s["config"].get("SomethingElse") == {"Nested": [1, 2, 3]} else "fallback failed")]))
    # B-86: a user's own File Transformation is only touched when missing/unsupported (or they agree)
    S.append(("own_filetransformation_kept", "old_ft_installed", [dict(args=ADMIN, code=0, out=["All done!", "File Transformation 2.2.1.0 is already installed"],
             check=lambda s: None if vers(s, "File Transformation") == [("2.2.1.0", "Active")] else "FT was changed: %s" % vers(s, "File Transformation"))]))
    S.append(("own_filetransformation_upgrade_when_asked", "old_ft_installed", [dict(
        args=[], stdin="\n".join(["admin", PW, "y", "", "", "", "n"]) + "\n", code=0, out=["newer File Transformation", "All done!"],
        check=lambda s: None if ("3.0.0.0", "Active") in vers(s, "File Transformation") else "FT not upgraded: %s" % vers(s, "File Transformation"))]))
    S.append(("own_filetransformation_declined", "old_ft_installed", [dict(
        args=[], stdin="\n".join(["admin", PW, "n", "", "", "", "n"]) + "\n", code=0, out=["Keeping your File Transformation", "All done!"],
        check=lambda s: None if vers(s, "File Transformation") == [("2.2.1.0", "Active")] else "FT changed after 'no'")]))
    S.append(("filetransformation_update_flag_upgrades", "old_ft_installed", [dict(args=ADMIN + ["-Update"], code=0, out=["All done!"],
             check=lambda s: None if ("3.0.0.0", "Active") in vers(s, "File Transformation") else "FT not upgraded with -Update")]))
    S.append(("unsupported_filetransformation_replaced", "old_ft_installed,ft_unsupported", [dict(args=ADMIN, code=0, out=["All done!"],
             check=lambda s: None if ("3.0.0.0", "Active") in vers(s, "File Transformation") else "unsupported FT not replaced: %s" % vers(s, "File Transformation"))]))
    # B-97: an "OK" restart needs evidence that Jellyfin actually restarted
    S.append(("restart_that_never_happens", "no_actual_restart", [dict(args=ADMIN + ["-RestartTimeoutSec", "26"], code=1, out=["never went offline", "restart"])]))
    # B-87: the self-test follows the script tag through the same base path
    S.append(("baseurl_install_ok", "baseurl", [dict(args=ADMIN + ["-Server", "{base}/jellyfin"], code=0, out=["All done!", "script tag points to a file that is served"])]))
    S.append(("baseurl_script_tag_ignores_base_path", "baseurl,bad_inject_path", [dict(args=ADMIN + ["-Server", "{base}/jellyfin"], code=0,
             out=["needs attention", "does not load"])]))
    # B-84: HTTPS with a certificate nobody trusts: never silently, never for other hosts
    S.append(("tls_unattended_needs_insecure_flag", "tls", [dict(args=ADMIN, code=1, out=["certificate", "-Insecure"], forbid=["All done!"],
             check=lambda s: None if not s["plugins"] else "installed over an unverified connection")]))
    S.append(("tls_insecure_flag_ok", "tls", [dict(args=ADMIN + ["-Insecure"], code=0, out=["All done!"])]))
    S.append(("tls_insecure_is_scoped_to_jellyfin_host", "tls", [dict(
        args=ADMIN + ["-Insecure", "-FileTransformationRepoUrl", "{alt}/ft/manifest.json"], code=1, out=["cannot download the File Transformation plugin list"])]))
    # B-100: the install POST is sent once per plugin (never repeated blindly)
    S.append(("install_post_not_retried", "", [dict(args=ADMIN, code=0, out=["All done!"],
             check=lambda s: None if sum(1 for m, p in s["log"] if m == "POST" and p.startswith("/Packages/Installed/")) == 2 else "install POST repeated")]))
    return S


def mock_sanity():
    """The mock must stay as strict as real Jellyfin; if someone relaxes it these fail (B-75)."""
    m = Mock()
    bad = []
    try:
        body = json.dumps({"Username": "admin", "Pw": PW}).encode()
        h = {"Content-Type": "application/json"}
        good = 'MediaBrowser Client="t", Device="d", DeviceId="id", Version="1"'
        for label, hdr, want in (
                ("no Authorization header", {}, 500),
                ("header without Device", {"Authorization": 'MediaBrowser Client="t", DeviceId="id", Version="1"'}, 500),
                ("header without Version", {"Authorization": 'MediaBrowser Client="t", Device="d", DeviceId="id"'}, 500),
                ("empty Client", {"Authorization": 'MediaBrowser Client="", Device="d", DeviceId="id", Version="1"'}, 500),
                ("complete header", {"Authorization": good}, 200),
                ("complete X-Emby-Authorization", {"X-Emby-Authorization": good}, 200)):
            code, _ = m.raw("POST", "/Users/AuthenticateByName", body, dict(h, **hdr))
            if code != want:
                bad.append("AuthenticateByName with %s -> %s, real Jellyfin gives %s" % (label, code, want))
        # appended versions + ordering + exact delete
        code, txt = m.raw("POST", "/Users/AuthenticateByName", body, dict(h, Authorization=good))
        tok = json.loads(txt)["AccessToken"]
        ah = {"Authorization": good + ', Token="%s"' % tok}
        q = "/Packages/Installed/FullUI?assemblyGuid=%s&version=" % FU
        m.raw("POST", q + "0.1.0.0", None, ah)
        m.post("/__mock/publish", {"version": "0.2.0.0"})
        m.raw("POST", q + "0.2.0.0", None, ah)
        code, txt = m.raw("GET", "/Plugins", None, ah)
        got = [(p["Version"]) for p in json.loads(txt)]
        if got != ["0.1.0.0", "0.2.0.0"]:
            bad.append("installing a second version must ADD it (oldest first); got %s" % got)
        code, _ = m.raw("DELETE", "/Plugins/%s/0.2.0.0" % FU, None, ah)
        code2, txt = m.raw("GET", "/Plugins", None, ah)
        if code != 204 or [p["Version"] for p in json.loads(txt)] != ["0.1.0.0"]:
            bad.append("DELETE /Plugins/{id}/{version} must remove only that version")
    finally:
        m.stop()
    print("%s %-4s %s" % ("PASS" if not bad else "FAIL", "mock", "mock_is_as_strict_as_real_jellyfin"))
    for b in bad:
        print("     " + b)
    return bad


NORM_CASES = [  # (input, expected) - "" / "10" / "abc" used to loop forever (B-85)
    ("", "0.0.0.0"), ("10", "10.0.0.0"), ("abc", "0.0.0.0"), ("10.11", "10.11.0.0"), ("10.11.4", "10.11.4.0"),
    ("10.11.4.0", "10.11.4.0"), ("10.11.4.7-rc1", "10.11.4.7"), ("1.2.3.4.5", "1.2.3.4"), ("1..2", "1.2.0.0"),
    ("v1.2", "0.0.0.0"), ("99999999999.1", "0.1.0.0"), ("  ", "0.0.0.0"), (".", "0.0.0.0"), ("10.11.4+build", "10.11.4.0"),
]


def unit_norm_version(pwsh):
    """Norm-Version in install.ps1 and nv() in fullui_install.py, with a hard timeout per implementation."""
    bad = []
    sys.path.insert(0, INST)
    import fullui_install as fi
    for text, want in NORM_CASES:
        got = ".".join(str(x) for x in fi.nv(text))
        if got != want:
            bad.append("python nv(%r) = %s, want %s" % (text, got, want))
    if pwsh:
        driver = r"""
$ErrorActionPreference = 'Stop'
$tokens = $null; $errs = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($env:FULLUI_SCRIPT, [ref]$tokens, [ref]$errs)
$fn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Norm-Version' }, $true)
if ($fn.Count -ne 1) { Write-Output 'Norm-Version not found'; exit 3 }
. ([scriptblock]::Create($fn[0].Extent.Text))
$bad = 0
# one case per line: input|expected   (plain text on purpose: no JSON quirks between PowerShell versions)
foreach ($line in [IO.File]::ReadAllLines($env:FULLUI_CASES)) {
    $parts = $line.Split('|')
    $got = (Norm-Version $parts[0]).ToString()
    if ($got -ne $parts[1]) { Write-Output ('Norm-Version(' + $parts[0] + ') = ' + $got + ', want ' + $parts[1]); $bad++ }
}
if ($bad) { exit 1 }
Write-Output 'norm ok'
"""
        tmpd = tempfile.mkdtemp(prefix="fullui-norm-")
        cases = os.path.join(tmpd, "cases.txt")
        with open(cases, "w") as f:
            f.write("\n".join("%s|%s" % (t, w) for t, w in NORM_CASES) + "\n")
        drv = os.path.join(tmpd, "driver.ps1")
        with open(drv, "w") as f:
            f.write(driver)
        env = dict(os.environ, FULLUI_SCRIPT=os.path.join(INST, "install.ps1"), FULLUI_CASES=cases)
        try:
            r = subprocess.run(ps_cmd(pwsh, drv), capture_output=True, text=True, env=env, timeout=60)
            if r.returncode != 0 or "norm ok" not in r.stdout:
                bad.append("PowerShell Norm-Version: " + (r.stdout + r.stderr).strip()[-600:])
        except subprocess.TimeoutExpired:
            bad.append("PowerShell Norm-Version HUNG (timeout 60 s)")
        shutil.rmtree(tmpd, ignore_errors=True)
    print("%s %-4s %s" % ("PASS" if not bad else "FAIL", "unit", "norm_version_edge_cases"))
    for b in bad:
        print("     " + b)
    return bad


def main():
    global PS_EXE_OVERRIDE
    ap = argparse.ArgumentParser()
    ap.add_argument("--impl", default="all")
    ap.add_argument("--only", default="")
    ap.add_argument("--ps-exe", default="", help="PowerShell executable to test, e.g. powershell (Windows PowerShell 5.1) or pwsh")
    a = ap.parse_args()
    PS_EXE_OVERRIDE = a.ps_exe or None
    pwsh = find_pwsh()
    impls = []
    if a.impl in ("py", "all"):
        impls.append("py")
    if a.impl in ("ps", "all"):
        if pwsh:
            impls.append("ps")
        else:
            print("SKIP: pwsh not found - PowerShell implementation NOT tested")
    only = set(x for x in a.only.split(",") if x)
    results = []
    if not only:
        results.append(("mock", "mock_sanity", mock_sanity()))
        results.append(("unit", "norm_version", unit_norm_version(pwsh if "ps" in impls else None)))
    for impl in impls:
        for name, modes, steps in scenarios():
            if only and name not in only:
                continue
            if name == "ctrl_c_during_restart" and os.name == "nt":
                print("SKIP %-4s %s (Ctrl+C delivery is not scriptable on Windows)" % (impl, name))
                continue
            m = Mock(modes)
            logdir = tempfile.mkdtemp(prefix="fullui-test-")
            problems = []
            try:
                for i, st in enumerate(steps):
                    if st.get("pre"):
                        st["pre"](m)
                    args = [x.replace("{base}", m.base).replace("{front}", m.front or "").replace("{alt}", m.alt) for x in st["args"]]
                    code, out = run(impl, pwsh, m, args, logdir, stdin=st.get("stdin"), sigint_after=st.get("sigint"))
                    if st.get("code") is not None and code != st["code"]:
                        problems.append("step %d: exit %s != %s" % (i + 1, code, st["code"]))
                    # exit code after SIGINT is not meaningful: pwsh on Linux reports 0 when interrupted; only the message matters
                    if st.get("sigint") and code == 99:
                        problems.append("step %d: hung after Ctrl+C" % (i + 1))
                    for s in st.get("out", []):
                        if s not in out:
                            problems.append("step %d: output lacks %r" % (i + 1, s))
                    lg = logs_text(logdir)
                    for s in st.get("forbid", []):
                        if s in out or s in lg:
                            problems.append("step %d: forbidden text %r appears in output/log" % (i + 1, s))
                    for s in st.get("forbid_log", []):
                        if s in lg:
                            problems.append("step %d: secret %r leaked into log file" % (i + 1, s[:4] + "..."))
                    for secret in (PW, TMDB):
                        if secret in lg:
                            problems.append("step %d: secret leaked into log file" % (i + 1))
                    if "Traceback" in out or "at <ScriptBlock>" in out or "Exception calling" in out:
                        problems.append("step %d: raw stack trace shown to user" % (i + 1))
                    if st.get("check"):
                        r = st["check"](m.state())
                        if r:
                            problems.append("step %d: %s" % (i + 1, r))
                    if problems:
                        problems.append("--- output of failing step ---\n" + out[-2500:])
                        break
            except Exception as e:
                problems.append("harness error: %r" % e)
            finally:
                m.stop()
                shutil.rmtree(logdir, ignore_errors=True)
            results.append((impl, name, problems))
            print("%s %-4s %s" % ("PASS" if not problems else "FAIL", impl, name))
            for p in problems:
                print("     " + p)
            sys.stdout.flush()
    bad = [r for r in results if r[2]]
    print("\n%d scenarios run, %d passed, %d failed" % (len(results), len(results) - len(bad), len(bad)))
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
