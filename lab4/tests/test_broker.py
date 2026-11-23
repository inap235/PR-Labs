#!/usr/bin/env python3
"""Automatic tests of the message broker of lab 4 (Python 3.8 or newer, standard library only).

Every test starts a real broker on this computer, with its own port, data folder and log, and real
publishers and subscribers: the C# programs (typed into through their standard input), and for
Part 1 also raw HTTP requests from Python, to send what the programs never send.
Part 2 has no HTTP API, so its tests use only the C# gRPC programs.

From the repository root, after building both projects:
    dotnet build Labs.csproj -c Release
    dotnet build lab4/grpc/BrokerGrpc.csproj -c Release
    python lab4/tests/test_broker.py              all tests
    python lab4/tests/test_broker.py http         the tests whose names contain these words
    python lab4/tests/test_broker.py measure      the measurements used in the report
"""

import http.client
import json
import os
import shutil
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WINDOWS = os.name == "nt"
HTTP_EXE = ROOT / "bin" / "Release" / "net9.0" / ("Labs.exe" if WINDOWS else "Labs")
GRPC_EXE = ROOT / "lab4" / "grpc" / "bin" / "Release" / "net9.0" / ("BrokerGrpc.exe" if WINDOWS else "BrokerGrpc")
WORK = ROOT / "lab4" / "data" / "tests"
TOKENS = ROOT / "lab4" / "tokens.json"
RESULTS = ROOT / "lab4" / "results"

TEACHER, ANA, BENCH = "teacher-5f2c9a71d3", "ana-8b41e07c66", "bench-0c7e44f913"

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


class TestFailed(Exception):
    pass


def check(condition, message):
    if not condition:
        raise TestFailed(message)


# ----------------------------------------------------------------------------------- programs

RUNNING = []  # every process started by the current test, stopped at its end


