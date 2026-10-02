#!/usr/bin/env python3
"""FullUI installer - pure Python 3 (standard library only) twin of install.ps1.

Used by install.sh when PowerShell (pwsh) is not installed. Same steps, same
messages, same flags (PowerShell style: -Server, -Username, ... or --server ...).
Drives your RUNNING Jellyfin through its web API; safe to run again.
"""
import getpass
import json
import os
import re
import shutil
import socket
import ssl
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime

VERSION = "1.0.0"
FULLUI_GUID = "7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57"

# name -> (kind, default)
OPTS = {
    "server": ("str", None), "username": ("str", None), "password": ("str", None), "apikey": ("str", None),
    "unattended": ("flag", False), "update": ("flag", False), "uninstall": ("flag", False),
    "removefiletransformation": ("flag", False), "purgedata": ("flag", False),
    "servername": ("str", None), "tmdbkey": ("str", None), "installollama": ("flag", False),
    "skipollama": ("flag", False), "allowotherversion": ("flag", False), "insecure": ("flag", False),
    "firetvip": ("str", None), "logdir": ("str", None),
    "restarttimeoutsec": ("int", 300), "installtimeoutsec": ("int", 300), "pollsec": ("int", 2),
    "filetransformationrepourl": ("str", "https://www.iamparadox.dev/jellyfin/plugins/manifest.json"),
    "fulluirepourl": ("str", "https://raw.githubusercontent.com/Narsmow/FullUISuit/main/manifest.json"),
    "fulluirepofallbackurl": ("str", "https://github.com/Narsmow/FullUISuit/releases/latest/download/manifest.json"),
    "tmdbbaseurl": ("str", "https://api.themoviedb.org/3"),
    "apkurl": ("str", "https://github.com/Narsmow/FullUISuit/releases/latest/download/FullUI-FireTV.apk"),
}


def parse_args(argv):
    o = {k: v[1] for k, v in OPTS.items()}
    i = 0
    while i < len(argv):
        a = argv[i]
        if not a.startswith("-"):
            i += 1
            continue
        name = a.lstrip("-").lower().replace("-", "").replace("_", "")
        if name not in OPTS:
            print("Unknown option: %s" % a)
            sys.exit(2)
        kind = OPTS[name][0]
        if kind == "flag":
            o[name] = True
        else:
            i += 1
            if i >= len(argv):
                print("Option %s needs a value" % a)
                sys.exit(2)
            o[name] = int(argv[i]) if kind == "int" else argv[i]
        i += 1
    for env, key in (("FULLUI_PASSWORD", "password"), ("FULLUI_APIKEY", "apikey"), ("FULLUI_TMDB_KEY", "tmdbkey")):
        if not o[key] and os.environ.get(env):
            o[key] = os.environ[env]
    return o


O = {}
SECRETS = []
LOGFILE = None
STATE = {"token": None, "base": None, "version": None, "total": 9, "used_pw": False, "insecure": False,
         "done": False, "reported": False}


class Fail(Exception):
    def __init__(self, problem, fix, tech=None):
        Exception.__init__(self, problem)
        self.fix, self.tech = fix, tech


class Cancel(Exception):
    pass


# ----------------------------------------------------------------- log + output
def scrub(t):
    t = "" if t is None else str(t)
    for s in SECRETS:
        t = t.replace(s, "***")
    t = re.sub(r'(?i)(Token\s*=\s*")[^"]*', r"\1***", t)
    t = re.sub(r'(?i)((?:api_?key|apikey|x-emby-token|access_?token)\s*[=:]\s*"?)[^&\s",]+', r"\1***", t)
    t = re.sub(r'(?i)("(?:Pw|Password|TmdbApiKey|AccessToken)"\s*:\s*")[^"]*', r"\1***", t)
    t = re.sub(r"(?i)(Bearer\s+)[A-Za-z0-9._\-]+", r"\1***", t)
    return t


def add_secret(v):
    if v and len(v) >= 3 and v not in SECRETS:
        SECRETS.append(v)


def log(m):
    if LOGFILE:
        try:
            with open(LOGFILE, "a", encoding="utf-8") as f:
                f.write("%s %s\n" % (datetime.now().strftime("%H:%M:%S"), scrub(m)))
        except Exception:
            pass


def init_log():
    global LOGFILE
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    for d in (O["logdir"], os.getcwd(), tempfile.gettempdir()):
        if not d or not os.path.isdir(d):
            continue
        try:
            p = os.path.join(d, "FullUI-install-%s.log" % stamp)
            with open(p, "w", encoding="utf-8") as f:
                f.write("FullUI installer %s (python %s)\n" % (VERSION, sys.version.split()[0]))
            LOGFILE = p
            return
        except Exception:
            continue


def say(m="", color=None):
    codes = {"red": "31", "green": "32", "yellow": "33", "cyan": "36"}
    if color and sys.stdout.isatty() and color in codes:
        print("\033[%sm%s\033[0m" % (codes[color], m))
    else:
        print(m)
    sys.stdout.flush()
    log(m)


def step(n, m):
    say()
    say("[%d/%d] %s" % (n, STATE["total"], m), "cyan")


def ok(m):
    say("      OK   " + m, "green")


