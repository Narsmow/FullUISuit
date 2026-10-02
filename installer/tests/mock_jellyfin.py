#!/usr/bin/env python3
"""Minimal fake Jellyfin 10.11 server for testing the FullUI installer.

Implements only what the installer uses (shapes taken from the Jellyfin REST API):
  GET  /System/Info/Public         POST /Users/AuthenticateByName   POST /Sessions/Logout
  GET/POST /System/Configuration   POST /System/Restart
  GET  /Packages                   POST /Packages/Installed/{name}
  GET  /Plugins                    DELETE /Plugins/{id}/{version}
  GET/POST /Plugins/{id}/Configuration
  GET  /FullUI/Status  /FullUI/web/fullui.js  /web/index.html
  GET  /ft/manifest.json  /fu/manifest.json   (plugin repositories)
  GET  /3/configuration                        (fake TMDB)

Failure modes (comma separated, --modes a,b):
  wrong_version      server reports 10.10.7
  jf_10_11_5         server reports 10.11.5 (too old: FullUI needs 10.11.6+)
  jf_10_11_6         server reports 10.11.6 (the oldest supported); default is 10.11.11
  repo_down          FullUI manifest answers 503
  ft_repo_down       File Transformation manifest answers 503
  no_packages        server catalog never lists the plugins (server cannot reach repos)
  install_500        package install returns HTTP 500
  no_restart_return  after /System/Restart the server never comes back
  slow_restart       server stays down 6 s after restart
  no_injection       index.html is not modified (File Transformation not working)
  malfunction        FullUI shows up as Malfunctioned after restart
  tmdb_reject        fake TMDB rejects every key
  restart_refused    /System/Restart answers 403
  no_actual_restart  /System/Restart answers 204 but nothing restarts (server never goes down)
  old_ft_installed   user already has File Transformation 2.2.1.0 (Active, their own copy)
  ft_unsupported     ... and Jellyfin reports that copy as NotSupported
  no_repositories_endpoint  GET/POST /Repositories answer 404 (very old server)
  baseurl            everything is served under /jellyfin (Jellyfin "BaseUrl" setting)
  bad_inject_path    with baseurl: the injected script tag ignores the base path (browser would 404)
  tls                serve HTTPS with a self-signed certificate (installer must not trust it silently)
  redirect           a second port (FRONT=) answers every request with 301 -> the real server
Behaviour copied from real Jellyfin 10.11 (this mock must never be MORE lenient than the real thing):
  * POST /Users/AuthenticateByName without Authorization: MediaBrowser Client=, Device=, DeviceId=, Version= -> HTTP 500
  * installing another version of a plugin ADDS a version; older ones stay (Superseded after a restart)
  * GET /Plugins is ordered by name (then version, oldest first)
  * DELETE /Plugins/{id}/{version} removes only that exact version
  * GET/POST /Repositories (admin only) list / replace the plugin repositories
  * POST /System/Configuration replaces the whole configuration
Accounts: admin/Adm1nPass! (administrator), kid/KidPass1 (normal user).
Valid TMDB key: 0123456789abcdef0123456789abcdef
"""
import argparse
import json
import re
import sys
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs, unquote

FULLUI_GUID = "7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57"
FT_GUID = "5e87cc92-571a-4d8d-8d98-d2d4147f9f90"
GOOD_TMDB = "0123456789abcdef0123456789abcdef"
USERS = {"admin": ("Adm1nPass!", True), "kid": ("KidPass1", False)}


