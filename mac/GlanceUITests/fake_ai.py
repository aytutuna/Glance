#!/usr/bin/env python3
"""OpenAI-compatible stand-in for the UI tests: usage fake_ai.py PORT LOGFILE"""
import base64
import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

port, logfile = int(sys.argv[1]), sys.argv[2]
seen = []


def image_size(messages):
    for m in messages:
        if m["role"] == "user" and isinstance(m["content"], list):
            for part in m["content"]:
                if part.get("type") == "image_url":
                    url = part["image_url"]["url"]
                    return len(base64.b64decode(url.split(",", 1)[1]))
    return 0


def text_of(message):
    c = message["content"]
    if isinstance(c, list):
        return " ".join(p.get("text", "") for p in c if p.get("type") == "text")
    return c


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply_json(self, value):
        body = json.dumps(value).encode()
        self.send_response(200)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        # the tests read back what the app sent
        self.reply_json(seen if self.path == "/requests" else {"data": [{"id": "fake-model"}]})

    def do_POST(self):
        if self.path == "/reset":
            seen.clear()
            return self.reply_json([])
        data = json.loads(self.rfile.read(int(self.headers["content-length"])))
        messages = data["messages"]
        users = [m for m in messages if m["role"] == "user"]
        size = image_size(messages)
        if len(users) == 1:
            answer = f"Answer one. Image {size} bytes."
        else:
            answer = f"Follow-up {len(users) - 1}: {text_of(users[-1])}"
        entry = {"roles": [m["role"] for m in messages], "image": size, "last": text_of(users[-1]), "reply": answer}
        seen.append(entry)
        with open(logfile, "a") as f:
            f.write(json.dumps(entry) + "\n")
        self.send_response(200)
        self.send_header("content-type", "text/event-stream")
        self.end_headers()
        words = answer.split(" ")
        for i, w in enumerate(words):
            chunk = {"choices": [{"delta": {"content": w + (" " if i < len(words) - 1 else "")}}]}
            self.wfile.write(f"data: {json.dumps(chunk)}\n\n".encode())
            self.wfile.flush()
            time.sleep(0.05)
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()


ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