def info(m):
    say("      " + m)


def warn(m):
    say("      NOTE " + m, "yellow")


# ----------------------------------------------------------------- prompts
def ask(prompt, default=""):
    if O["unattended"]:
        return default
    suffix = " [%s]" % default if default else ""
    try:
        r = input("      %s%s: " % (prompt, suffix))
    except EOFError:
        r = ""
    r = r.strip()
    return r or default


def ask_yn(prompt, default):
    if O["unattended"]:
        return default
    d = "Y/n" if default else "y/N"
    while True:
        try:
            r = input("      %s (%s): " % (prompt, d))
        except EOFError:
            r = ""
        r = r.strip().lower()
        if not r:
            return default
        if r in ("y", "yes"):
            return True
        if r in ("n", "no"):
            return False
        info("Please type y or n.")


def ask_secret(prompt):
    if O["unattended"]:
        return ""
    if not sys.stdin.isatty():
        try:
            return input("      %s: " % prompt)
        except EOFError:
            return ""
    return getpass.getpass("      %s: " % prompt)


# ----------------------------------------------------------------- http
def net_kind(msg):
    m = str(msg).lower()
    if re.search(r"ssl|tls|certificate", m):
        return "tls"
    if re.search(r"resolved|no such host|name or service|nodename|getaddrinfo", m):
        return "dns"
    if "refused" in m:
        return "refused"
    if re.search(r"timed out|timeout", m):
        return "timeout"
    if "proxy" in m or "407" in m:
        return "proxy"
    return "other"


class Resp(object):
    def __init__(self):
        self.status, self.body, self.json, self.error, self.kind = 0, "", None, "", ""


def ssl_ctx():
    if STATE["insecure"]:
        c = ssl.create_default_context()
        c.check_hostname = False
        c.verify_mode = ssl.CERT_NONE
        return c
    return None


def api(method, url, body=None, token="__default__", timeout=30, retries=2, headers=None, auth=True):
    if token == "__default__":
        token = STATE["token"]
    attempt = 0
    while True:
        attempt += 1
        r = Resp()
        try:
            h = {"Accept": "application/json", "User-Agent": "FullUI-Installer/" + VERSION}
            if auth:
                dev = re.sub(r"[^A-Za-z0-9_-]", "", socket.gethostname()) or "pc"
                a = 'MediaBrowser Client="FullUI Installer", Device="%s", DeviceId="fullui-installer-%s", Version="%s"' % (dev, dev, VERSION)
                if token:
                    a += ', Token="%s"' % token
                h["Authorization"] = a
            if headers:
                h.update(headers)
            data = None
            if body is not None:
                data = (body if isinstance(body, str) else json.dumps(body)).encode("utf-8")
                h["Content-Type"] = "application/json"
            req = urllib.request.Request(url, data=data, headers=h, method=method)
            try:
                resp = urllib.request.urlopen(req, timeout=timeout, context=ssl_ctx())
                r.status = resp.getcode()
                raw = resp.read()
            except urllib.error.HTTPError as e:
                r.status = e.code
                raw = e.read()
            r.body = raw.decode("utf-8", "replace")
            s = r.body.lstrip()
            if s[:1] in ("{", "["):
                try:
                    r.json = json.loads(r.body)
                except Exception:
                    pass
        except Exception as e:  # network level
            msg = str(getattr(e, "reason", e)) + " " + str(e)
            r.error, r.kind = msg, net_kind(msg)
        log("HTTP %s %s -> %s %s" % (method, url, r.status, r.error))
        transient = (r.status == 0 and r.kind not in ("tls", "dns")) or r.status in (408, 429, 502, 503, 504)
        if transient and attempt <= retries:
            time.sleep(2 ** (attempt - 1))
            continue
        return r


def explain(r, what):
    if r.status == 0:
        return {
            "tls": "%s failed because of a security-certificate problem (HTTPS)." % what,
            "dns": "%s failed because the address could not be found. Check the spelling and your internet/network." % what,
            "refused": "%s failed because nothing is listening there (connection refused). Is Jellyfin running?" % what,
            "timeout": "%s timed out. The other side did not answer in time." % what,
            "proxy": "%s was blocked by a proxy server on your network." % what,
        }.get(r.kind, "%s failed because of a network problem." % what)
    if r.status == 401:
        return "%s was refused: not signed in or wrong credentials (HTTP 401)." % what
    if r.status == 403:
        return "%s was refused: this account is not allowed to do that (HTTP 403)." % what
    if r.status == 404:
        return "%s was not found (HTTP 404). The address may be wrong, or this Jellyfin does not have that feature." % what
    if r.status >= 500:
        return "%s hit an error on the server side (HTTP %d). Jellyfin may still be starting, or something went wrong inside it." % (what, r.status)
    return "%s failed (HTTP %d)." % (what, r.status)


def A(path):
    return STATE["base"] + path


def nv(v):
    p = [x for x in re.sub(r"[^0-9.].*$", "", str(v)).split(".") if x != ""]
    while len(p) < 4:
        p.append("0")
    return tuple(int(x) for x in p[:4])


def prop(o, name):
    if not isinstance(o, dict):
        return None
    for k, v in o.items():
        if k.lower() == name.lower():
            return v
    return None