class State:
    def __init__(self, modes, port):
        self.modes = set(m for m in modes if m)
        self.port = port
        self.lock = threading.Lock()
        self.tokens = {}  # token -> (username, isAdmin)
        self.config = {
            "ServerName": "Mock", "EnableUPnP": False, "PublicPort": 8096,
            "PluginRepositories": [],
            "CachePath": "", "SomethingElse": {"Nested": [1, 2, 3]},
        }
        self.plugins = []  # dicts Name, Version, Id, Status
        self.plugin_cfg = {FULLUI_GUID: {"ServerName": "FullUI", "TmdbApiKey": "", "OllamaEnabled": False,
                                         "OllamaUrl": "http://localhost:11434"}}
        self.fu_versions = ["0.1.0.0"]
        if "old_ft_installed" in self.modes:
            self.plugins.append({"Name": "File Transformation", "Version": "2.2.1.0", "Id": FT_GUID,
                                 "Status": "NotSupported" if "ft_unsupported" in self.modes else "Active", "CanUninstall": True})
        self.down_until = 0.0
        self.down_forever = False
        self.injected = False
        self.log = []  # (method, path)
        self.restarts = 0

    @property
    def base(self):
        return "%s://127.0.0.1:%d" % ("https" if "tls" in self.modes else "http", self.port)

    def version(self):
        for m, v in (("wrong_version", "10.10.7"), ("jf_10_11_5", "10.11.5"), ("jf_10_11_6", "10.11.6")):
            if m in self.modes:
                return v
        return "10.11.11"

    def manifests(self):
        ft = [{"guid": FT_GUID, "name": "File Transformation", "description": "x", "overview": "x",
               "owner": "IAmParadox27", "category": "General",
               "versions": [
                   {"version": "2.2.1.0", "changelog": "old", "targetAbi": "10.10.0.0", "sourceUrl": self.base + "/dl/ft2.zip",
                    "checksum": "00", "timestamp": "2025-01-01T00:00:00Z"},
                   {"version": "3.0.0.0", "changelog": "new", "targetAbi": "10.11.0.0", "sourceUrl": self.base + "/dl/ft3.zip",
                    "checksum": "00", "timestamp": "2025-09-01T00:00:00Z"}]}]
        fu = [{"guid": FULLUI_GUID, "name": "FullUI", "description": "x", "overview": "x", "owner": "narsmow",
               "category": "General",
               "versions": [{"version": v, "changelog": "c", "targetAbi": "10.11.6.0",
                             "sourceUrl": self.base + "/dl/fu.zip", "checksum": "00", "timestamp": "2026-01-01T00:00:00Z"}
                            for v in reversed(self.fu_versions)]}]
        return ft, fu

    def is_down(self):
        return self.down_forever or time.time() < self.down_until

    def apply_restart(self):
        # like PluginManager at startup: per plugin id the HIGHEST version loads, older ones are Superseded
        best = {}
        for p in self.plugins:
            k = p["Id"]
            if k not in best or vkey(p["Version"]) > vkey(best[k]["Version"]):
                best[k] = p
        for p in self.plugins:
            if p is not best[p["Id"]]:
                p["Status"] = "Superseded"
            elif p["Status"] == "Restart":
                p["Status"] = "Malfunctioned" if (p["Name"] == "FullUI" and "malfunction" in self.modes) else "Active"
        ft_active = any(p["Name"] == "File Transformation" and p["Status"] == "Active" for p in self.plugins)
        fu_active = any(p["Name"] == "FullUI" and p["Status"] == "Active" for p in self.plugins)
        self.injected = ft_active and fu_active and "no_injection" not in self.modes


def vkey(v):
    return tuple(int(x) for x in (str(v).split(".") + ["0"] * 4)[:4])


def auth_fields(headers):
    """Parse 'MediaBrowser Client="..", Device=".."' like Jellyfin's AuthorizationContext."""
    raw = headers.get("X-Emby-Authorization") or headers.get("Authorization") or ""
    out = {}
    if raw.split(" ", 1)[0] in ("MediaBrowser", "Emby"):
        for m in re.finditer(r'(\w+)="([^"]*)"', raw):
            out[m.group(1)] = m.group(2)
    return out


