# Laboratory Work 2: A Coffee Shop Server

The server is written in C# (.NET 9), the client and the provider in Java. They talk over TCP
sockets used directly (`Socket` in C#, `Socket` in Java), with the text protocol described in
[PROTOCOL.md](PROTOCOL.md). The server keeps the stock in a JSON file (`System.Text.Json`).

| Folder | What it is |
|---|---|
| `lab2/Lab2.cs`, `lab2/Server/` | the server (part of the `Labs` project at the repository root) |
| `lab2/client-java/src/coffeeshop/` | the client and the provider: one program, the role is a parameter |
| `lab2/data/stock.json` | the saved stock, created by the server |

## Requirements

- .NET 9 SDK (`dotnet --version` should print `9.x`)
- JDK 17 or newer: `java -version` **and** `javac -version` should both print 17 or more.


## Build

From the repository root (the folder with `Labs.csproj`):

```
dotnet build Labs.csproj -c Release
javac -d lab2/client-java/out lab2/client-java/src/coffeeshop/*.java
```

## Run

Server:

```
dotnet run -c Release -- lab2 server --port 5000
```

or, after the build, `bin\Release\net9.0\Labs.exe lab2 server --port 5000`.

| Option | Default | Meaning |
|---|---|---|
| `--port` | `5000` | port to listen on |
| `--bind` | `0.0.0.0` | address to listen on; `0.0.0.0` = every network, `127.0.0.1` = this computer only |
| `--workers` | logical processors | number of workers that prepare orders |
| `--stock` | `lab2/data/stock.json` | file that keeps the stock between runs |

At start the server prints the addresses other computers can connect to, the number of workers
and the stock. Then it writes one log line for every connection, order (accepted, refused, ready,
cancelled), delivery, alert and error, with the endpoint of the client.

Client and provider, each in its own terminal:

```
java -cp lab2/client-java/out coffeeshop.ShopClient --host 127.0.0.1 --port 5000 --role client
java -cp lab2/client-java/out coffeeshop.ShopClient --host 127.0.0.1 --port 5000 --role provider
```

Commands are typed as in the protocol (`help` lists them):

```
MENU
ORDER croissant 2 tea 1
DELIVER coffee 20 croissant 10      (provider only)
QUIT
```

Messages from the server appear as soon as they arrive (`<< READY 3`, `<< ALERT tea 4`), even
while you are typing. `Ctrl+Z` then Enter (Windows) or `Ctrl+D` (Linux, macOS) also quits.

## Saved stock

The server saves the stock to `lab2/data/stock.json` after every change and loads it when it
starts. Delete the file to start with a new random stock (10 to 50 of each product).

## Two computers in the same network

1. On the server computer, start the server and note the address it prints, for example
   `other computers connect to 172.18.0.75:5000 (Wi-Fi)`.
2. The first time, Windows asks whether `Labs.exe` may use the network: allow it for private
   networks. Without that prompt, add an inbound firewall rule for TCP port 5000.
3. On the other computer: `java -cp lab2/client-java/out coffeeshop.ShopClient --host 172.18.0.75 --port 5000`.


## Showing the tasks

**Task 1.** Start the server and one client. `MENU`, then `ORDER croissant 2 tea 1`: `ACCEPTED`
comes at once, `READY` 7 s later. `ORDER coffee 999` is refused with how many are left;
`ORDER muffin 1`, `ORDER tea -1` and `FOO` get an error.

**Task 2.** Start the server with `--workers 2` and three clients. Order from all three at once:
two orders start, the third waits (the server log shows `started by worker` and how many wait).
A waiting client can still ask for `MENU` or order again.

**Task 3.** Start a provider. Order until a product falls below 5: the provider gets `ALERT` at
once. `DELIVER croissant 20` adds to the stock; the same command from a client gets an error.
Stop the provider, make another product fall below 5, start the provider again: it gets the alert
when it connects.

**Task 4.** With one long order preparing and one waiting (`--workers 1`), close the client
window or kill it in the Task Manager: the server logs that both orders are cancelled and the
products go back to the stock. For a lost network, turn off the Wi-Fi of the client computer:
the server notices after about 16 s. Stop the server: the client and the provider say so and end.