def setprop(o, name, val):
    for k in list(o.keys()):
        if k.lower() == name.lower():
            o[k] = val
            return
    o[name] = val


def is_local(base):
    try:
        h = (urllib.parse.urlparse(base).hostname or "").lower()
        if h in ("localhost", "127.0.0.1", "::1"):
            return True
        if h == socket.gethostname().lower():
            return True
        return h in socket.gethostbyname_ex(socket.gethostname())[2]
    except Exception:
        return False


# ----------------------------------------------------------------- steps
def candidates(s):
    t = s.strip().rstrip("/")
    if re.match(r"^https?://", t):
        return [t]
    if re.search(r":\d+$", t):
        return ["http://" + t, "https://" + t]
    return ["http://%s:8096" % t, "http://" + t, "https://" + t, "https://%s:8920" % t]


def test_jellyfin(base):
    r = api("GET", base + "/System/Info/Public", timeout=8, retries=0, auth=False)
    good = r.status == 200 and isinstance(r.json, dict) and prop(r.json, "Version")
    return bool(good), r


def find_jellyfin():
    tries = 0
    lst = candidates(O["server"]) if O["server"] else ["http://localhost:8096", "https://localhost:8920"]
    while True:
        for c in lst:
            info("Trying %s ..." % c)
            good, r = test_jellyfin(c)
            if not good and r.kind == "tls" and not STATE["insecure"]:
                warn("%s uses a security certificate this computer does not trust (normal for home servers with a self-made certificate)." % c)
                if O["unattended"] or ask_yn("Continue anyway? The password will still be encrypted in transit, but the server identity is not checked.", True):
                    STATE["insecure"] = True
                    good, r = test_jellyfin(c)
            if good:
                STATE["base"] = c
                return r.json
        tries += 1
        if O["unattended"] or tries >= 3:
            raise Fail("I could not find a Jellyfin server%s." % (" at '%s'" % O["server"] if O["server"] else " on this computer"),
                       "Make sure Jellyfin is running (open it in your browser first). If it is on another computer, run this again with its address, e.g. -Server 192.168.1.20:8096.")
        if tries == 1 and not O["server"]:
            info("Jellyfin was not found on this computer at the usual addresses.")
        info("Please type the address you use in your browser to open Jellyfin,")
        info("for example 192.168.1.20 or 192.168.1.20:8096 or http://myserver:8096")
        a = ask("Jellyfin address", "")
        if not a:
            raise Cancel("No address given.")
        lst = candidates(a)


def sign_in():
    if O["apikey"]:
        add_secret(O["apikey"])
        STATE["token"] = O["apikey"]
        r = api("GET", A("/System/Configuration"), retries=1)
        if r.status == 200:
            ok("API key accepted (administrator access).")
            return
        if r.status in (401, 403):
            raise Fail("Jellyfin did not accept that API key.", "Create a new one in Jellyfin: Dashboard > Advanced > API Keys, then run this again.")
        raise Fail(explain(r, "Checking the API key"), "Check that Jellyfin is running, then run this file again.", r.error)
    user, pw, attempt = O["username"], O["password"], 0
    while True:
        attempt += 1
        if not user:
            info("I need the username and password of a Jellyfin ADMINISTRATOR account.")
            info("(The password is only used right now to sign in. It is never saved or written to the log.)")
            user = ask("Admin username", "")
        if not user:
            raise Cancel("No username given.")
        if not pw:
            pw = ask_secret("Password (typing is hidden)")
        add_secret(pw)
        r = api("POST", A("/Users/AuthenticateByName"), {"Username": user, "Pw": pw}, retries=1, auth=False)
        if r.status == 200 and isinstance(r.json, dict) and prop(r.json, "AccessToken"):
            tok = prop(r.json, "AccessToken")
            add_secret(tok)
            admin = bool(prop(prop(prop(r.json, "User"), "Policy"), "IsAdministrator"))
            if admin:
                STATE["token"], STATE["used_pw"] = tok, True
                ok("Signed in as administrator '%s'." % user)
                return
            api("POST", A("/Sessions/Logout"), token=tok, retries=0)
            if O["unattended"] or attempt >= 3:
                raise Fail("'%s' is a normal user, not an administrator." % user, "Run this again with the admin account (the one you use for Dashboard > Settings).")
            warn("'%s' is a normal user, not an administrator. Installing plugins needs an admin account." % user)
            user, pw = "", ""
            continue
        if r.status in (400, 401, 403):
            if O["unattended"] or attempt >= 3:
                raise Fail("The username or password was not accepted.", "Double-check them (the password is case-sensitive) by signing in to Jellyfin in your browser, then run this again.")
            warn("That username or password was not accepted. Try again (%d of 3 used)." % attempt)
            user, pw = "", ""
            continue
        raise Fail(explain(r, "Signing in"), "Check that Jellyfin is running and reachable, then run this file again.", r.error)


