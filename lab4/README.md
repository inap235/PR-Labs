# Laboratory Work 4: A Message Broker

A message broker with publishers and subscribers. Publishers send messages to a topic; the broker
gives each one an id (1, 2, 3, ... in each topic) and the time, saves it, puts it in a queue and
answers at once. A delivery task in the background takes the messages out of the queue, in order,
and gives each one to every subscriber of its topic. Publishers and subscribers know only the
address of the broker.

- **Part 1** (grades 5 to 7): the broker speaks HTTP, on `HttpListener` (no web framework). A REST API
  with JSON; a subscriber gets the messages over an answer that stays open (one JSON per line).
- **Part 2** (grades 8 to 10): the same broker over gRPC, with the contract in
  [`grpc/broker.proto`](grpc/broker.proto) and a server stream for each subscriber.

Both parts have the three tasks: 1. publish and deliver, 2. restarts (nothing is lost),
3. strangers (only publishers with a token can publish). The report is in `report/`.

| Folder | What it is |
|---|---|
| `lab4/Lab4.cs`, `lab4/Http/` | Part 1: the HTTP broker and client (part of the `Labs` project at the repository root) |
| `lab4/grpc/` | Part 2: the project `BrokerGrpc` (gRPC broker and client, `broker.proto`) |
| `lab4/Core/` | the broker without its network part, compiled into both projects |
| `lab4/tokens.json` | Task 3: the publishers, their tokens and their topics |
| `lab4/tests/test_broker.py` | automatic tests: real brokers, publishers and subscribers on this computer |
| `lab4/data/`, `lab4/logs/` | the topics on disk and the logs, created when the broker runs |

| File | What it does |
|---|---|
| `Core/MessageBroker.cs` | the queue (producers: publishes; consumer: the delivery task), the subscribers, the tokens check |
| `Core/MessageStore.cs` | the messages of every topic, the ids, one lock per topic, the files (temporary file, then replace) |
| `Core/Subscription.cs` | one subscriber: its outbox, the messages it asked for after a restart, each message once |
| `Core/TokenFile.cs` | Task 3: reads `tokens.json` (again when it changes) |
| `Core/BrokerLog.cs` | the log: a line for every request and every delivery |
| `Core/BrokerRules.cs` | limits, topic names, the JSON format |
| `Core/ClientPrograms.cs` | the publisher, the subscriber, `topics` and `bench`, the same for both parts |
| `Core/IBrokerClient.cs` | what a client of the broker can do; implemented by `HttpBrokerClient` and `GrpcBrokerClient` |
| `Http/HttpBroker.cs` | Part 1: the HTTP server (routes, status codes, the stream) |
| `Http/HttpBrokerClient.cs` | Part 1: the client, on `HttpClient` |
| `grpc/BrokerService.cs` | Part 2: the three calls of `broker.proto` |
| `grpc/GrpcBrokerClient.cs` | Part 2: the client, on the generated `Broker.BrokerClient` |
| `grpc/Program.cs` | Part 2: the commands, and Kestrel set up for gRPC only (HTTP/2) |

## Requirements

- .NET 9 SDK (`dotnet --version` should print `9.x`)
- For Part 2, the NuGet packages `Grpc.AspNetCore` and `Grpc.Net.Client` (downloaded by `dotnet build`)
- For the tests: Python 3.8 or newer (only its standard library)

## Build

From the repository root (the folder with `Labs.csproj`):

```
dotnet build Labs.csproj -c Release
dotnet build lab4/grpc/BrokerGrpc.csproj -c Release
```

or both at once with `dotnet build ConsoleApp1.sln -c Release`.

## Run: Part 1 (HTTP)

The broker, publishers and subscribers are three commands of one program; run each in its own terminal.

```
bin\Release\net9.0\Labs.exe lab4 broker --port 8080
bin\Release\net9.0\Labs.exe lab4 subscribe --broker http://localhost:8080 --topic grades
bin\Release\net9.0\Labs.exe lab4 publish --broker http://localhost:8080 --topic grades --name teacher
```

The publisher publishes every line that you type; the subscriber shows each message as it arrives:

```
[grades] teacher: Lab 3 grades are online
```

| Command | Options |
|---|---|
| `broker` | `--port 8080`, `--data <folder>` (default `lab4/data/http`), `--tokens <file>` (Task 3), `--log <file>` (default `lab4/logs/broker-http.log`) |
| `publish` | `--broker <address>`, `--topic <topic>`, `--name <name>`, `--token <token>` (or the variable `BROKER_TOKEN`) |
| `subscribe` | `--broker <address>`, `--topic <topic>`, `--after <id>` (first the stored messages after that id, `0` for all), `--ids` (show id and time) |
| `topics` | `--broker <address>`: the topics, their sizes and their subscribers |
| `bench` | `--broker`, `--topic bench`, `--messages 1000`, `--publishers 4`, `--token`: publishers at the same time, checks the ids |

The address can be `http://192.168.1.5:8080`, `192.168.1.5:8080` or `localhost`. A topic is 1 to 64
letters, digits, `-` or `_`, in lower case (`Grades` is the topic `grades`).

### The HTTP API

| Request | What it does | Answer |
|---|---|---|
| `POST /topics/{topic}/messages` with `{"sender": "teacher", "body": "..."}` | queues a message | `202 Accepted` and the message |
| `GET /topics/{topic}/stream` (optional `?after=<id>`) | new messages, as they arrive | `200 OK`, stays open |
| `GET /topics` | the topics, and their sizes | `200 OK` |

