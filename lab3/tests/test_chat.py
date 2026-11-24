#!/usr/bin/env python3
"""Automatic tests of the LAN chat of lab 3 (Python 3.8 or newer, standard library only).

Every test starts real peers on this computer (the C# chat, and the Java peer for the bonus
tests), types into them through their standard input, and checks what they print and what their
log files say. Some tests also use a RawPeer: Python sockets that send what the chat itself never
sends (random bytes, changed datagrams), or that listen on the port of the chat like a stranger.
Every test uses its own port, so peers left over from one test cannot disturb another.

From the repository root, after building:
    dotnet build Labs.csproj -c Release
    javac -d lab3/chat-java/out lab3/chat-java/src/lanchat/*.java     (for the java tests)
    python lab3/tests/test_chat.py                 all tests
    python lab3/tests/test_chat.py private java    the tests whose names contain these words
    python lab3/tests/test_chat.py measure         the measurements used in the report
"""

import base64
import json
import os
import random
import re
import shutil
import socket
import statistics
import subprocess
import sys
import threading
import time
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EXE = ROOT / "bin" / "Release" / "net9.0" / ("Labs.exe" if os.name == "nt" else "Labs")
JAVA_OUT = ROOT / "lab3" / "chat-java" / "out"
LOGS = ROOT / "lab3" / "logs" / "tests"
GROUP = "239.255.42.42"
PROTO = "lanchat/1"

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


class TestFailed(Exception):
    pass


def check(condition, message):
    if not condition:
        raise TestFailed(message)


def default_interface():
    """The address the chat chooses by default, from 'lab3 networks' (the line marked with *)."""
    out = subprocess.run([str(EXE), "lab3", "networks"], capture_output=True, text=True, encoding="utf-8").stdout
    for line in out.splitlines():
        if line.strip().startswith("*"):
            return line.split()[1]
    return "127.0.0.1"


# ------------------------------------------------------------------------------------- peers

RUNNING = []  # every process started by the current test, stopped at its end


class Peer:
    """One chat program (C# or Java), typed into through its standard input."""

    counter = 0

    def __init__(self, name, port, *options, java=False, env=None):
        Peer.counter += 1
        self.name = name
        self.log_path = LOGS / f"{name}-{port}-{Peer.counter}.log"
        common = ["--name", name, "--port", str(port), "--log", str(self.log_path), *options]
        if java:
            command = ["java", "-cp", str(JAVA_OUT), "lanchat.LanChat", *common]
        else:
            command = [str(EXE), "lab3", "chat", *common]
        self.started = time.monotonic()
        self.process = subprocess.Popen(command, cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
                                        env=None if env is None else {**os.environ, **env})
        self.lines = []  # (time.monotonic(), line)
        self.changed = threading.Condition()
        threading.Thread(target=self._read, daemon=True).start()
        RUNNING.append(self)

    def _read(self):
        for line in self.process.stdout:
            with self.changed:
                self.lines.append((time.monotonic(), line.rstrip("\r\n")))
                self.changed.notify_all()

    def type(self, line):
        self.process.stdin.write(line + "\n")
        self.process.stdin.flush()

    def wait_for(self, text, timeout=3.0, count=1):
        """The time of the count-th printed line that contains text; fails after timeout seconds."""
        deadline = time.monotonic() + timeout
        with self.changed:
            while True:
                found = [t for t, line in self.lines if text in line]
                if len(found) >= count:
                    return found[count - 1]
                left = deadline - time.monotonic()
                if left <= 0:
                    raise TestFailed(f"{self.name} did not print '{text}' within {timeout} s. It printed:\n"
                                     + "\n".join("    | " + line for _, line in self.lines))
                self.changed.wait(left)

    def joined(self, timeout=5.0):
        self.wait_for("You joined the chat", timeout)
        return self

    def count(self, text):
        with self.changed:
            return sum(text in line for _, line in self.lines)

    def output(self):
        with self.changed:
            return [line for _, line in self.lines]

    def endpoint(self):
        """Address and port of the peer's own socket, from 'This peer: 192.168.1.5:53812.'"""
        self.wait_for("This peer:")
        for line in self.output():
            match = re.search(r"This peer: ([\d.]+):(\d+)", line)
            if match:
                return match.group(1), int(match.group(2))
        raise TestFailed(f"{self.name} did not print its endpoint")

    def log(self):
        return self.log_path.read_text(encoding="utf-8") if self.log_path.exists() else ""

    def log_lines(self, what, kind=None):
        """The log lines of one kind of event: sent, recv or drop."""
        result = []
        for line in self.log().splitlines():
            parts = line.split()
            if len(parts) >= 4 and parts[1] == what and (kind is None or parts[3] == kind):
                result.append(line)
        return result

    def exit_code(self, timeout=5.0):
        try:
            return self.process.wait(timeout)
        except subprocess.TimeoutExpired:
            return None

    def quit(self):
        self.type("/quit")
        return self.exit_code()

    def kill(self):
        self.process.kill()
        self.process.wait()

    def stop(self):
        if self.process.poll() is None:
            self.process.kill()
            self.process.wait()


