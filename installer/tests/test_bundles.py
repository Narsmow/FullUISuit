#!/usr/bin/env python3
"""Builds the one-file installers and proves they work:
 - FullUI-Installer.sh runs end to end (python path, and pwsh path when pwsh exists) against the mock.
 - FullUI-Installer.cmd: the exact PowerShell unpack command from its header is executed with pwsh and the
   extracted script is compared byte for byte with install.ps1 (cmd.exe itself cannot run on Linux).
 - install.sh refuses cleanly when neither pwsh nor python3 exist.
"""
import os
import re
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from run_tests import Mock, find_pwsh, INST, PW  # noqa: E402


def sh(cmd, env=None, timeout=200):
    r = subprocess.run(cmd, capture_output=True, text=True, env=env, timeout=timeout)
    return r.returncode, r.stdout + r.stderr


def main():
    out = tempfile.mkdtemp(prefix="fullui-dist-")
    fails = []
    pwsh = find_pwsh()
    try:
        subprocess.check_call([sys.executable, os.path.join(INST, "build_single.py"), out], stdout=subprocess.DEVNULL)
        env = dict(os.environ, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="1")
        if pwsh:
            env["PATH"] = os.path.dirname(pwsh) + os.pathsep + env["PATH"]

        def args(m, logdir):
            return ["-Unattended", "-Username", "admin", "-Password", PW, "-Server", m.base,
                    "-FileTransformationRepoUrl", m.base + "/ft/manifest.json", "-FullUIRepoUrl", m.base + "/fu/manifest.json",
                    "-FullUIRepoFallbackUrl", "", "-TmdbBaseUrl", m.base + "/3", "-ApkUrl", m.base + "/none.apk", "-PollSec", "1"]

        # 1) single .sh via python
        for label, force in (("sh+python", "1"), ("sh+pwsh", "")):
            if not force and not pwsh:
                print("SKIP bundle %s (no pwsh)" % label)
                continue
            m = Mock()
            e = dict(env)
            if force:
                e["FULLUI_FORCE_PY"] = "1"
            code, text = sh(["bash", os.path.join(out, "FullUI-Installer.sh")] + args(m, out), env=e)
            st = m.state()
            m.stop()
            ok = code == 0 and "All done!" in text and len(st["plugins"]) == 2
            logs = [f for f in os.listdir(out) if f.startswith("FullUI-install-")]
            ok = ok and bool(logs)
            for f in logs:
                os.remove(os.path.join(out, f))
            print("%s bundle FullUI-Installer.sh via %s" % ("PASS" if ok else "FAIL", label))
            if not ok:
                fails.append(label)
                print(text[-1500:])

        # 2) repo-layout install.sh wrapper
        m = Mock()
        e = dict(env, FULLUI_FORCE_PY="1")
        code, text = sh(["bash", os.path.join(INST, "install.sh")] + args(m, out), env=e)
        m.stop()
        good = code == 0 and "All done!" in text
        print("%s install.sh wrapper (python fallback)" % ("PASS" if good else "FAIL"))
        if not good:
            fails.append("install.sh")
            print(text[-1500:])
        for f in os.listdir(INST):
            if f.startswith("FullUI-install-"):
                os.remove(os.path.join(INST, f))

        # 3) neither pwsh nor python3 -> friendly refusal
        empty = tempfile.mkdtemp()
        e = dict(os.environ, PATH=empty)
        code, text = sh(["/bin/bash", os.path.join(INST, "install.sh")], env=e)
        good = code == 1 and "needs either PowerShell" in text and "Nothing was changed" in text
        shutil.rmtree(empty)
        print("%s install.sh with no pwsh/python3 refuses cleanly" % ("PASS" if good else "FAIL"))
        if not good:
            fails.append("nointerp")

        # 4) .cmd payload unpack, same PowerShell command text as in the header
        cmdtext = open(os.path.join(out, "FullUI-Installer.cmd"), newline="").read()
        if "\r\n" not in cmdtext or re.search(r"[^\r]\n", cmdtext):
            fails.append("cmd-crlf")
            print("FAIL .cmd must use CRLF line endings throughout")
        else:
            print("PASS .cmd uses CRLF line endings")
        for must in ("StringComparison]::Ordinal", 'for %%A in (%*) do if /i "%%~A"=="-Unattended"', "FULLUI_MARK"):
            if must not in cmdtext:
                fails.append("cmd-" + must[:12])
                print("FAIL .cmd lacks %r" % must)
        if "echo %* |" in cmdtext:
            fails.append("cmd-echo-args")
            print("FAIL .cmd still pipes %* through echo (breaks on quotes and &)")
        if pwsh:
            line = [l for l in cmdtext.split("\r\n") if l.startswith("powershell") and "-Command" in l][0]
            snippet = re.search(r'-Command "(.*)"$', line).group(1)
            dst = os.path.join(out, "unpacked.ps1")
            e = dict(env, FULLUI_SELF=os.path.join(out, "FullUI-Installer.cmd"), FULLUI_PS=dst)
            code, text = sh([pwsh, "-NoProfile", "-Command", snippet], env=e)
            same = code == 0 and os.path.exists(dst) and open(dst, "rb").read().lstrip(b"\xef\xbb\xbf").replace(b"\r\n", b"\n") == open(os.path.join(INST, "install.ps1"), "rb").read()
            print("%s .cmd header unpacks to an identical install.ps1" % ("PASS" if same else "FAIL"))
            if not same:
                fails.append("cmd-unpack")
                print(text)
            else:
                # the unpacked copy must run, too (it is what the .cmd executes)
                m = Mock()
                code, text = sh([pwsh, "-NoProfile", "-File", dst] + args(m, out) + ["-LogDir", out], env=env)
                m.stop()
                good = code == 0 and "All done!" in text
                print("%s unpacked .cmd payload runs end to end" % ("PASS" if good else "FAIL"))
                if not good:
                    fails.append("cmd-run")
                    print(text[-1500:])
        else:
            print("SKIP .cmd payload unpack test (no pwsh)")
    finally:
        shutil.rmtree(out, ignore_errors=True)
    if fails:
        print("bundle tests failed:", fails)
        sys.exit(1)


if __name__ == "__main__":
    main()