class Program:
    """One C# program (broker, publisher or subscriber); its output is collected line by line."""

    def __init__(self, exe, *args, name="program"):
        self.name = name
        command = [str(exe), *args] if exe == GRPC_EXE else [str(exe), "lab4", *args]
        self.process = subprocess.Popen(command, cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
        self.lines = []
        self.changed = threading.Condition()
        threading.Thread(target=self._read, daemon=True).start()
        RUNNING.append(self)

    def _read(self):
        for line in self.process.stdout:
            with self.changed:
                self.lines.append(line.rstrip("\r\n"))
                self.changed.notify_all()

    def type(self, line):
        self.process.stdin.write(line + "\n")
        self.process.stdin.flush()

    def wait_for(self, text, timeout=10.0, count=1):
        deadline = time.monotonic() + timeout
        with self.changed:
            while True:
                found = [line for line in self.lines if text in line]
                if len(found) >= count:
                    return found[count - 1]
                left = deadline - time.monotonic()
                if left <= 0:
                    raise TestFailed(f"{self.name} did not print '{text}' (x{count}) within {timeout} s. It printed:\n"
                                     + "\n".join("    | " + line for line in self.lines[-40:]))
                self.changed.wait(left)

    def output(self):
        with self.changed:
            return list(self.lines)

    def kill(self):
        """A hard kill (TerminateProcess on Windows): no chance to clean up, like a crash."""
        if self.process.poll() is None:
            self.process.kill()
            self.process.wait(10)

    def finish(self, timeout=20):
        """Closes the input (end of file: the publisher stops) and waits for the program to end."""
        try:
            self.process.stdin.close()
        except OSError:
            pass
        self.process.wait(timeout)


class Broker:
    """A broker of Part 1 (http) or Part 2 (grpc) with its own port, data folder and log."""

    def __init__(self, kind, port, folder, tokens=False):
        self.kind, self.port, self.folder = kind, port, folder
        self.data, self.log = folder / "data", folder / "broker.log"
        self.exe = HTTP_EXE if kind == "http" else GRPC_EXE
        self.tokens = tokens
        self.address = f"http://localhost:{port}"
        self.program = None
        self.start()

    def start(self):
        args = ["broker", "--port", str(self.port), "--data", str(self.data), "--log", str(self.log)]
        if self.tokens:
            args += ["--tokens", str(self.tokens if isinstance(self.tokens, Path) else TOKENS)]
        self.program = Program(self.exe, *args, name=f"{self.kind} broker")
        self.program.wait_for("broker started", 20)
        wait_port(self.port)
        return self

    def kill(self):
        self.program.kill()

    def publisher(self, topic, name, token=None):
        args = ["publish", "--broker", self.address, "--topic", topic, "--name", name]
        if token:
            args += ["--token", token]
        p = Program(self.exe, *args, name=f"publisher {name}")
        p.wait_for("Publishing to")
        return p

    def subscriber(self, topic, *extra, wait=True):
        s = Program(self.exe, "subscribe", "--broker", self.address, "--topic", topic, *extra, name=f"subscriber {topic}")
        if wait:
            s.wait_for("Subscribed to")
        return s

    def run(self, *args, timeout=120):
        exe = [str(self.exe)] if self.kind == "grpc" else [str(self.exe), "lab4"]
        done = subprocess.run([*exe, *args, "--broker", self.address], cwd=ROOT, capture_output=True, text=True,
                              encoding="utf-8", errors="replace", timeout=timeout)
        return done.returncode, done.stdout + done.stderr

    def log_lines(self):
        return self.log.read_text(encoding="utf-8").splitlines() if self.log.exists() else []


def wait_port(port, timeout=10):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            with socket.create_connection(("localhost", port), timeout=1):
                return
        except OSError:
            time.sleep(0.1)
    raise TestFailed(f"nothing listens on port {port}")


def fresh_folder(name):
    folder = WORK / name
    shutil.rmtree(folder, ignore_errors=True)
    folder.mkdir(parents=True)
    return folder


# --------------------------------------------------------------------------- raw HTTP (Part 1)

def request(port, method, path, body=None, headers=None, raw=None):
    """One HTTP request; returns (status, headers, parsed JSON or text)."""
    connection = http.client.HTTPConnection("localhost", port, timeout=10)
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    h = {"Content-Type": "application/json"} if data is not None else {}
    h.update(headers or {})
    connection.request(method, path, body=data, headers=h)
    response = connection.getresponse()
    text = response.read().decode("utf-8", errors="replace")
    connection.close()
    try:
        parsed = json.loads(text) if text else None
    except ValueError:
        parsed = text
    return response.status, dict(response.getheaders()), parsed


def publish(port, topic, sender, body, token=None):
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    return request(port, "POST", f"/topics/{topic}/messages", {"sender": sender, "body": body}, headers)


class RawStream:
    """GET /topics/{topic}/stream read from Python: one JSON message per line, empty lines are heartbeats."""

    def __init__(self, port, topic, after=None):
        self.connection = http.client.HTTPConnection("localhost", port, timeout=10)
        path = f"/topics/{topic}/stream" + (f"?after={after}" if after is not None else "")
        self.connection.request("GET", path)
        self.response = self.connection.getresponse()
        self.status = self.response.status
        self.headers = dict(self.response.getheaders())

    def next(self, timeout=10):
        self.connection.sock.settimeout(timeout)
        while True:
            line = self.response.readline()
            if not line:
                raise TestFailed("the stream ended")
            line = line.strip()
            if line:
                return json.loads(line)

    def close(self):
        self.connection.close()


# ------------------------------------------------------------------------------ Part 1: HTTP

def test_http_publish_and_deliver():
    """Task 1: two publishers and two subscribers; ids 1, 2, 3, ... and the format of the subscriber."""
    broker = Broker("http", 18101, fresh_folder("http_publish"))
    s1, s2 = broker.subscriber("grades"), broker.subscriber("grades")
    news = broker.subscriber("news")
    teacher, ana = broker.publisher("grades", "teacher"), broker.publisher("grades", "Ana")
    teacher.type("Lab 3 grades are online")
    teacher.wait_for("-> #1")
    ana.type("Thank you! ăîșțâ")
    ana.wait_for("-> #2")
    for s in (s1, s2):
        s.wait_for("[grades] teacher: Lab 3 grades are online")
        s.wait_for("[grades] Ana: Thank you! ăîșțâ")
    time.sleep(0.5)
    check(not any("[grades]" in line for line in news.output()), "a subscriber of news got a message of grades")
    status, _, answer = request(18101, "GET", "/topics")
    check(status == 200, f"GET /topics answered {status}")
    row = next(t for t in answer["topics"] if t["name"] == "grades")
    check(row["size"] == 2 and row["lastId"] == 2 and row["subscribers"] == 2, f"wrong row for grades: {row}")


def test_http_api():
    """The answers of Table 2, and errors for every request the broker cannot accept (it stays up, and logs them)."""
    broker = Broker("http", 18102, fresh_folder("http_api"))
    status, _, m = publish(18102, "Orders", "shop", "first")
    check(status == 202, f"publish answered {status}")
    check(m["id"] == 1 and m["topic"] == "orders" and m["sender"] == "shop" and m["body"] == "first" and "time" in m,
          f"the answer is not the message: {m}")
    check(publish(18102, "orders", "shop", "second")[2]["id"] == 2, "the second id is not 2")

    bad = [
        ("POST", "/topics/orders/messages", None, b"not json", 400),
        ("POST", "/topics/orders/messages", None, b"[1, 2]", 400),
        ("POST", "/topics/orders/messages", {"sender": "x"}, None, 400),
        ("POST", "/topics/orders/messages", {"sender": "", "body": "b"}, None, 400),
        ("POST", "/topics/bad%20topic!/messages", {"sender": "x", "body": "b"}, None, 400),
        ("POST", "/topics/orders/messages", None, b'{"sender":"x","body":"' + b"a" * 70000 + b'"}', 413),
        ("GET", "/topics/orders/messages", None, None, 405),
        ("DELETE", "/topics", None, None, 405),
        ("GET", "/nothing", None, None, 404),
        ("GET", "/topics/orders/stream?after=-3", None, None, 400),
    ]
    for method, path, body, raw, expected in bad:
        status, headers, answer = request(18102, method, path, body, raw=raw)
        check(status == expected, f"{method} {path} answered {status}, expected {expected}")
        check(isinstance(answer, dict) and "error" in answer, f"{method} {path}: no JSON error in {answer!r}")
        if expected == 405:
            check("Allow" in headers, f"{method} {path}: 405 without Allow")
    check(publish(18102, "orders", "shop", "third")[2]["id"] == 3, "the broker is not well after the bad requests")
    log = "\n".join(broker.log_lines())
    for text in ("400 Bad Request", "404 Not Found", "405 Method Not Allowed", "413", "202 Accepted"):
        check(text in log, f"the log has no line with '{text}'")


def test_http_concurrent_publishers():
    """Many publishers at the same time: no message lost, no id given twice, the subscriber gets all, in order."""
    broker = Broker("http", 18103, fresh_folder("http_concurrent"))
    code, out = broker.run("bench", "--messages", "600", "--publishers", "8")
    result = bench_result(out)
    check(code == 0 and result["idsUniqueAndContiguous"] and result["receivedOnceInOrder"] and result["errors"] == 0,
          f"bench failed:\n{out}")


def bench_result(out):
    line = next((l for l in out.splitlines() if l.startswith("BENCH ")), None)
    check(line is not None, f"the bench printed no result:\n{out}")
    return json.loads(line[6:])


def test_http_gone_subscriber():
    """A subscriber that is gone does not stop the delivery to the others; the broker forgets it."""
    broker = Broker("http", 18104, fresh_folder("http_gone"))
    stays = broker.subscriber("news")
    goes = broker.subscriber("news")
    raw = RawStream(18104, "news")
    raw.close()  # a subscriber that leaves without a word
    goes.kill()  # and one that crashes
    time.sleep(0.5)
    for i in range(1, 4):
        publish(18104, "news", "ana", f"after the crash {i}")
    for i in range(1, 4):
        stays.wait_for(f"[news] ana: after the crash {i}")
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        status, _, answer = request(18104, "GET", "/topics")
        if next(t for t in answer["topics"] if t["name"] == "news")["subscribers"] == 1:
            break
        time.sleep(0.3)
    else:
        raise TestFailed(f"the broker did not forget the gone subscribers: {answer}")
    check(any("forgotten" in line for line in broker.log_lines()), "no log line about a forgotten subscriber")


def test_http_restart():
    """Task 2: a broker killed and started again keeps its messages, the ids go on, the clients come back."""
    broker = Broker("http", 18105, fresh_folder("http_restart"))
    sub = broker.subscriber("grades")
    pub = broker.publisher("grades", "teacher")
    for i in range(1, 4):
        pub.type(f"before {i}")
        pub.wait_for(f"-> #{i}")
    sub.wait_for("[grades] teacher: before 3")

    broker.kill()
    sub.wait_for("connection lost")
    pub.type("while the broker is down")  # the publisher tries again until the broker is back
    pub.wait_for("not reachable")
    time.sleep(1)
    broker.start()
    pub.wait_for("-> #4", 30)
    pub.type("after the restart")
    pub.wait_for("-> #5")
    sub.wait_for("connected again", 30)
    sub.wait_for("[grades] teacher: while the broker is down", 15)
    sub.wait_for("[grades] teacher: after the restart")
    shown = [l for l in sub.output() if l.startswith("[grades]")]
    check(len(shown) == len(set(shown)) == 5, f"the subscriber showed a message twice or missed one: {shown}")

    status, _, answer = request(18105, "GET", "/topics")
    check(answer["topics"][0]["size"] == 5, f"wrong size after the restart: {answer}")
    stream = RawStream(18105, "grades", after=2)  # catch up: the stored messages after id 2
    check([stream.next()["id"] for _ in range(3)] == [3, 4, 5], "?after=2 did not give 3, 4, 5")
    stream.close()
    stored = json.loads((broker.data / "grades.json").read_text(encoding="utf-8"))
    check([m["id"] for m in stored] == [1, 2, 3, 4, 5], f"the file has {[m['id'] for m in stored]}")


def test_http_half_written_file():
    """Task 2: a broker killed in the middle of a write leaves <topic>.json.tmp next to the whole old file."""
    folder = fresh_folder("http_tmp")
    broker = Broker("http", 18106, folder)
    publish(18106, "news", "ana", "one")
    publish(18106, "news", "ana", "two")
    broker.kill()
    (broker.data / "news.json.tmp").write_text('[{"id": 1, "topic": "ne', encoding="utf-8")  # cut in the middle
    broker.start()
    check(not (broker.data / "news.json.tmp").exists(), "the .tmp file is still there")
    check(publish(18106, "news", "ana", "three")[2]["id"] == 3, "the ids did not go on after the restart")


def test_http_many_restarts_under_load():
    """Task 2: kill the broker again and again while a publisher sends; every acknowledged message is still there."""
    broker = Broker("http", 18107, fresh_folder("http_kill_loop"))
    acknowledged = []
    for round_ in range(5):
        stop = time.monotonic() + 0.7
        while time.monotonic() < stop:
            status, _, m = publish(18107, "load", "p", f"round {round_}")
            if status == 202:
                acknowledged.append(m["id"])
        broker.kill()
        broker.start()
    status, _, answer = request(18107, "GET", "/topics")
    size = next(t for t in answer["topics"] if t["name"] == "load")["size"]
    check(acknowledged == list(range(1, len(acknowledged) + 1)), "the acknowledged ids have gaps")
    check(size == len(acknowledged), f"{len(acknowledged)} messages acknowledged, {size} after the restarts")


def test_http_tokens():
    """Task 3: 401 with WWW-Authenticate: Bearer without a token or with an unknown one, 403 for a topic not allowed."""
    folder = fresh_folder("http_tokens")
    tokens = folder / "tokens.json"
    shutil.copy(TOKENS, tokens)
    broker = Broker("http", 18108, folder, tokens=tokens)

    status, headers, _ = publish(18108, "grades", "x", "no token")
    check(status == 401 and headers.get("WWW-Authenticate") == "Bearer", f"no token: {status} {headers}")
    status, headers, _ = publish(18108, "grades", "x", "unknown", token="not-a-token")
    check(status == 401 and headers.get("WWW-Authenticate") == "Bearer", f"unknown token: {status}")
    status, _, _ = request(18108, "POST", "/topics/grades/messages", {"sender": "x", "body": "b"},
                           {"Authorization": f"Basic {ANA}"})
    check(status == 401, f"another scheme than Bearer: {status}")
    status, _, _ = publish(18108, "grades", "Ana", "not my topic", token=ANA)
    check(status == 403, f"good token, topic not allowed: {status}")
    status, _, m = publish(18108, "grades", "somebody else", "hello", token=TEACHER)
    check(status == 202 and m["sender"] == "teacher", f"good token: {status} {m}")
    check(request(18108, "GET", "/topics")[0] == 200, "reading needs no token")

    # the C# publisher with a token, and without
    good = broker.publisher("grades", "teacher", token=TEACHER)
    good.type("from the program")
    good.wait_for("-> #2")
    bad = broker.publisher("grades", "Ana", token=ANA)
    bad.type("not allowed")
    bad.wait_for("403 Forbidden")

    # a publisher added while the broker runs
    content = json.loads(tokens.read_text(encoding="utf-8"))
    content["publishers"].append({"name": "Maria", "token": "maria-new-token", "topics": ["grades"]})
    time.sleep(1.1)  # a newer modification time
    tokens.write_text(json.dumps(content), encoding="utf-8")
    status, _, m = publish(18108, "grades", "Maria", "I am new", token="maria-new-token")
    check(status == 202 and m["sender"] == "Maria", f"the new token was not read: {status}")
    log = "\n".join(broker.log_lines())
    check("401 Unauthorized" in log and "403 Forbidden" in log, "the rejected publishes are not in the log")


# ------------------------------------------------------------------------------ Part 2: gRPC

def test_grpc_publish_and_deliver():
    """Task 1 over gRPC: two publishers, two subscribers, the same format; ListTopics."""
    broker = Broker("grpc", 15101, fresh_folder("grpc_publish"))
    s1, s2 = broker.subscriber("grades"), broker.subscriber("grades")
    teacher, ana = broker.publisher("grades", "teacher"), broker.publisher("grades", "Ana")
    teacher.type("Lab 3 grades are online")
    teacher.wait_for("-> #1")
    ana.type("Thank you! ăîșțâ")
    ana.wait_for("-> #2")
    for s in (s1, s2):
        s.wait_for("[grades] teacher: Lab 3 grades are online")
        s.wait_for("[grades] Ana: Thank you! ăîșțâ")
    code, out = broker.run("topics")
    check(code == 0 and any(l.split()[:4] == ["grades", "2", "2", "2"] for l in out.splitlines()), f"topics:\n{out}")
    code, out = broker.run("subscribe", "--topic", "-bad")
    check(code != 0, "a bad topic name was accepted")


def test_grpc_concurrent_publishers():
    broker = Broker("grpc", 15102, fresh_folder("grpc_concurrent"))
    code, out = broker.run("bench", "--messages", "600", "--publishers", "8")
    result = bench_result(out)
    check(code == 0 and result["idsUniqueAndContiguous"] and result["receivedOnceInOrder"] and result["errors"] == 0,
          f"bench failed:\n{out}")


def test_grpc_gone_subscriber():
    broker = Broker("grpc", 15103, fresh_folder("grpc_gone"))
    stays, goes = broker.subscriber("news"), broker.subscriber("news")
    goes.kill()
    time.sleep(0.5)
    pub = broker.publisher("news", "ana")
    for i in range(1, 4):
        pub.type(f"after the crash {i}")
    for i in range(1, 4):
        stays.wait_for(f"[news] ana: after the crash {i}")
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        code, out = broker.run("topics")
        if any(l.split()[:1] == ["news"] and l.split()[3] == "1" for l in out.splitlines()):
            break
        time.sleep(0.3)
    else:
        raise TestFailed(f"the broker did not forget the gone subscriber:\n{out}")
    check(any("forgotten" in line for line in broker.log_lines()), "no log line about a forgotten subscriber")


def test_grpc_restart():
    broker = Broker("grpc", 15104, fresh_folder("grpc_restart"))
    sub = broker.subscriber("grades")
    pub = broker.publisher("grades", "teacher")
    for i in range(1, 4):
        pub.type(f"before {i}")
        pub.wait_for(f"-> #{i}")
    sub.wait_for("[grades] teacher: before 3")
    broker.kill()
    sub.wait_for("connection lost")
    pub.type("while the broker is down")
    pub.wait_for("not reachable")
    time.sleep(1)
    broker.start()
    pub.wait_for("-> #4", 30)
    pub.type("after the restart")
    pub.wait_for("-> #5")
    sub.wait_for("connected again", 30)
    sub.wait_for("[grades] teacher: while the broker is down", 15)
    sub.wait_for("[grades] teacher: after the restart")
    shown = [l for l in sub.output() if l.startswith("[grades]")]
    check(len(shown) == len(set(shown)) == 5, f"the subscriber showed a message twice or missed one: {shown}")
    late = broker.subscriber("grades", "--after", "2", "--ids", wait=False)
    late.wait_for("#5 ")
    check([l.split()[0] for l in late.output() if l.startswith("#")] == ["#3", "#4", "#5"], "--after 2 did not give 3, 4, 5")


def test_grpc_tokens():
    broker = Broker("grpc", 15105, fresh_folder("grpc_tokens"), tokens=True)
    none = broker.publisher("grades", "x")
    none.type("no token")
    none.wait_for("Unauthenticated")
    unknown = broker.publisher("grades", "x", token="not-a-token")
    unknown.type("unknown token")
    unknown.wait_for("Unauthenticated")
    ana = broker.publisher("grades", "Ana", token=ANA)
    ana.type("not my topic")
    ana.wait_for("PermissionDenied")
    sub = broker.subscriber("grades")
    teacher = broker.publisher("grades", "somebody else", token=TEACHER)
    teacher.type("hello")
    teacher.wait_for("-> #1")
    sub.wait_for("[grades] teacher: hello")
    log = "\n".join(broker.log_lines())
    check("Unauthenticated" in log and "PermissionDenied" in log, "the rejected publishes are not in the log")


def test_grpc_has_no_http_api():
    """In Part 2 the broker speaks only gRPC: an HTTP/1.1 request gets no answer of the API."""
    Broker("grpc", 15106, fresh_folder("grpc_no_http"))
    try:
        with urllib.request.urlopen("http://localhost:15106/topics", timeout=5) as response:
            check(response.status != 200, "GET /topics answered 200 on the gRPC broker")
    except (urllib.error.URLError, http.client.HTTPException, ConnectionError, socket.timeout, OSError):
        pass


# ------------------------------------------------------------------------------- measurements

def measure():
    """The benches of the report: both parts, 1 to 16 publishers at the same time, one topic."""
    RESULTS.mkdir(parents=True, exist_ok=True)
    rows = []
    for kind, port in (("http", 18190), ("grpc", 15190)):
        for publishers in (1, 2, 4, 8, 16):
            for repeat in range(3):
                broker = Broker(kind, port, fresh_folder(f"measure_{kind}_{publishers}_{repeat}"))
                code, out = broker.run("bench", "--messages", "1000", "--publishers", str(publishers))
                result = bench_result(out)
                result["repeat"] = repeat
                rows.append(result)
                print(f"  {kind:4} publishers={publishers:2} repeat={repeat}: {result['publishesPerSecond']:8} per s, "
                      f"publish p50 {result['publishMs']['p50']} ms, delivery p50 {result['deliveryMs']['p50']} ms")
                broker.kill()
                stop_all()
    (RESULTS / "measurements.json").write_text(json.dumps(rows, indent=1), encoding="utf-8")
    print(f"written {RESULTS / 'measurements.json'}")


# ------------------------------------------------------------------------------------- runner

def stop_all():
    for program in RUNNING:
        program.kill()
    RUNNING.clear()


def main():
    for exe in (HTTP_EXE, GRPC_EXE):
        if not exe.exists():
            print(f"{exe} does not exist: build both projects first (see the top of this file)")
            return 2
    words = sys.argv[1:]
    if words == ["measure"]:
        try:
            measure()
        finally:
            stop_all()
        return 0

    tests = [(name, f) for name, f in globals().items() if name.startswith("test_") and callable(f)]
    tests = [(n, f) for n, f in tests if not words or any(w in n for w in words)]
    failed = 0
    for name, test in tests:
        start = time.monotonic()
        try:
            test()
            print(f"PASS  {name}  ({time.monotonic() - start:.1f} s)")
        except Exception as e:  # a failed check, or a crash of the test itself
            failed += 1
            print(f"FAIL  {name}  ({time.monotonic() - start:.1f} s)\n      {type(e).__name__}: {e}")
        finally:
            stop_all()
    print(f"\n{len(tests) - failed} of {len(tests)} tests passed")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