class RawPeer:
    """Python sockets that act like a peer, or like a stranger, on the network of the chat.
    'shared' is bound to the port of the chat (with SO_REUSEADDR, like every peer) and joined to
    the group; 'own' is bound to a free port and sends everything."""

    def __init__(self, port, name="Eve"):
        self.port = port
        self.name = name
        self.id = os.urandom(8).hex()
        self.own = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.own.bind(("0.0.0.0", 0))
        self.own.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(IFACE))
        # macOS refuses to send a datagram larger than the send buffer (9 KB by default)
        self.own.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 65536)
        self.shared = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.shared.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        if hasattr(socket, "SO_REUSEPORT"):  # macOS, Linux: needed to share the port with the chat
            self.shared.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
        self.shared.bind(("0.0.0.0", port))
        self.shared.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP,
                               socket.inet_aton(GROUP) + socket.inet_aton(IFACE))
        self.received = {"own": [], "shared": []}  # (data, (address, port))
        self.changed = threading.Condition()
        self.closed = False
        for where, sock in (("own", self.own), ("shared", self.shared)):
            sock.settimeout(0.2)
            threading.Thread(target=self._read, args=(where, sock), daemon=True).start()

    def _read(self, where, sock):
        while not self.closed:
            try:
                data, source = sock.recvfrom(65536)
            except socket.timeout:
                continue
            except OSError:
                if self.closed:
                    return
                continue  # Windows: ICMP port unreachable for an earlier datagram
            with self.changed:
                self.received[where].append((data, source))
                self.changed.notify_all()

    def message(self, kind, **fields):
        return json.dumps({"proto": PROTO, "type": kind, "from": self.name, "id": self.id, **fields}).encode()

    def send(self, data, to):
        self.own.sendto(data, to)

    def to_group(self, data):
        self.own.sendto(data, (GROUP, self.port))

    def join(self):
        """Joins like a peer, so the chat lets private messages be sent to it."""
        self.to_group(self.message("join"))

    def wait(self, where, predicate, timeout=3.0):
        deadline = time.monotonic() + timeout
        with self.changed:
            while True:
                for data, source in self.received[where]:
                    if predicate(data):
                        return data, source
                left = deadline - time.monotonic()
                if left <= 0:
                    return None
                self.changed.wait(left)

    def close(self):
        self.closed = True
        self.own.close()
        self.shared.close()


def kind_of(data):
    try:
        return json.loads(data.decode()).get("type")
    except (ValueError, UnicodeDecodeError, AttributeError):
        return None


# ------------------------------------------------------------------------------------- tests

def test_general_channel(port):
    """Task 1: one datagram for everybody; the author sees its message once."""
    ana = Peer("Ana", port).joined()
    ion = Peer("Ion", port).joined()
    maria = Peer("Maria", port).joined()
    ana.type("hello everyone")
    for peer in (ana, ion, maria):
        peer.wait_for("[general] Ana: hello everyone")
    maria.type("hi Ana")
    for peer in (ana, ion, maria):
        peer.wait_for("[general] Maria: hi Ana")
    time.sleep(1.0)  # a second copy would have arrived by now
    for peer in (ana, ion, maria):
        check(peer.count("[general] Ana: hello everyone") == 1, f"{peer.name} showed Ana's message more than once")
    sent = ana.log_lines("sent", "chat")
    check(len(sent) == 1 and f"{GROUP}:{port}" in sent[0], f"Ana should send one chat datagram to the group: {sent}")
    check(any("own datagram" in line for line in ana.log_lines("recv", "chat")), "Ana's log should show her own message coming back")