def ensure_repo(name, urls):
    chosen = None
    for u in urls:
        r = api("GET", u, timeout=30, retries=2, auth=False)
        if r.status == 200 and r.json is not None:
            chosen = u
            break
        warn(explain(r, "Reaching the %s list at %s" % (name, u)) + " Trying the next address if there is one.")
    if not chosen:
        raise Fail("This computer cannot download the %s plugin list." % name,
                   "Check your internet connection (and any proxy or firewall), or try again later - the site may be temporarily down.", ", ".join(urls))
    c = api("GET", A("/System/Configuration"), retries=2)
    if c.status != 200 or not isinstance(c.json, dict):
        raise Fail(explain(c, "Reading the Jellyfin settings"), "Make sure you signed in with an administrator account, then run this file again.", c.error)
    cfg = c.json
    repos = list(prop(cfg, "PluginRepositories") or [])
    norm = lambda x: str(x or "").strip().rstrip("/").lower()
    found = None
    for rp in repos:
        if norm(prop(rp, "Url")) == norm(chosen):
            found = rp
    if found:
        if prop(found, "Enabled"):
            ok("%s repository already present." % name)
            return chosen
        setprop(found, "Enabled", True)
        info("%s repository was switched off; turning it on." % name)
    else:
        repos.append({"Name": name, "Url": chosen, "Enabled": True})
        info("Adding the %s repository." % name)
    setprop(cfg, "PluginRepositories", repos)
    w = api("POST", A("/System/Configuration"), cfg, retries=1)
    if w.status not in (200, 204):
        raise Fail(explain(w, "Saving the %s repository in Jellyfin" % name),
                   "You need an administrator account. If this keeps happening, add it by hand: Dashboard > Plugins > Repositories > +.", w.error)
    ok("%s repository added." % name)
    return chosen


def installed_plugins():
    r = api("GET", A("/Plugins"), retries=2)
    return r.json if r.status == 200 and isinstance(r.json, list) else None


def find_installed(plugins, name, guid):
    for p in plugins or []:
        if str(prop(p, "Name")).lower() == name.lower() or (guid and str(prop(p, "Id")).replace("-", "").lower() == guid.replace("-", "").lower()):
            return p
    return None


def select_version(pkg, srv):
    best = same = None
    for v in prop(pkg, "versions") or []:
        abi, ver = nv(prop(v, "targetAbi")), nv(prop(v, "version"))
        if abi > srv:
            continue
        key = (abi, ver)
        if best is None or key > best[0]:
            best = (key, v)
        if abi[:2] == srv[:2] and (same is None or key > same[0]):
            same = (key, v)
    if same:
        return same[1], True
    if best:
        return best[1], False
    return None, False


def install_one(display, guid):
    r = api("GET", A("/Packages"), timeout=120, retries=2)
    if r.status != 200:
        raise Fail(explain(r, "Asking Jellyfin for the list of plugins"),
                   "Jellyfin itself needs internet access to read plugin lists. Check the server computer is online, then run this again.", r.error)
    pkg = None
    for p in r.json or []:
        if str(prop(p, "name")).lower() == display.lower() or (guid and str(prop(p, "guid")).replace("-", "").lower() == guid.replace("-", "").lower()):
            pkg = p
            break
    if not pkg:
        raise Fail("Jellyfin's plugin catalog does not list '%s'." % display,
                   "Jellyfin could not read the repository (the server needs internet access), or the plugin has not been published yet. Check the server is online and run this again in a minute.")
    v, exact = select_version(pkg, nv(STATE["version"]))
    if not v:
        raise Fail("No version of %s is built for Jellyfin %s." % (display, STATE["version"]),
                   "The plugin author may not have updated it for your Jellyfin yet. Try again later, or update/downgrade Jellyfin to a 10.11.x version.")
    ver = prop(v, "version")
    if not exact:
        warn("%s's newest build targets an older Jellyfin than yours; it may not load." % display)
    inst = find_installed(installed_plugins(), display, guid)
    if inst:
        st = str(prop(inst, "Status"))
        if nv(prop(inst, "Version")) >= nv(ver) and st not in ("NotSupported", "Malfunctioned"):
            ok("%s %s is already installed and up to date." % (display, prop(inst, "Version")))
            return "uptodate"
        info("Updating %s from %s to %s." % (display, prop(inst, "Version"), ver))
    else:
        info("Installing %s %s (Jellyfin downloads it itself; this can take a minute)." % (display, ver))
    q = "assemblyGuid=%s&version=%s" % (urllib.parse.quote(str(prop(pkg, "guid")), safe=""), urllib.parse.quote(str(ver), safe=""))
    if prop(v, "repositoryUrl"):
        q += "&repositoryUrl=" + urllib.parse.quote(str(prop(v, "repositoryUrl")), safe="")
    res = api("POST", A("/Packages/Installed/%s?%s" % (urllib.parse.quote(display, safe=""), q)), timeout=O["installtimeoutsec"], retries=1)
    if res.status not in (200, 204):
        if res.status == 0 and res.kind == "timeout":
            warn("Jellyfin is taking a long time to answer; checking whether the install went through anyway.")
        else:
            hint = "Make sure the server computer has internet access and enough free disk space, then run this again."
            if res.status >= 500:
                hint = "Jellyfin could not download or unpack the file (a blocked download, full disk, or a file in use by antivirus are common causes). Check Dashboard > Logs in Jellyfin, then run this again."
            raise Fail(explain(res, "Installing " + display), hint, res.body)
    deadline = time.time() + O["installtimeoutsec"]
    while time.time() < deadline:
        inst = find_installed(installed_plugins(), display, guid)
        if inst and nv(prop(inst, "Version")) >= nv(ver):
            ok("%s %s installed (it becomes active after the restart)." % (display, prop(inst, "Version")))
            return "installed"
        time.sleep(O["pollsec"])
    raise Fail("%s did not show up as installed within %d seconds." % (display, O["installtimeoutsec"]),
               "Look at Dashboard > Logs in Jellyfin for download errors, then run this again - it will continue where it stopped.")


