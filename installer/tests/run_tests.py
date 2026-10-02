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
import subprocess
import sys
import tempfile
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
INST = os.path.dirname(HERE)
PW = "Adm1nPass!"
TMDB = "0123456789abcdef0123456789abcdef"


def find_pwsh():
    for c in (os.environ.get("PWSH"), shutil.which("pwsh"), "/opt/pwsh/pwsh"):
        if c and os.path.exists(c):
            return c
    return None


class Mock:
    def __init__(self, modes=""):
        self.p = subprocess.Popen([sys.executable, os.path.join(HERE, "mock_jellyfin.py"), "--modes", modes],
                                  stdout=subprocess.PIPE, text=True)
        self.port = int(self.p.stdout.readline().strip().split("=")[1])
        self.base = "http://127.0.0.1:%d" % self.port

    def state(self):
        return json.load(urllib.request.urlopen(self.base + "/__mock/state"))

    def stop(self):
        self.p.terminate()
        self.p.wait()


def common(m, logdir):
    return ["-FileTransformationRepoUrl", m.base + "/ft/manifest.json",
            "-FullUIRepoUrl", m.base + "/fu/manifest.json",
            "-FullUIRepoFallbackUrl", "",
            "-TmdbBaseUrl", m.base + "/3",
            "-ApkUrl", m.base + "/nonexistent.apk",
            "-LogDir", logdir, "-PollSec", "1", "-InstallTimeoutSec", "30"]


def run(impl, pwsh, m, extra, logdir, stdin=None, timeout=150, sigint_after=None):
    if impl == "ps":
        cmd = [pwsh, "-NoProfile", "-File", os.path.join(INST, "install.ps1")]
    else:
        cmd = [sys.executable, os.path.join(INST, "fullui_install.py")]
    cmd += common(m, logdir) + ([] if "-RestartTimeoutSec" in extra else ["-RestartTimeoutSec", "40"]) + ([] if "-Server" in extra else ["-Server", m.base]) + extra
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
    S.append(("ctrl_c_during_restart", "slow_restart", [dict(args=ADMIN + ["-RestartTimeoutSec", "60"], sigint="Restarting Jellyfin now", code=None, out=["STOPPED", "safe to run"], forbid=["Traceback", "at <ScriptBlock>"])]))
    return S


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--impl", default="all")
    ap.add_argument("--only", default="")
    a = ap.parse_args()
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
    for impl in impls:
        for name, modes, steps in scenarios():
            if only and name not in only:
                continue
            m = Mock(modes)
            logdir = tempfile.mkdtemp(prefix="fullui-test-")
            problems = []
            try:
                for i, st in enumerate(steps):
                    args = [x.replace("{base}", m.base) for x in st["args"]]
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