def test_join_and_users(port):
    """Task 2: a new peer knows who is online within a second; /users lists them with endpoints."""
    ana = Peer("Ana", port).joined()
    ion = Peer("Ion", port).joined()
    maria = Peer("Maria", port)
    maria.wait_for("You joined the chat as Maria. Online: Ana, Ion.")
    ana.wait_for("* Maria joined the chat")
    ion.wait_for("* Maria joined the chat")
    hello = maria.log_lines("sent", "hello")
    heres = maria.log_lines("recv", "here")
    check(hello and len(heres) == 2, f"Maria should get two answers to her hello: {heres}")
    delay = max(seconds_between(hello[0], line) for line in heres)
    check(delay < 1.0, f"answers to hello took {delay:.3f} s")
    maria.type("/users")
    maria.wait_for("* 3 online:")
    for name, peer in (("Ana", ana), ("Ion", ion)):
        address, own_port = peer.endpoint()
        maria.wait_for(f"{address}:{own_port}")
    print(f"    answers to hello within {delay * 1000:.0f} ms")


def test_name_taken(port):
    """Task 2: a peer that starts with a name already in use is told so and does not join."""
    ana = Peer("Ana", port).joined()
    ion = Peer("Ion", port).joined()
    second = Peer("ana", port)  # names are not case sensitive
    second.wait_for("already used in this chat")
    check(second.exit_code() == 3, "the second Ana should end with exit code 3")
    time.sleep(0.5)
    check(ion.count("* ana joined the chat") == 0, "Ion should not see the second Ana join")
    check(any("taken" in line for line in ana.log_lines("sent", "taken")), "Ana should answer 'taken'")


def test_same_name_same_moment(port):
    """Task 2, question 3: two peers start with the same name at the same moment; exactly one joins."""
    ana = Peer("Ana", port).joined()
    for round_number in range(3):
        name = f"Bob{round_number}"
        first, second = Peer(name, port), Peer(name, port)
        codes = []
        for peer in (first, second):
            deadline = time.monotonic() + 5
            while time.monotonic() < deadline and not (peer.count("You joined") or peer.count("Start again")):
                time.sleep(0.05)
            codes.append(bool(peer.count("You joined")))
        check(codes.count(True) == 1, f"round {round_number}: exactly one {name} should join, got {codes}")
        time.sleep(0.5)
        check(ana.count(f"* {name} joined the chat") == 1, f"Ana should see {name} join once")
        loser = second if codes[0] else first
        print(f"    round {round_number}: the loser printed: "
              + next(line for line in loser.output() if "Start again" in line))


def test_bye(port):
    """Task 2: a peer that leaves with /quit says goodbye; the others show it at once."""
    ana = Peer("Ana", port).joined()
    ion = Peer("Ion", port).joined()
    quit_time = time.monotonic()
    check(ion.quit() == 0, "Ion should end with exit code 0")
    seen = ana.wait_for("* Ion left the chat", 2)
    check("not heard" not in ana.output()[-1], "the goodbye should be shown, not a timeout")
    print(f"    Ana showed it {(seen - quit_time) * 1000:.0f} ms after /quit")


def test_killed_peer(port):
    """Task 2: a killed peer is noticed after 15 s without any datagram from it."""
    ana = Peer("Ana", port).joined()
    ion = Peer("Ion", port).joined()
    time.sleep(random.uniform(0, 5))  # kill it anywhere between two alives
    killed = time.monotonic()
    ion.kill()
    seen = ana.wait_for("* Ion left the chat (not heard from for 15 s)", 18)
    delay = seen - killed
    check(9.5 < delay < 16.5, f"noticed {delay:.1f} s after the kill, expected 10 to 15 s")
    print(f"    noticed {delay:.1f} s after the kill")


def test_restarted_peer(port):
    """A peer killed and started again at once replaces its old self (it gets a new endpoint)."""
    ana = Peer("Ana", port).joined()
    ion = Peer("Ion", port).joined()
    old = ion.endpoint()
    ion.kill()
    ion2 = Peer("Ion", port).joined()
    ana.wait_for("* Ion joined the chat", count=2)
    ana.type("/msg Ion are you back?")
    ion2.wait_for("[private] Ana: are you back?")
    check(ion2.endpoint() != old, "the new Ion should have another endpoint")


