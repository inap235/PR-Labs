# Laboratory Work 3: A Local Network Chat

A chat for the local network over UDP sockets used directly (`Socket` with `SocketType.Dgram` in
C#, `DatagramSocket` in Java). There is no server: every running copy is a peer that sends and
receives. The general channel goes to a multicast group (or as a broadcast with `--broadcast`), a
private message only to its receiver, encrypted with AES-256-GCM when the chat has a passphrase.
The datagrams are described in [PROTOCOL.md](PROTOCOL.md); the report is in `report/`.

| Folder | What it is |
|---|---|
| `lab3/Lab3.cs`, `lab3/Chat/` | the chat in C# (part of the `Labs` project at the repository root) |
| `lab3/chat-java/src/lanchat/` | the same chat in Java (bonus: it talks with the C# one) |
| `lab3/tests/test_chat.py` | automatic tests: real peers on this computer, typed into through their input |
| `lab3/logs/` | the log files of the peers, created when they run |

| File in `lab3/Chat/` | What it does |
|---|---|
| `ChatPeer.cs` | the peer: two sockets, the network thread, joining, private messages, timers |
| `Message.cs` | one datagram: JSON encoding, and the checks that drop invalid ones |
| `Protocol.cs` | the constants of the protocol (group, limits, times) and the kinds of datagrams |
| `PrivateCipher.cs` | Task 4: key derivation (PBKDF2) and AES-256-GCM |
| `PeerTable.cs` | the peers online, shared by the two threads |
| `DatagramLog.cs` | the log file |
| `ConsoleUi.cs` | prints messages while the user types, without mixing them with the typed line |
| `LocalNetwork.cs` | chooses the network (the adapter with a gateway) and its broadcast address |

## Requirements

- .NET 9 SDK (`dotnet --version` should print `9.x`)
- For the Java peer: JDK 17 or newer (`java -version` and `javac -version`)
- For the tests: Python 3.8 or newer (only its standard library)

## Build

From the repository root (the folder with `Labs.csproj`):

```
dotnet build Labs.csproj -c Release
javac -d lab3/chat-java/out lab3/chat-java/src/lanchat/*.java
```

## Run

One peer per terminal. The name and the port are parameters; every peer of one chat uses the
same port (and the same passphrase).

```
dotnet run -c Release -- lab3 chat --name Ana --port 5000
bin\Release\net9.0\Labs.exe lab3 chat --name Ion --port 5000
bin\Release\net9.0\Labs.exe lab3 chat --name Maria --port 5000 --passphrase "our secret"
```

| Option | Default | Meaning |
|---|---|---|
| `--name` | (required) | 1 to 16 letters, digits, `_` or `-`; unique in the chat, not case sensitive |
| `--port` | `5000` | the port of the chat |
| `--passphrase` | none | Task 4: encrypts the private messages with a key derived from it |
| `--broadcast` | off | send the general channel as a broadcast instead of to the multicast group |
| `--interface` | the adapter with a gateway | the IPv4 address of this computer whose network the chat uses |
| `--log` | `lab3/logs/<name>-<process id>.log` | the log file |

`bin\Release\net9.0\Labs.exe lab3 networks` lists the addresses of this computer, with the one the
chat chooses by default marked with `*`.

In the chat:

| Command | What it does |
|---|---|
| `text` | sends text to everybody (the general channel) |
| `/msg name text` | sends text to name only (a private message) |
| `/users` | shows who is online, with their endpoints |
| `/quit` | leaves the chat (also `Ctrl+C`, or `Ctrl+Z` then Enter on an empty line) |

At start the peer prints its endpoint and its log file. Messages from the others appear as soon as
they arrive, above the line being typed.

Java peer, with the same options (on Windows, run `chcp 65001` first so that letters like ă and ș
are shown and typed correctly):

```
java -cp lab3/chat-java/out lanchat.LanChat --name Ion --port 5000 --passphrase "our secret"
```

On Windows, Java reads the command line in the ANSI code page: a passphrase with letters outside
it (ă, ș, ț in Cp1251) arrives changed and gives another key. Give such a passphrase in the
environment variable instead (PowerShell):

```
$env:LANCHAT_PASSPHRASE = "parolă"; java -cp lab3/chat-java/out lanchat.LanChat --name Ion --port 5000
```

To watch the log of a peer while it runs, in another terminal:

```
Get-Content lab3\logs\Ana-12345.log -Wait
```

## Several computers in the same network

1. Connect the computers to the same network. Many Wi-Fi networks (university, café) do not pass
   multicast or broadcast between their clients: use the hotspot of a phone.
2. The first time, Windows asks whether `Labs.exe` (and `java.exe`) may use the network: allow it
   for **private** networks. If the network is "public", either make it private (Settings →
   Network → Properties) or allow UDP port 5000 in the firewall. Without it, the datagrams of the
   other computers do not arrive.
3. Start the chat on every computer with the same `--port`. Each one prints the network it uses,
   for example `through 192.168.43.12 (Wi-Fi)`; if it shows another adapter (VirtualBox, WSL),
   add `--interface 192.168.43.12`.
4. If multicast does not pass, start every peer with `--broadcast`.
5. On a Mac, the commands use forward slashes and the program has no `.exe`:
   `./bin/Release/net9.0/Labs lab3 chat --name Maria --port 5000`. The first time, macOS asks
   whether the terminal may find devices on the local network: allow it (System Settings →
   Privacy & Security → Local Network), otherwise the other computers' datagrams do not arrive.

## Showing the tasks

All the commands run from the repository root, each peer in its own terminal.

### Task 1: the general channel

```
bin\Release\net9.0\Labs.exe lab3 chat --name Ana --port 5000
bin\Release\net9.0\Labs.exe lab3 chat --name Ion --port 5000
bin\Release\net9.0\Labs.exe lab3 chat --name Maria --port 5000      (on the other computer)
```

Type `hello everyone` in Ana: every peer shows `[general] Ana: hello everyone`, Ana too, once.
Start typing in Ion and let Maria send something: it appears above Ion's line, which stays.
Ana's log shows one `sent ... 239.255.42.42:5000 chat` and her own copy coming back
(`own datagram, not shown again`).

### Task 2: who is online

- Start a fourth peer: within a second it prints `Online: Ana, Ion, Maria.`, and the others print
  `* ... joined the chat`. `/users` shows everybody with their endpoints.
- Start another peer with `--name ana`: it prints that the name is already used and ends.
- `/quit` in Ion: the others print `* Ion left the chat` at once.
- Close the terminal of Maria (or kill her process): 10 to 15 s later the others print
  `* Maria left the chat (not heard from for 15 s)`.

### Task 3: private messages

- Ana: `/msg Ion see you at five`. Only Ion shows `[private] Ana: see you at five`, not Maria, even
  on the same computer. Ana's log: one `sent` to Ion's endpoint, one `recv ... ack`.
- `/msg Nobody hi` gets `* Nobody is not online`.
- Kill Ion, then at once `/msg Ion are you there?` in Ana: on Windows the computer of Ion answers
  "port unreachable" and Ana says so at once; otherwise Ana tries 3 times and says that Ion did not
  confirm it.

### Task 4: secure private messages

Every peer with the same passphrase:

```
bin\Release\net9.0\Labs.exe lab3 chat --name Ana --port 5000 --passphrase "our secret"
bin\Release\net9.0\Labs.exe lab3 chat --name Ion --port 5000 --passphrase "our secret"
bin\Release\net9.0\Labs.exe lab3 chat --name Eve --port 5000 --passphrase "wrong guess"
```

Private messages between Ana and Ion work. A private message to Eve is not shown by her: her log
says `cannot be decrypted`, and Ana says that Eve did not confirm it. The general channel still
reaches everybody. In Wireshark (filter `udp.port == 5000` and the port of the receiver) the
private datagram has `nonce` and `data` but no `text`.

### Invalid datagrams

From PowerShell, while the chat runs, 300 random bytes to the endpoint a peer printed at start
(`This peer: 192.168.100.15:56254.`); its log shows `drop ... not UTF-8 text` and it goes on:

```
$s = [System.Net.Sockets.Socket]::new('InterNetwork', 'Dgram', 'Udp'); $b = [byte[]]::new(300); [Random]::new().NextBytes($b); [void]$s.SendTo($b, [System.Net.IPEndPoint]::new([ipaddress]'192.168.100.15', 56254)); $s.Close()
```

(Sent to the group `239.255.42.42` instead, this datagram may leave through another adapter of the
computer and never reach the chat: the socket has no `IP_MULTICAST_IF`, which the chat always sets.)
`python lab3/tests/test_chat.py invalid` sends sixteen kinds of invalid datagrams.

### Bonus: two languages

C# and Java peers in one chat, with the same passphrase: general and private messages pass both
ways, `/users` lists all of them, and a Java peer cannot take the name of a C# one.

```
bin\Release\net9.0\Labs.exe lab3 chat --name Ana --port 5000 --passphrase "our secret"
java -cp lab3/chat-java/out lanchat.LanChat --name Ion --port 5000 --passphrase "our secret"
```

## Tests

```
python lab3/tests/test_chat.py                 all tests (about 2 minutes)
python lab3/tests/test_chat.py private java    only the tests whose names contain these words
python lab3/tests/test_chat.py measure         the measurements of the report
```

The tests start real peers on this computer, on ports 47000 to 47999, type into them and check
their output and their log files. Their logs are kept in `lab3/logs/tests/`.
