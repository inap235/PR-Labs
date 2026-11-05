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

All commands run from the repository root. Stop the server with `Ctrl+C`.

Server, with one worker per logical processor:

```
dotnet run -c Release -- lab2 server --port 5000
```

Server with other options:

```
dotnet run -c Release -- lab2 server --port 5000 --workers 1
dotnet run -c Release -- lab2 server --port 5000 --workers 2
dotnet run -c Release -- lab2 server --port 5000 --workers 8
dotnet run -c Release -- lab2 server --port 6000
dotnet run -c Release -- lab2 server --port 5000 --bind 127.0.0.1
dotnet run -c Release -- lab2 server --port 5000 --workers 2 --stock lab2/data/demo.json
```

After the build, the same options also work with `bin\Release\net9.0\Labs.exe`:

```
bin\Release\net9.0\Labs.exe lab2 server --port 5000 --workers 2
```

| Option | Default | Meaning |
|---|---|---|
| `--port` | `5000` | port to listen on |
| `--bind` | `0.0.0.0` | address to listen on; `0.0.0.0` = every network, `127.0.0.1` = this computer only |
| `--workers` | logical processors | number of workers that prepare orders |
| `--stock` | `lab2/data/stock.json` | file that keeps the stock between runs |

At start the server prints the addresses other computers can connect to, the number of workers
and the stock. Then it writes one log line for every connection, order (accepted, refused, ready,
cancelled), delivery, alert and error, with the endpoint of the client.

Client, in its own terminal. Run the same command in more terminals for more clients: each one is
a separate connection.

```
java -cp lab2/client-java/out coffeeshop.ShopClient --host 127.0.0.1 --port 5000 --role client
```

Provider, in its own terminal. Only one can be connected at a time; a second one says so and ends:

```
java -cp lab2/client-java/out coffeeshop.ShopClient --host 127.0.0.1 --port 5000 --role provider
```

`--port` must match the port of the server.

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
starts. To change the file, stop the server first. An invalid file is kept as `stock.json.bad` and
the server starts with a random stock. A file that cannot be read (for example locked by another
program) stops the server with an error, so the saved stock is not replaced.

New random stock (10 to 50 of each product):

```
Remove-Item lab2/data/stock.json
```

A small stock, to reach the low-stock alert quickly:

```
Set-Content lab2/data/stock.json '{"coffee": 6, "tea": 6, "croissant": 6}'
```

## Two computers in the same network

1. On the server computer, start the server and note the address it prints, for example
   `other computers connect to 172.18.0.75:5000 (Wi-Fi)`.
2. The first time, Windows asks whether `Labs.exe` may use the network: allow it for private
   networks. Without that prompt, add an inbound firewall rule for TCP port 5000.
3. On the other computer, with the address from step 1:

```
java -cp lab2/client-java/out coffeeshop.ShopClient --host 172.18.0.75 --port 5000 --role client
java -cp lab2/client-java/out coffeeshop.ShopClient --host 172.18.0.75 --port 5000 --role provider
```

## Showing the tasks

### Task 1: one client

```
dotnet run -c Release -- lab2 server --port 5000
java -cp lab2/client-java/out coffeeshop.ShopClient --host 127.0.0.1 --port 5000 --role client
```

Type in the client: `MENU`, then `ORDER croissant 2 tea 1`: `ACCEPTED` comes at once, `READY` 7 s
later. `ORDER coffee 9999` is refused with how many are left. `ORDER muffin 1`, `ORDER tea -1` and
`FOO` get an error.

### Task 2: many clients

Server with 2 workers, then the client command in three terminals:

```
dotnet run -c Release -- lab2 server --port 5000 --workers 2
java -cp lab2/client-java/out coffeeshop.ShopClient --host 127.0.0.1 --port 5000 --role client
```

Type `ORDER coffee 2` in all three: two orders start, the third waits (the server log shows
`started by worker` and how many wait). A waiting client can still ask for `MENU` or order again.

### Task 3: the provider

With the server stopped, make the stock small, then start the server, a client and a provider:

```
Set-Content lab2/data/stock.json '{"coffee": 6, "tea": 6, "croissant": 6}'
dotnet run -c Release -- lab2 server --port 5000
java -cp lab2/client-java/out coffeeshop.ShopClient --host 127.0.0.1 --port 5000 --role client
java -cp lab2/client-java/out coffeeshop.ShopClient --host 127.0.0.1 --port 5000 --role provider
```

- Client: `ORDER croissant 2`. The provider gets `ALERT croissant 4` at once.
- Provider: `DELIVER croissant 20` adds to the stock. The same command from the client gets an error.
- Provider: `QUIT`. Client: `ORDER tea 2`. Start the provider again: it gets `ALERT tea 4` when it connects.

### Task 4: when things go wrong

Server with 1 worker, then the client command in two terminals:

```
dotnet run -c Release -- lab2 server --port 5000 --workers 1
java -cp lab2/client-java/out coffeeshop.ShopClient --host 127.0.0.1 --port 5000 --role client
```

- First client: `ORDER coffee 2`, then `ORDER tea 1` (this one waits).
- Second client: `ORDER croissant 1` (it waits too).
- Close the terminal of the first client: the server logs that both of its orders are cancelled
  and the products are back in the stock, and the second client's order starts at once.
- Lost network: with a client on another computer, turn off its Wi-Fi. The server notices after
  about 16 s.
- Stop the server with `Ctrl+C`: the client and the provider say so and end.

Random bytes, from PowerShell, while the server runs (the server logs the errors and goes on):

```
$s = [System.Net.Sockets.Socket]::new('InterNetwork', 'Stream', 'Tcp'); $s.Connect('127.0.0.1', 5000); $b = [byte[]]::new(5000); [Random]::new().NextBytes($b); [void]$s.Send($b); $s.Close()
```

### Bonus: the stock survives a restart

In a client, `ORDER tea 1`, then `MENU`. Stop the server with `Ctrl+C` and start it again with the
same command. Connect a client again: `MENU` shows the same stock.