def test_private_message(port):
    """Task 3: a private message reaches only its receiver, even on the same computer."""
    stranger = RawPeer(port)  # listens on the shared port of the chat, like every peer of this computer
    try:
        ana = Peer("Ana", port).joined()
        ion = Peer("Ion", port).joined()
        maria = Peer("Maria", port).joined()
        ana.type("/msg Ion see you at five")
        ana.wait_for("[private to Ion] Ana: see you at five")
        ion.wait_for("[private] Ana: see you at five")
        time.sleep(1.0)
        check(maria.count("see you at five") == 0, "Maria must not get Ion's private message")
        check(not any(kind_of(d) == "private" for d, _ in stranger.received["shared"]),
              "nothing private may arrive on the shared port")
        address, own_port = ion.endpoint()
        sent = ana.log_lines("sent", "private")
        check(len(sent) == 1 and f"{address}:{own_port}" in sent[0], f"one datagram to Ion's own socket: {sent}")
        check(len(ana.log_lines("recv", "ack")) == 1, "Ana should get one ack")
        rtt = seconds_between(sent[0], ana.log_lines("recv", "ack")[0])
        ion.type("/msg ana ok")  # names are not case sensitive
        ana.wait_for("[private] Ion: ok")
        print(f"    private message acknowledged after {rtt * 1000:.1f} ms")
    finally:
        stranger.close()


def test_private_errors(port):
    """Task 3: a private message to a name that is not online gets an error; too long is refused."""
    ana = Peer("Ana", port).joined()
    ion = Peer("Ion", port).joined()
    ana.type("/msg Nobody hello")
    ana.wait_for("* Nobody is not online")
    ana.type("/msg Ion")
    ana.wait_for("* Usage: /msg <name> <text>")
    ana.type("/foo")
    ana.wait_for("* Unknown command /foo")
    ana.type("x" * 501)
    ana.wait_for("* Too long: 501 characters, at most 500. Not sent.")
    ana.type("/msg Ion " + "y" * 501)
    ana.wait_for("* Too long: 501 characters", count=2)
    ana.type("z" * 500)
    ion.wait_for("[general] Ana: " + "z" * 500)
    check(ion.count("x" * 501) == 0 and ion.count("y" * 501) == 0, "a message too long must not be sent")


def test_private_to_gone_peer(port):
    """Task 3, question 3: a private message to a peer that was killed a second ago."""
    ana = Peer("Ana", port).joined()
    ion = Peer("Ion", port).joined()
    ion.kill()
    time.sleep(1)
    ana.type("/msg Ion are you there?")
    ana.wait_for("[private to Ion]")
    ana.wait_for("Ion did not", 5)  # "did not get" (ICMP, Windows) or "did not confirm" (no ack, 3 attempts)
    print("    " + next(line for line in ana.output() if "Ion did not" in line))
    print("    " + next((line for line in ana.log().splitlines() if "ICMP" in line), "(no ICMP error on this system)"))


def test_invalid_datagrams(port):
    """Requirements: a datagram that is not a valid message is dropped and logged; no crash."""
    invalid_datagrams(port, java=False)


def test_java_invalid_datagrams(port):
    """Bonus: the Java peer drops the same invalid datagrams, for the same reasons."""
    invalid_datagrams(port, java=True)


def invalid_datagrams(port, java):
    raw = RawPeer(port, "Eve")
    try:
        ana = Peer("Ana", port, java=java).joined(8)
        ion = Peer("Ion", port).joined()
        ana_own = ana.endpoint()
        group = (GROUP, port)
        bad = [
            (group, os.urandom(300), "not UTF-8 text"),
            (group, b"hello, is anybody there?", "not JSON"),
            (group, b"[1, 2, 3]", "not a JSON object"),
            (group, b'{"type": "chat", "text": "no proto"}', f"not a {PROTO} message"),
            (group, raw.message("dance"), "unknown type"),
            (group, raw.message("chat", seq=1), "text missing"),
            (group, raw.message("chat", seq=1, text="t" * 501), "longer than 500"),
            (group, json.dumps({"proto": PROTO, "type": "chat", "from": "a b", "id": raw.id, "seq": 1, "text": "x"}).encode(), "invalid name"),
            (group, raw.message("chat", text="no seq"), "invalid 'seq'"),
            (group, b"x" * 5000, "longer than 4096 bytes"),
            (group, b"\xff" * 60000, "longer than 4096 bytes"),
            (ana_own, raw.message("private", seq=1, to="Ana"), "private message without"),
            (group, raw.message("private", seq=2, to="Ana", text="sent to the shared port"), "must come to the peer's own port"),
            (ana_own, raw.message("private", seq=3, to="Ion", text="for somebody else"), "not for Ana"),
            (ana_own, raw.message("ack", seq=999), "no private message waits"),
            (ana_own, raw.message("bye"), "not online"),
        ]
        for to, data, _ in bad:
            raw.send(data, to)
            time.sleep(0.02)
        ion.type("still alive?")
        ana.wait_for("[general] Ion: still alive?")
        time.sleep(0.5)
        check(ana.process.poll() is None, "Ana must still run")
        log = ana.log()
        for _, _, reason in bad:
            check(any(reason in line for line in log.splitlines() if " drop " in line), f"Ana's log has no drop line with '{reason}'")
        check(ana.count("sent to the shared port") == 0 and ana.count("for somebody else") == 0, "dropped messages must not be shown")
        print(f"    {len(bad)} invalid datagrams dropped, each with its reason in the log")
    finally:
        raw.close()