def restart_and_wait():
    info("Restarting Jellyfin now. The website will be offline for a short moment.")
    r = api("POST", A("/System/Restart"), retries=0)
    if r.status not in (200, 204) and r.status != 0:
        raise Fail(explain(r, "Asking Jellyfin to restart"),
                   "Restart Jellyfin by hand (Dashboard > Restart, or restart the Jellyfin service / tray app), then run this file again.", r.body)
    start = time.time()
    saw_down, last_dot = False, time.time()
    while time.time() - start < O["restarttimeoutsec"]:
        time.sleep(O["pollsec"])
        good, _ = test_jellyfin(STATE["base"])
        el = int(time.time() - start)
        if not good:
            saw_down = True
        elif saw_down or el >= 20:
            if api("GET", A("/Plugins"), retries=0).status == 200:
                ok("Jellyfin is back after about %d seconds." % el)
                return
        if time.time() - last_dot >= 10:
            info("...still waiting (%d s)" % el)
            last_dot = time.time()
    raise Fail("Jellyfin did not come back within %d seconds." % O["restarttimeoutsec"],
               "Start Jellyfin by hand (Windows: open the Jellyfin tray app or the 'Jellyfin Server' service; Linux: 'sudo systemctl restart jellyfin'). Once it is running, run this file again - nothing is lost.")


def verify_active():
    deadline = time.time() + 90
    want = [("File Transformation", None), ("FullUI", FULLUI_GUID)]
    last = ""
    while True:
        plugins = installed_plugins()
        bad = []
        for n, g in want:
            p = find_installed(plugins, n, g)
            st = str(prop(p, "Status")) if p else ""
            if st != "Active":
                bad.append("%s=%s" % (n, st or "missing"))
        if not bad:
            ok("File Transformation and FullUI are both Active.")
            return
        last = ", ".join(bad)
        if re.search(r"Malfunctioned|NotSupported|Disabled", last) and not re.search(r"Restart|missing", last):
            break
        if time.time() >= deadline:
            break
        time.sleep(O["pollsec"])
    fix = "Open Jellyfin > Dashboard > Logs and look for lines mentioning the plugin; then run this file again."
    if "Restart" in last:
        fix = "Jellyfin has not restarted yet. Restart it by hand (Dashboard > Restart), then run this file again."
    if "Malfunctioned" in last:
        fix = "The plugin crashed while loading, usually because it was built for a different Jellyfin version. Check Dashboard > Logs, and see docs/INSTALL.md troubleshooting."
    if "NotSupported" in last:
        fix = "This plugin does not support your Jellyfin version. See docs/INSTALL.md troubleshooting."
    raise Fail("A plugin did not become active (%s)." % last, fix)


def get_pcfg():
    r = api("GET", A("/Plugins/%s/Configuration" % FULLUI_GUID), retries=2)
    return r.json if r.status == 200 and isinstance(r.json, dict) else None


def save_pcfg(cfg):
    r = api("POST", A("/Plugins/%s/Configuration" % FULLUI_GUID), cfg, retries=1)
    if r.status not in (200, 204):
        raise Fail(explain(r, "Saving the FullUI settings"), "You can set these later in Jellyfin: Dashboard > Plugins > FullUI.", r.body)


def test_tmdb(key):
    key = key.strip()
    if key.startswith("eyJ") or len(key) > 40:
        return api("GET", O["tmdbbaseurl"] + "/configuration", headers={"Authorization": "Bearer " + key}, auth=False)
    return api("GET", O["tmdbbaseurl"] + "/configuration?api_key=" + urllib.parse.quote(key, safe=""), auth=False)


def configure_tmdb(cfg):
    existing = str(prop(cfg, "TmdbApiKey") or "")
    key = O["tmdbkey"]
    if not key and existing and not O["unattended"]:
        ok("A TMDB key is already saved. Keeping it.")
        return False
    if not key and O["unattended"]:
        return False
    if not key:
        info("")
        info('OPTIONAL: movie info for "Coming Soon" and trailers comes from TMDB (themoviedb.org).')
        info("It needs a free key: make a free account at themoviedb.org, open Settings > API,")
        info('and copy the "API Key" (a 32-letter/number code) or the long "Read Access Token".')
        info("Press Enter to skip - you can add it later in Dashboard > Plugins > FullUI.")
    tries = 0
    while True:
        tries += 1
        if not key:
            key = ask_secret("TMDB key (hidden; Enter to skip)")
        if not key:
            info("Skipping TMDB for now.")
            return False
        add_secret(key)
        t = test_tmdb(key)
        if t.status == 200:
            setprop(cfg, "TmdbApiKey", key.strip())
            ok("TMDB accepted the key.")
            return True
        if t.status in (401, 403):
            if O["unattended"] or tries >= 3:
                warn("TMDB rejected the key, so I did not save it. Add it later in the plugin settings.")
                return False
            warn("TMDB said that key is not valid. Please copy it again (no spaces).")
            key = ""
            continue
        warn(explain(t, "Checking the key with TMDB") + " I cannot verify the key right now.")
        if not O["unattended"] and ask_yn("Save it anyway (you can test it later in the plugin settings)?", False):
            setprop(cfg, "TmdbApiKey", key.strip())
            return True
        return False