Every message that the broker answers with or forwards:

```json
{"id":17,"topic":"grades","sender":"teacher","body":"Lab 3 grades are online","time":"2026-11-23T13:53:03.2000167+00:00"}
```

The stream (`Content-Type: application/x-ndjson`, chunked) has one message per line. After 15 s
without messages the broker sends an empty line: a heartbeat, so a dead subscriber is noticed by
the broker and a dead broker by the subscriber. The header `Last-Id` says after which id the
subscriber gets messages. Errors are JSON `{"error": "..."}` with `400` (bad topic, bad JSON,
missing field), `401` + `WWW-Authenticate: Bearer`, `403`, `404`, `405` + `Allow`, `413` (body over
64 KB) or `503` (the message could not be saved).

With curl:

```
curl -i -X POST http://localhost:8080/topics/grades/messages -H "Content-Type: application/json" -d "{\"sender\":\"teacher\",\"body\":\"hello\"}"
curl -N http://localhost:8080/topics/grades/stream
curl http://localhost:8080/topics
```

## Run: Part 2 (gRPC)

The same commands and options, in the program `BrokerGrpc`; the default port is 50051.

```
lab4\grpc\bin\Release\net9.0\BrokerGrpc.exe broker --port 50051
lab4\grpc\bin\Release\net9.0\BrokerGrpc.exe subscribe --broker localhost:50051 --topic grades
lab4\grpc\bin\Release\net9.0\BrokerGrpc.exe publish --broker localhost:50051 --topic grades --name teacher
```

(or `dotnet run --project lab4/grpc -c Release -- broker`). The broker has no HTTP API in this
part: only the calls of `broker.proto` (`Publish`, `Subscribe`, `ListTopics`), over HTTP/2 without
TLS. Errors are gRPC status codes: `InvalidArgument`, `Unauthenticated`, `PermissionDenied`, `Unavailable`.

## Task 2: restarts

Every topic is the file `<data>/<topic>.json`, with all its messages. A publish writes the whole
topic to `<topic>.json.tmp`, forces it to the disk, and renames it over `<topic>.json`; only then
does the broker answer `202`. A broker killed in the middle of a write leaves the old file whole
(and a `.tmp`, deleted at the next start). After a restart the broker reads the files, and the ids go on.

The clients come back by themselves: the publisher tries a message again every second while the broker
cannot be reached (up to 30 s); the subscriber connects again and asks for the messages after the
last one it showed (`?after=<id>` / `after_id`), so it misses none and shows none twice.

## Task 3: tokens

```
bin\Release\net9.0\Labs.exe lab4 broker --tokens lab4/tokens.json
bin\Release\net9.0\Labs.exe lab4 publish --topic grades --name teacher --token teacher-5f2c9a71d3
```

`tokens.json` lists the publishers, each with its token and the topics it may publish to (`"*"`: all):

```json
{ "publishers": [ { "name": "teacher", "token": "teacher-5f2c9a71d3", "topics": ["grades", "news"] } ] }
```

The token goes in `Authorization: Bearer <token>` (HTTP) or in the metadata `authorization: Bearer <token>`
(gRPC). Without a token or with an unknown one: `401` / `Unauthenticated`; a topic the token does not
allow: `403` / `PermissionDenied`. The sender of the message is the name of the token's publisher,
so nobody can publish under somebody else's name. The file is read again when it changes. Subscribing
and listing the topics need no token. Without `--tokens` everybody may publish (Tasks 1 and 2).

## The log

One line for every request and every delivery, on the console and in the log file: the time, the
client, the request, the topic, the id of the message and the result.

```
2026-11-23 15:53:01.354  [::1]:54123            GET /topics/grades/stream           topic=grades      id=-      200 OK, subscribed
2026-11-23 15:53:03.246  [::1]:54124            POST /topics/grades/messages        topic=grades      id=1      202 Accepted, from teacher
2026-11-23 15:53:03.255  [::1]:54123            DELIVER                             topic=grades      id=1      delivered
```

## On two computers

Start the broker on one computer and use its address on the other (`ipconfig` shows it).

- Part 1 on Windows: `HttpListener` needs, once, in a terminal opened as administrator,
  `netsh http add urlacl url=http://+:8080/ user=Everyone`; without it the broker listens only on
  `localhost` (it says so when it starts).
- Allow the port in the firewall of the broker's computer:
  `netsh advfirewall firewall add rule name="Lab4 broker" dir=in action=allow protocol=TCP localport=8080`
  (and `50051` for Part 2).

For the defense: two publishers and two subscribers of the same topic, on both computers; the
publishers type at the same time; every message gets its own id, and both subscribers show all of them
in the same order. `bench --publishers 8` shows the same with thousands of messages.

## Tests

```
python lab4/tests/test_broker.py              all tests (about a minute)
python lab4/tests/test_broker.py http tokens  the tests whose names contain these words
python lab4/tests/test_broker.py measure      the measurements of the report (lab4/results/measurements.json)
```

The tests start real brokers (each with its own port and data folder under `lab4/data/tests`),
publishers and subscribers, and check: ids and delivery to two subscribers; every error of the API;
8 publishers at the same time; subscribers that crash; a broker killed and started again (and five
times in a row while a publisher sends); a half-written file; tokens (401, 403, a token added while
the broker runs); and that the gRPC broker has no HTTP API.