def test_unicode(port):
    """Names and messages in Romanian and Russian."""
    stefan = Peer("Ștefan", port).joined()
    ion = Peer("Ион", port).joined()
    stefan.type("Bună ziua! Привет! ăâîșț")
    ion.wait_for("[general] Ștefan: Bună ziua! Привет! ăâîșț")
    ion.type("/msg ștefan Mulțumesc")  # not case sensitive, also for Ș
    stefan.wait_for("[private] Ион: Mulțumesc")


def test_encrypted_on_the_wire(port):
    """Task 4: a private message is encrypted; somebody without the passphrase sees no text."""
    eve = RawPeer(port, "Eve")
    try:
        ana = Peer("Ana", port, "--passphrase", "correct horse battery").joined()
        ion = Peer("Ion", port, "--passphrase", "correct horse battery").joined()
        ana.type("/msg Ion meet me at the library")
        ion.wait_for("[private] Ana: meet me at the library")
        eve.join()  # Eve joins without the passphrase, so Ana lets her be sent a private message
        ana.wait_for("* Eve joined the chat")
        ana.type("/msg Eve the secret is 42")
        got = eve.wait("own", lambda d: kind_of(d) == "private")
        check(got is not None, "Eve should receive the datagram for her")
        data = got[0]
        message = json.loads(data)
        check("text" not in message and "nonce" in message and "data" in message, f"the datagram should be encrypted: {message}")
        check(b"secret" not in data and b"42" not in data.replace(b'"seq":', b""), "the text must not be readable")
        print(f"    on the wire: {data.decode()[:150]}...")
        ana.wait_for("Eve did not confirm", 5)  # Eve cannot answer with an ack she could make sense of
    finally:
        eve.close()


def test_wrong_passphrase(port):
    """Task 4: a peer with another passphrase cannot read the private messages."""
    ana = Peer("Ana", port, "--passphrase", "first").joined()
    mallory = Peer("Mallory", port, "--passphrase", "second").joined()
    plain = Peer("Plain", port).joined()
    ana.type("/msg Mallory can you read this?")
    ana.type("/msg Plain and this?")
    ana.type("the general channel is not encrypted")
    mallory.wait_for("[general] Ana: the general channel is not encrypted")
    plain.wait_for("[general] Ana: the general channel is not encrypted")
    ana.wait_for("Mallory did not confirm", 5)
    ana.wait_for("Plain did not confirm", 5)
    check(mallory.count("can you read this") == 0 and plain.count("and this") == 0, "nobody may read them")
    check(any("cannot be decrypted" in line for line in mallory.log_lines("drop", "private")), "Mallory should log the failure")
    check(any("has no --passphrase" in line for line in plain.log_lines("drop", "private")), "Plain should log why")
    check(len(mallory.log_lines("drop", "private")) == 3, "three attempts should arrive at Mallory")


