#!/usr/bin/env python3
"""Simple EZTV API proxy for the TV. Run on your Mac, point the TV at http://<mac-ip>:8098/api/get-torrents"""
import http.server
import urllib.request
import sys

EZTV_BASE = "https://eztvx.to"
PORT = 8098

class Proxy(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        url = EZTV_BASE + self.path
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                body = r.read()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.send_header("Access-Control-Allow-Origin", "*")
                self.end_headers()
                self.wfile.write(body)
        except Exception as e:
            msg = str(e).encode()
            self.send_response(502)
            self.send_header("Content-Type", "text/plain")
            self.send_header("Content-Length", str(len(msg)))
            self.end_headers()
            self.wfile.write(msg)

    def log_message(self, fmt, *args):
        print(f"[proxy] {args[0]}")

print(f"EZTV proxy on http://0.0.0.0:{PORT}/")
print(f"Enter this in the TV app: http://192.168.8.139:{PORT}/api/get-torrents")
http.server.HTTPServer(("0.0.0.0", PORT), Proxy).serve_forever()