def setup_ollama(cfg):
    if not is_local(STATE["base"]):
        warn("Ollama has to be installed on the same computer as Jellyfin. Run this installer on that computer to add it. Skipping.")
        return False
    exe = shutil.which("ollama")
    if exe:
        ok("Ollama is already installed.")
    else:
        info("Installing Ollama (this can take a few minutes)...")
        if sys.platform.startswith("linux"):
            rc = subprocess.call("curl -fsSL https://ollama.com/install.sh | sh", shell=True)
            if rc != 0:
                warn("The Ollama install script failed (it usually needs sudo/root). You can install it yourself from ollama.com.")
                return False
        else:
            warn("Please install Ollama from https://ollama.com/download, then run this installer again.")
            return False
        exe = shutil.which("ollama")
        if not exe:
            warn("Ollama was installed but I cannot find it yet. Open a NEW terminal and run this installer again.")
            return False
        ok("Ollama installed.")
    up = False
    for i in range(20):
        if api("GET", "http://localhost:11434/api/version", timeout=3, retries=0, auth=False).status == 200:
            up = True
            break
        if i == 1:
            try:
                subprocess.Popen([exe, "serve"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            except Exception:
                pass
        time.sleep(3)
    if not up:
        warn("Ollama is not answering on port 11434. Start it and run this installer again.")
        return False
    for m in ("nomic-embed-text", "llama3.2:3b"):
        info("Downloading AI model %s (a few hundred MB to 2 GB; please wait)..." % m)
        good = False
        for _ in range(2):
            if subprocess.call([exe, "pull", m]) == 0:
                good = True
                break
        if not good:
            warn("Could not download %s. Try 'ollama pull %s' yourself later." % (m, m))
            return False
        ok("Model %s ready." % m)
    setprop(cfg, "OllamaEnabled", True)
    setprop(cfg, "OllamaUrl", "http://localhost:11434")
    setprop(cfg, "OllamaEmbedModel", "nomic-embed-text")
    setprop(cfg, "OllamaChatModel", "llama3.2:3b")
    return True


def configure_plugin():
    cfg = get_pcfg()
    if cfg is None:
        warn("Could not read the FullUI settings; skipping optional setup. Use Dashboard > Plugins > FullUI later.")
        return
    changed = False
    cur = str(prop(cfg, "ServerName") or "")
    if O["servername"]:
        setprop(cfg, "ServerName", O["servername"])
        changed = True
    elif not O["unattended"] and not O["update"]:
        n = ask("What should your site be called? (the name shown at the top)", cur or "FullUI")
        if n and n != cur:
            setprop(cfg, "ServerName", n)
            changed = True
    try:
        changed = configure_tmdb(cfg) or changed
    except Fail:
        raise
    except Exception as e:
        warn("TMDB setup skipped due to an error (see log).")
        log("tmdb: %s" % e)
    want = False
    if not O["skipollama"]:
        if O["installollama"]:
            want = True
        elif not O["unattended"] and not O["update"] and not prop(cfg, "OllamaEnabled"):
            info("")
            info('OPTIONAL: smarter search ("find me a funny space movie") uses a small AI that runs on this')
            info("computer (Ollama). It is a free download of a few GB and works fine on a normal CPU.")
            want = ask_yn("Install Ollama and the two small AI models now?", False)
    if want:
        try:
            changed = setup_ollama(cfg) or changed
        except Exception as e:
            warn("Ollama setup did not complete (see log). The rest works without it.")
            log("ollama: %s" % e)
    if changed:
        save_pcfg(cfg)
        ok("FullUI settings saved.")
    else:
        info("No settings needed changing.")


def self_test():
    good = True
    s = api("GET", A("/FullUI/Status"), retries=3)
    if s.status == 200 and isinstance(s.json, dict):
        ok("FullUI answers (site name: '%s')." % prop(s.json, "serverName"))
    else:
        good = False
        warn(explain(s, "The FullUI status check") + " The plugin may need another moment; reload in a minute.")
    js = api("GET", A("/FullUI/web/fullui.js"), retries=1, auth=False)
    if js.status == 200:
        ok("The FullUI web bundle is being served.")
    else:
        good = False
        warn("The FullUI web bundle was not served (the plugin build may be missing its web files).")
    ix = api("GET", A("/web/index.html"), retries=1, auth=False)
    if ix.status == 200 and "/FullUI/web/fullui.js" in ix.body:
        ok("Jellyfin web pages are loading the FullUI bundle (File Transformation is working).")
    else:
        good = False
        warn("The Jellyfin web page does NOT load FullUI yet.")
        info("Most likely File Transformation has not applied. Restart Jellyfin once more, wait a minute,")
        info('then run this file again. See docs/INSTALL.md, "The site looks like normal Jellyfin".')
    return good


def show_firetv():
    say()
    say("Fire TV / Fire Stick app")
    h = api("HEAD", O["apkurl"], timeout=15, retries=1, auth=False)
    if h.status == 404:
        info("The Fire TV app has not been published yet. It is coming; run this installer with -Update later")
        info('or check https://github.com/Narsmow/FullUISuit/releases for "FullUI-FireTV.apk".')
        return
    info('1. On the Fire TV, install the free "Downloader" app from the Amazon Appstore.')
    info("2. Settings > My Fire TV > Developer Options > Install unknown apps > allow Downloader.")
    info("3. Open Downloader and type exactly:")
    info("      " + O["apkurl"])
    info("4. Choose Install. Then sign in with your Jellyfin address and username.")
    if h.status != 200:
        info("(I could not confirm the file is online right now; if the download fails, it is not published yet.)")
    adb = shutil.which("adb")
    if adb and (not O["unattended"] or O["firetvip"]):
        ip = O["firetvip"] or ask("adb found. To install on the Fire TV right now, type its IP address (Fire TV: Settings > My Fire TV > About > Network). Enter to skip", "")
        if ip:
            try:
                tmp = os.path.join(tempfile.gettempdir(), "FullUI-FireTV.apk")
                urllib.request.urlretrieve(O["apkurl"], tmp)
                subprocess.call([adb, "connect", ip + ":5555"])
                if subprocess.call([adb, "-s", ip + ":5555", "install", "-r", tmp]) == 0:
                    ok("Installed on the Fire TV.")
                else:
                    warn("adb could not install it (is ADB debugging on? Allow the connection on the TV screen). Use the Downloader steps above.")
            except Exception as e:
                warn("Could not install over adb. Use the Downloader steps above.")
                log("adb: %s" % e)


def summary(good):
    say()
    say("=" * 62)
    say("  All done! FullUI is installed." if good else "  FullUI is installed, but one check needs attention (see above).", "green" if good else "yellow")
    say("=" * 62)
    say("  Open your site:   %s/web/   (press Ctrl+F5 once to refresh)" % STATE["base"])
    say("  Log in:           with your normal Jellyfin username and password.")
    say("  Settings:         Dashboard > Plugins > FullUI (name, TMDB key, AI search).")
    say("  Update later:     run this same file again.")
    say("  Uninstall:        run it with -Uninstall (see docs/INSTALL.md).")
    if LOGFILE:
        say("  Technical log:    " + LOGFILE)


# ----------------------------------------------------------------- flows
def run_install():
    STATE["total"] = 9
    step(1, "Looking for your Jellyfin server...")
    inf = find_jellyfin()
    ok("Found '%s' at %s" % (prop(inf, "ServerName"), STATE["base"]))

    step(2, "Checking the Jellyfin version...")
    STATE["version"] = str(prop(inf, "Version"))
    sv = nv(STATE["version"])
    if sv[0] == 10 and sv[1] == 11:
        ok("Version %s - supported." % STATE["version"])
    else:
        warn("Your Jellyfin is version %s. FullUI is built for 10.11.x only." % STATE["version"])
        info("On other versions the plugin may refuse to load or show a broken page.")
        info("Best fix: update Jellyfin to 10.11.x (jellyfin.org/downloads) and run this again.")
        if not O["allowotherversion"]:
            if O["unattended"]:
                raise Fail("Jellyfin %s is not 10.11.x." % STATE["version"], "Update Jellyfin to 10.11.x, or pass -AllowOtherVersion to try anyway.")
            if not ask_yn("Continue anyway at your own risk?", False):
                raise Cancel("Stopped because of the Jellyfin version. Nothing was changed.")
        else:
            info("-AllowOtherVersion given; continuing.")

    step(3, "Signing in as a Jellyfin administrator...")
    sign_in()

    step(4, "Adding the plugin download sources (repositories)...")
    ensure_repo("File Transformation", [O["filetransformationrepourl"]])
    urls = [O["fulluirepourl"]]
    if O["fulluirepofallbackurl"] and O["fulluirepofallbackurl"] != O["fulluirepourl"]:
        urls.append(O["fulluirepofallbackurl"])
    ensure_repo("FullUI", urls)

    step(5, "Installing File Transformation (lets plugins change the web page)...")
    a = install_one("File Transformation", None)
    step(6, "Installing FullUI...")
    b = install_one("FullUI", FULLUI_GUID)
    if O["update"] and a == "uptodate" and b == "uptodate":
        say()
        ok("Everything is already on the newest version. Nothing to update.")
        STATE["done"] = True
        return

    step(7, "Restarting Jellyfin so the plugins load...")
    plugins = installed_plugins()
    pending = any((p and str(prop(p, "Status")) == "Restart") for p in [find_installed(plugins, "File Transformation", None), find_installed(plugins, "FullUI", FULLUI_GUID)])
    if a == "uptodate" and b == "uptodate" and not pending:
        ok("Nothing new was installed, so no restart is needed.")
    else:
        if not O["unattended"] and not ask_yn("Jellyfin will restart now (anyone watching will be interrupted for ~1 minute). Continue?", True):
            raise Cancel("You chose not to restart. The plugins are downloaded and will start the next time Jellyfin restarts; run this file again then.")
        restart_and_wait()

    step(8, "Checking that both plugins are active...")
    verify_active()
    step(9, "Final setup and self-test...")
    configure_plugin()
    good = self_test()
    show_firetv()
    summary(good)
    STATE["done"] = True


def run_uninstall():
    STATE["total"] = 6
    step(1, "Looking for your Jellyfin server...")
    inf = find_jellyfin()
    ok("Found '%s' at %s" % (prop(inf, "ServerName"), STATE["base"]))
    STATE["version"] = str(prop(inf, "Version"))
    step(2, "Signing in as a Jellyfin administrator...")
    sign_in()
    step(3, "Looking at installed plugins...")
    plugins = installed_plugins()
    if plugins is None:
        raise Fail("I could not read the list of installed plugins.", "Check that you used an administrator account, then run this again.")
    fu = find_installed(plugins, "FullUI", FULLUI_GUID)
    ft = find_installed(plugins, "File Transformation", None)
    if not fu:
        ok("FullUI is not installed. Nothing to remove.")
    else:
        info("FullUI %s is installed." % prop(fu, "Version"))
    if not O["unattended"] and fu:
        if not ask_yn("Really remove FullUI? (Your settings and data are kept so a later reinstall picks up where you left off.)", False):
            raise Cancel("Cancelled. Nothing was removed.")
    removed = False
    step(4, "Removing FullUI...")
    if fu:
        r = api("DELETE", A("/Plugins/%s/%s" % (prop(fu, "Id"), prop(fu, "Version"))), retries=1)
        if r.status not in (200, 204):
            raise Fail(explain(r, "Removing FullUI"), "Use an administrator account, or remove it in Dashboard > Plugins > FullUI > Uninstall.", r.body)
        ok("FullUI removed.")
        removed = True
    else:
        info("Skipped.")
    step(5, "File Transformation (other plugins may use it)...")
    rm = O["removefiletransformation"]
    if ft and not rm and not O["unattended"]:
        rm = ask_yn("Also remove File Transformation? Choose No if you use other plugins like Home Screen Sections.", False)
    if ft and rm:
        r = api("DELETE", A("/Plugins/%s/%s" % (prop(ft, "Id"), prop(ft, "Version"))), retries=1)
        if r.status not in (200, 204):
            raise Fail(explain(r, "Removing File Transformation"), "Remove it in Dashboard > Plugins instead.", r.body)
        ok("File Transformation removed.")
        removed = True
    else:
        info("Kept.")
    step(6, "Finishing...")
    if removed:
        if O["unattended"] or ask_yn("Restart Jellyfin now so the removal takes effect?", True):
            restart_and_wait()
        else:
            info("Restart Jellyfin yourself later (Dashboard > Restart) to finish.")
    if O["purgedata"]:
        info("Note: Jellyfin cannot delete plugin data through its web API. To erase it, stop Jellyfin and delete the")
        info("FullUI folders under its data directory: plugins/configurations/ (FullUI*.xml) and data/plugins/FullUI.")
    else:
        info("Your FullUI settings and data were kept.")
    say()
    say("  Done. FullUI has been uninstalled.", "green")
    STATE["done"] = True


def main():
    global O
    O = parse_args(sys.argv[1:])
    init_log()
    say()
    say("FullUI installer %s" % VERSION)
    say("This will set up FullUI on your Jellyfin server. It is safe to run again at any time.")
    code = 1
    try:
        (run_uninstall if O["uninstall"] else run_install)()
        code = 0
    except Cancel as e:
        STATE["reported"] = True
        say()
        say("STOPPED: %s" % e, "yellow")
        say("  Nothing is half-installed. You can run this file again whenever you like.")
        code = 2
    except KeyboardInterrupt:
        STATE["reported"] = True
        say()
        say("STOPPED: you cancelled the installer.", "yellow")
        say("  It is safe to run the file again; it picks up where it stopped.")
        code = 2
    except Fail as e:
        STATE["reported"] = True
        say()
        say("SOMETHING WENT WRONG", "red")
        say("  What happened: %s" % e, "red")
        say("  What to try:   %s" % e.fix)
        say("  It is safe to run this file again - it never duplicates anything.")
        log("ERROR: %s" % e)
        if e.tech:
            log("detail: %s" % e.tech)
        if LOGFILE:
            say("  Technical log:   " + LOGFILE)
    except Exception as e:
        STATE["reported"] = True
        import traceback
        say()
        say("SOMETHING WENT WRONG", "red")
        say("  What happened: an unexpected problem occurred inside the installer.", "red")
        say("  What to try:   run this file again; if it repeats, send the log file (below) to whoever set this up.")
        say("  It is safe to run this file again - it never duplicates anything.")
        log("ERROR: %r" % e)
        log(traceback.format_exc())
        if LOGFILE:
            say("  Technical log:   " + LOGFILE)
    finally:
        if STATE["used_pw"] and STATE["token"] and STATE["base"]:
            try:
                api("POST", A("/Sessions/Logout"), retries=0, timeout=5)
            except Exception:
                pass
    sys.exit(code)


if __name__ == "__main__":
    main()