def test_tampered_message(port):
    """Task 4, question 4: a changed datagram is detected (GCM tag); a replayed one is not."""
    eve = RawPeer(port, "Eve")
    try:
        ana = Peer("Ana", port, "--passphrase", "p4ss").joined()
        eve.join()
        ana.wait_for("* Eve joined the chat")
        ana.type("/msg Eve meet at noon")
        got = eve.wait("own", lambda d: kind_of(d) == "private")
        check(got is not None, "Eve should receive the datagram")
        original = json.loads(got[0])
        eve.to_group(eve.message("bye"))  # the stranger leaves the name Eve free again
        ana.wait_for("* Eve left the chat")

        # the real Eve starts, with the passphrase; the stranger sends her what Ana sent before
        real_eve = Peer("Eve", port, "--passphrase", "p4ss").joined()
        target = real_eve.endpoint()

        def changed(**fields):
            return json.dumps({**original, **fields}).encode()

        data = bytearray(base64.b64decode(original["data"]))
        data[0] ^= 0x01  # one bit of the ciphertext
        flipped = base64.b64encode(bytes(data)).decode()
        attacks = [
            ("one bit of the ciphertext flipped", changed(data=flipped)),
            ("another sequence number", changed(seq=original["seq"] + 100)),
            ("another nonce", changed(nonce=base64.b64encode(os.urandom(12)).decode())),
            ("plain text instead", json.dumps({**{k: v for k, v in original.items() if k not in ("nonce", "data")},
                                               "text": "forged"}).encode()),
        ]
        for _, data in attacks:
            eve.send(data, target)
            time.sleep(0.05)
        time.sleep(0.5)
        drops = real_eve.log_lines("drop", "private")
        check(len(drops) == len(attacks), f"all {len(attacks)} changed datagrams should be dropped: {drops}")
        check(real_eve.count("[private]") == 0, "no changed message may be shown")
        for (what, _), line in zip(attacks, drops):
            print(f"    {what}: {line.split('  ')[-1]}")

        # the unchanged datagram, sent again (a replay): it is authentic, so it is accepted once
        eve.send(json.dumps(original).encode(), target)
        real_eve.wait_for("[private] Ana: meet at noon")
        eve.send(json.dumps(original).encode(), target)
        time.sleep(0.5)
        check(real_eve.count("meet at noon") == 1, "a second replay should be dropped as a copy")
        print("    replayed unchanged: accepted once (a new peer with that name has never seen it), then dropped as a copy")
    finally:
        eve.close()


def test_broadcast_mode(port):
    """Control question 4: the general channel by broadcast; it also reaches the multicast peers."""
    ana = Peer("Ana", port, "--broadcast").joined()
    ion = Peer("Ion", port, "--broadcast").joined()
    maria = Peer("Maria", port).joined()
    ana.type("by broadcast")
    maria.type("by multicast")
    for peer in (ana, ion, maria):
        peer.wait_for("[general] Ana: by broadcast")
        peer.wait_for("[general] Maria: by multicast")
    sent = ana.log_lines("sent", "chat")
    check(".255:" in sent[0] or "255.255.255.255" in sent[0], f"Ana should send to a broadcast address: {sent}")
    print("    " + sent[0])
    ana.type("/msg Maria private still works")
    maria.wait_for("[private] Ana: private still works")


def test_loopback_only(port):
    """--interface 127.0.0.1: a chat of this computer only."""
    ana = Peer("Ana", port, "--interface", "127.0.0.1").joined()
    ion = Peer("Ion", port, "--interface", "127.0.0.1").joined()
    ana.wait_for("Loopback only")
    ana.type("over loopback")
    ion.wait_for("[general] Ana: over loopback")
    ion.type("/msg Ana yes")
    ana.wait_for("[private] Ion: yes")


def test_java_peer(port):
    """Bonus: the Java peer and the C# peer in one chat, with encrypted private messages."""
    if not (JAVA_OUT / "lanchat" / "LanChat.class").exists():
        raise TestFailed(f"the Java peer is not compiled in {JAVA_OUT}")
    phrase = "două limbi, un chat"  # non-ASCII: both must turn it into the same UTF-8 bytes and key
    secret = ["--passphrase", phrase]
    java_secret = {"LANCHAT_PASSPHRASE": phrase}  # Java on Windows reads argv in the ANSI code page
    ana = Peer("Ana", port, *secret).joined()
    ion = Peer("Ion", port, java=True, env=java_secret).joined(8)
    ion.wait_for("Online: Ana.")
    ana.wait_for("* Ion joined the chat")
    maria = Peer("Maria", port, *secret).joined()
    maria.wait_for("Online: Ana, Ion.")
    ion.wait_for("* Maria joined the chat")
    ana.type("salut din C#")
    ion.wait_for("[general] Ana: salut din C#")
    ion.type("salut din Java, ăîșț")
    ana.wait_for("[general] Ion: salut din Java, ăîșț")
    maria.wait_for("[general] Ion: salut din Java, ăîșț")
    ana.type("/msg Ion only for Java")
    ion.wait_for("[private] Ana: only for Java")
    ion.type("/msg Maria only for Maria, from Java")
    maria.wait_for("[private] Ion: only for Maria, from Java")
    time.sleep(1)
    check(ana.count("only for Maria") == 0, "Ana must not see it")
    check(len(ion.log_lines("recv", "ack")) == 1, "the Java peer should get Maria's ack")
    check(ana.count("did not confirm") == 0, "the Java peer should ack Ana's message")
    ion.type("/users")
    ion.wait_for("3 online")
    java_twin = Peer("ana", port, java=True, env=java_secret)
    java_twin.wait_for("already used", 8)
    check(java_twin.exit_code() == 3, "the Java twin should end with exit code 3")
    check(ion.quit() == 0, "the Java peer should end with exit code 0")
    ana.wait_for("* Ion left the chat")
    maria.wait_for("* Ion left the chat")