def make_handler(st):
    class H(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *a):
            pass

        # -- helpers
        def send(self, code, obj=None, raw=None, ctype="application/json"):
            body = b""
            if raw is not None:
                body = raw if isinstance(raw, bytes) else raw.encode()
            elif obj is not None:
                body = json.dumps(obj).encode()
            self.send_response(code)
            if code != 204:
                self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)) if code != 204 else "0")
            self.end_headers()
            if self.command != "HEAD" and code != 204:
                self.wfile.write(body)

        def body(self):
            n = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(n) if n else b""
            try:
                return json.loads(raw) if raw else None
            except Exception:
                return None

        def token(self):
            a = self.headers.get("Authorization", "")
            if 'Token="' in a:
                return a.split('Token="', 1)[1].split('"', 1)[0]
            return None

        def user(self):
            return st.tokens.get(self.token())

        def need_admin(self):
            u = self.user()
            if not u:
                self.send(401, raw="")
                return None
            if not u[1]:
                self.send(403, raw="")
                return None
            return u

        # -- routing
        def handle_any(self):
            u = urlparse(self.path)
            path = u.path
            q = parse_qs(u.query)
            with st.lock:
                st.log.append((self.command, path))
            if path == "/__mock/state":
                return self.send(200, {"restarts": st.restarts, "log": st.log, "plugins": st.plugins,
                                       "config": st.config, "plugin_cfg": st.plugin_cfg})
            if path == "/__mock/publish" and self.command == "POST":  # a new FullUI version appears in the repository
                st.fu_versions.append((self.body() or {}).get("version", "0.2.0.0"))
                return self.send(204)
            if path in ("/fu-stale/manifest.json", "/fu-other/manifest.json"):
                # valid JSON that is NOT a usable FullUI list: empty versions / some other plugin
                ft, fu = st.manifests()
                if path.startswith("/fu-stale"):
                    fu[0]["versions"] = []
                    return self.send(200, fu)
                return self.send(200, ft)
            if path.startswith("/ft/") or path.startswith("/fu/"):
                if (path.startswith("/fu/") and "repo_down" in st.modes) or (path.startswith("/ft/") and "ft_repo_down" in st.modes):
                    return self.send(503, raw="down")
                ft, fu = st.manifests()
                return self.send(200, ft if path.startswith("/ft/") else fu)
            if path == "/3/configuration":
                key = (q.get("api_key") or [""])[0]
                auth = self.headers.get("Authorization", "")
                if "tmdb_reject" in st.modes or not (key == GOOD_TMDB or auth == "Bearer " + GOOD_TMDB):
                    return self.send(401, {"status_code": 7, "status_message": "Invalid API key"})
                return self.send(200, {"images": {}})
            if "baseurl" in st.modes:  # served under a base path, like Jellyfin's BaseUrl setting
                if path == "/jellyfin" or path.startswith("/jellyfin/"):
                    path = path[len("/jellyfin"):] or "/"
                else:
                    return self.send(404, raw="not found")
            if st.is_down():
                return self.send(503, raw="Server is starting")
            if path == "/System/Info/Public":
                return self.send(200, {"LocalAddress": st.base, "ServerName": "Mock Jellyfin", "Version": st.version(),
                                       "ProductName": "Jellyfin Server", "OperatingSystem": "", "Id": "abc",
                                       "StartupWizardCompleted": True})
            if path == "/Users/AuthenticateByName" and self.command == "POST":
                f = auth_fields(self.headers)
                if not (f.get("Client") and f.get("Device") and f.get("DeviceId") and f.get("Version")):
                    # real Jellyfin: SessionManager throws on empty app / device / version -> 500
                    return self.send(500, raw="Error processing request.")
                b = self.body() or {}
                name = b.get("Username", "")
                acct = USERS.get(name.lower())
                if not acct or acct[0] != b.get("Pw"):
                    return self.send(401, raw="")
                tok = uuid.uuid4().hex
                st.tokens[tok] = (name, acct[1])
                return self.send(200, {"User": {"Name": name, "Id": uuid.uuid4().hex, "Policy": {"IsAdministrator": acct[1]}},
                                       "AccessToken": tok, "ServerId": "abc"})
            if path == "/Sessions/Logout":
                st.tokens.pop(self.token(), None)
                return self.send(204)
            if path == "/System/Configuration":
                if not self.need_admin():
                    return
                if self.command == "GET":
                    return self.send(200, st.config)
                b = self.body()
                if not isinstance(b, dict):
                    return self.send(400, raw="bad")
                st.config = b
                return self.send(204)
            if path == "/System/Restart" and self.command == "POST":
                if "restart_refused" in st.modes:
                    return self.send(403, raw="")
                if not self.need_admin():
                    return
                st.restarts += 1
                if "no_actual_restart" in st.modes:
                    return self.send(204)
                delay = 6 if "slow_restart" in st.modes else 2
                self.send(204)
                if "no_restart_return" in st.modes:
                    st.down_forever = True
                else:
                    st.down_until = time.time() + delay
                    threading.Timer(delay, lambda: st.apply_restart()).start()
                return
            if path == "/Packages" and self.command == "GET":
                if not self.user():
                    return self.send(401, raw="")
                if "no_packages" in st.modes:
                    return self.send(200, [])
                ft, fu = st.manifests()
                out = []
                repos = {r["Url"]: r for r in st.config.get("PluginRepositories", []) if r.get("Enabled")}
                for pkgs, url in ((ft, st.base + "/ft/manifest.json"), (fu, st.base + "/fu/manifest.json")):
                    if url in repos and not (("repo_down" in st.modes and "/fu/" in url) or ("ft_repo_down" in st.modes and "/ft/" in url)):
                        for p in pkgs:
                            p = json.loads(json.dumps(p))
                            for v in p["versions"]:
                                v["repositoryName"] = repos[url]["Name"]
                                v["repositoryUrl"] = url
                            out.append(p)
                return self.send(200, out)
            if path.startswith("/Packages/Installed/") and self.command == "POST":
                if not self.need_admin():
                    return
                name = unquote(path[len("/Packages/Installed/"):])
                if "install_500" in st.modes:
                    return self.send(500, raw="download failed")
                ver = (q.get("version") or [""])[0]
                time.sleep(1)
                with st.lock:
                    gid = FT_GUID if name == "File Transformation" else FULLUI_GUID
                    # the same version is replaced in place; another version is ADDED beside the old one
                    st.plugins = [p for p in st.plugins if not (p["Id"] == gid and p["Version"] == ver)]
                    st.plugins.append({"Name": name, "Version": ver, "Id": gid, "Status": "Restart", "CanUninstall": True})
                    st.plugins.sort(key=lambda p: (p["Name"], vkey(p["Version"])))
                return self.send(204)
            if path == "/Plugins" and self.command == "GET":
                if not self.user():
                    return self.send(401, raw="")
                return self.send(200, sorted(st.plugins, key=lambda p: (p["Name"], vkey(p["Version"]))))
            if path.startswith("/Plugins/") and self.command == "DELETE":
                if not self.need_admin():
                    return
                parts = path.split("/")
                keep = [p for p in st.plugins if not (p["Id"] == parts[2] and p["Version"] == parts[3])]
                if len(keep) == len(st.plugins):
                    return self.send(404, raw="")
                st.plugins = keep
                return self.send(204)
            if path == "/Repositories" and self.command in ("GET", "POST"):
                if "no_repositories_endpoint" in st.modes:
                    return self.send(404, raw="")
                if not self.need_admin():
                    return
                if self.command == "GET":
                    return self.send(200, st.config.get("PluginRepositories", []))
                b = self.body()
                if not isinstance(b, list):
                    return self.send(400, raw="bad")
                st.config["PluginRepositories"] = b
                return self.send(204)
            if path.startswith("/Plugins/") and path.endswith("/Configuration"):
                if not self.need_admin():
                    return
                gid = path.split("/")[2]
                if gid not in st.plugin_cfg:
                    return self.send(404, raw="")
                if self.command == "GET":
                    return self.send(200, st.plugin_cfg[gid])
                b = self.body()
                st.plugin_cfg[gid] = b
                return self.send(204)
            if path == "/FullUI/Status":
                if not self.user():
                    return self.send(401, raw="")
                if not any(p["Name"] == "FullUI" and p["Status"] == "Active" for p in st.plugins):
                    return self.send(404, raw="")
                return self.send(200, {"serverName": st.plugin_cfg[FULLUI_GUID].get("ServerName"), "tmdbConfigured": False})
            if path == "/FullUI/web/fullui.js":
                if not any(p["Name"] == "FullUI" and p["Status"] == "Active" for p in st.plugins):
                    return self.send(404, raw="")
                return self.send(200, raw="/*js*/", ctype="application/javascript")
            if path in ("/web/index.html", "/web/"):
                html = "<html><body>jellyfin</body></html>"
                if st.injected:
                    html = html.replace("</body>", '<script defer src="%s/FullUI/web/fullui.js"></script></body>' % ("/jellyfin" if ("baseurl" in st.modes and "bad_inject_path" not in st.modes) else ""))
                return self.send(200, raw=html, ctype="text/html")
            return self.send(404, raw="not found")

        do_GET = do_POST = do_DELETE = do_HEAD = do_PUT = handle_any

    return H


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=0)
    ap.add_argument("--modes", default="")
    a = ap.parse_args()
    srv = ThreadingHTTPServer(("127.0.0.1", a.port), None)
    st = State(a.modes.split(","), srv.server_address[1])
    srv.RequestHandlerClass = make_handler(st)
    if "tls" in st.modes:
        import os
        import ssl
        import subprocess
        import tempfile
        d = tempfile.mkdtemp(prefix="mock-tls-")
        k, c = os.path.join(d, "k.pem"), os.path.join(d, "c.pem")
        subprocess.check_call(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", k, "-out", c,
                               "-days", "2", "-subj", "/CN=localhost"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        ctx.load_cert_chain(c, k)
        srv.socket = ctx.wrap_socket(srv.socket, server_side=True)
    print("PORT=%d" % srv.server_address[1], flush=True)
    if "redirect" in st.modes:
        class R(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *a):
                pass

            def go(self):
                n = int(self.headers.get("Content-Length") or 0)
                if n:
                    self.rfile.read(n)
                self.send_response(301)
                self.send_header("Location", st.base + self.path)
                self.send_header("Content-Length", "0")
                self.end_headers()

            do_GET = do_POST = do_DELETE = do_HEAD = do_PUT = go
        front = ThreadingHTTPServer(("127.0.0.1", 0), R)
        threading.Thread(target=front.serve_forever, daemon=True).start()
        print("FRONT=%d" % front.server_address[1], flush=True)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