def seconds_between(first_line, second_line):
    def at(line):
        return datetime.strptime(line.split()[0], "%H:%M:%S.%f")
    return (at(second_line) - at(first_line)).total_seconds()


# ------------------------------------------------------------------------------------- measurements

def measure(port):
    """The numbers of the report: join time, killed-peer detection, private round trip, key derivation,
    datagram sizes. Printed, and saved to lab3/results/measurements.json for make_charts.py."""
    results = {"date": datetime.now().strftime("%Y-%m-%d %H:%M"), "interface": IFACE}

    # 1. answers to hello: three peers online, ten newcomers one after another
    online = [Peer(n, port).joined() for n in ("A1", "A2", "A3")]
    delays = []
    for i in range(10):
        newcomer = Peer(f"New{i}", port).joined()
        hello = newcomer.log_lines("sent", "hello")[0]
        delays.append(max(seconds_between(hello, line) for line in newcomer.log_lines("recv", "here")) * 1000)
        newcomer.quit()
    results["last_here_ms"] = delays
    print(f"  last answer to hello (3 peers online, 10 joins): median {statistics.median(delays):.1f} ms, max {max(delays):.1f} ms")
    for peer in online:
        peer.quit()

    # 2. private round trip (private -> ack): C# to C#, without and with encryption, and C# to Java
    cases = [("plain", [], False), ("encrypted", ["--passphrase", "measure"], False),
             ("encrypted_java", ["--passphrase", "measure"], True)]
    for label, options, java in cases:
        ana = Peer("Ana", port + 1, *options).joined()
        ion = Peer("Ion", port + 1, *options, java=java).joined(8)
        for i in range(20):
            ana.type(f"/msg Ion message {i}")
            ion.wait_for(f"[private] Ana: message {i}")
        time.sleep(0.5)
        sent = ana.log_lines("sent", "private")
        acks = ana.log_lines("recv", "ack")
        rtts = [seconds_between(s, a) * 1000 for s, a in zip(sent, acks)]
        sizes = [int(line.split()[4]) for line in sent]
        results[f"rtt_{label}_ms"] = rtts
        print(f"  private round trip, {label}: median {statistics.median(rtts):.2f} ms, max {max(rtts):.2f} ms; "
              f"datagram {min(sizes)}-{max(sizes)} bytes")
        for who, peer in (("csharp", ana), ("java" if java else "csharp", ion)):
            key_line = next((line for line in peer.log().splitlines() if "key derived" in line), None)
            if key_line:
                key_ms = int(re.search(r"in (\d+) ms", key_line).group(1))
                results.setdefault(f"key_ms_{who}", []).append(key_ms)
        ana.quit()
        ion.quit()
    # 2b. the same round trip timed precisely (the log has 1 ms steps): a raw peer sends 100 private
    #     messages (not encrypted) to the peer and times each ack with perf_counter
    for label, java in (("csharp", False), ("java", True)):
        target = Peer("Ana", port + 3, java=java).joined(8)
        eve = RawPeer(port + 3, "Eve")
        try:
            eve.join()
            target.wait_for("* Eve joined the chat")
            to = target.endpoint()
            times = []
            for seq in range(1, 101):
                start = time.perf_counter()
                eve.send(eve.message("private", seq=seq, to="Ana", text=f"ping {seq}"), to)
                if eve.wait("own", lambda d, n=seq: kind_of(d) == "ack" and json.loads(d)["seq"] == n, 2):
                    times.append((time.perf_counter() - start) * 1000)
            results[f"rtt_precise_{label}_ms"] = times
            print(f"  private -> ack timed by a raw peer, {label} peer: median {statistics.median(times):.3f} ms, "
                  f"90th percentile {sorted(times)[int(len(times) * 0.9)]:.3f} ms ({len(times)} of 100 acked)")
        finally:
            eve.close()
            target.quit()

    for who in ("csharp", "java"):
        if f"key_ms_{who}" in results:
            print(f"  key derivation ({who}, 600 000 iterations): {results[f'key_ms_{who}']} ms")

    # 3. a killed peer: eight pairs at once, each killed at a random moment between two alives
    pairs = []
    for i in range(8):
        a = Peer(f"Watch{i}", port + 10 + i)
        b = Peer(f"Victim{i}", port + 10 + i)
        pairs.append((a, b))
    for a, b in pairs:
        a.joined()
        b.joined()
    kill_times = []
    for a, b in pairs:
        time.sleep(random.uniform(0.3, 1.2))
        kill_times.append(time.monotonic())
        b.kill()
    detected = []
    for (a, b), killed in zip(pairs, kill_times):
        seen = a.wait_for("left the chat (not heard from", 20)
        detected.append(seen - killed)
    results["killed_detected_s"] = detected
    print("  killed peer noticed after: " + ", ".join(f"{d:.1f}" for d in detected) +
          f" s (min {min(detected):.1f}, max {max(detected):.1f})")
    for a, _ in pairs:
        a.quit()

    # 4. datagram sizes: the kinds of datagrams, and a 500-character message in three alphabets
    ana = Peer("Ana", port + 2, "--passphrase", "x").joined()
    ion = Peer("Ion", port + 2, "--passphrase", "x").joined()
    alphabets = (("ASCII", "a"), ("Romanian", "ă"), ("Cyrillic", "д"), ("Chinese", "中"))  # 1, 2, 2, 3 bytes in UTF-8
    for label, letter in alphabets:
        ana.type(letter * 500)
        ana.type("/msg Ion " + letter * 500)
        ion.wait_for("[private] Ana: " + letter * 500)
    time.sleep(0.5)
    chats = [int(line.split()[4]) for line in ana.log_lines("sent", "chat")]
    privates = [int(line.split()[4]) for line in ana.log_lines("sent", "private")]
    results["size_500_chars"] = {label: {"general": c, "private": p}
                                 for (label, _), c, p in zip(alphabets, chats, privates)}
    for label, sizes in results["size_500_chars"].items():
        print(f"  500 characters, {label}: general {sizes['general']} bytes, private (encrypted) {sizes['private']} bytes")
    kinds = {}
    for line in ana.log().splitlines():
        parts = line.split()
        if len(parts) >= 5 and parts[1] in ("sent", "recv") and parts[3] not in kinds and parts[3] not in ("chat", "private"):
            kinds[parts[3]] = int(parts[4])
    results["size_by_kind"] = kinds
    print("  sizes of the other kinds (name Ana): " + ", ".join(f"{k} {v} B" for k, v in kinds.items()))
    ana.quit()
    ion.quit()

    out = ROOT / "lab3" / "results" / "measurements.json"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(results, indent=2), encoding="utf-8")
    print(f"  saved to {out.relative_to(ROOT)}")


TESTS = [test_general_channel, test_join_and_users, test_name_taken, test_same_name_same_moment, test_bye,
         test_killed_peer, test_restarted_peer, test_private_message, test_private_errors,
         test_private_to_gone_peer, test_invalid_datagrams, test_unicode, test_encrypted_on_the_wire,
         test_wrong_passphrase, test_tampered_message, test_broadcast_mode, test_loopback_only, test_java_peer,
         test_java_invalid_datagrams]


def main():
    check(EXE.exists(), f"build first: {EXE} not found")
    shutil.rmtree(LOGS, ignore_errors=True)  # log names repeat from run to run: start with an empty folder
    LOGS.mkdir(parents=True, exist_ok=True)
    global IFACE
    IFACE = default_interface()
    print(f"Network of the chat: {IFACE}")
    words = sys.argv[1:]
    if words == ["measure"]:
        try:
            measure(47900)
        finally:
            for peer in RUNNING:
                peer.stop()
        return 0

    chosen = [t for t in TESTS if not words or any(w in t.__name__ for w in words)]
    failed = 0
    for index, test in enumerate(chosen):
        port = 47000 + 20 * TESTS.index(test)
        title = test.__name__[5:].replace("_", " ")
        print(f"[{index + 1}/{len(chosen)}] {title} (port {port}): {test.__doc__.strip()}")
        start = time.monotonic()
        try:
            test(port)
            print(f"    PASSED in {time.monotonic() - start:.1f} s")
        except TestFailed as e:
            failed += 1
            print(f"    FAILED: {e}")
        finally:
            for peer in RUNNING:
                peer.stop()
            RUNNING.clear()
    print(f"\n{len(chosen) - failed} of {len(chosen)} tests passed.")
    return 1 if failed else 0


IFACE = "127.0.0.1"

if __name__ == "__main__":
    sys.exit(main())
