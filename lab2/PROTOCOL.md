# Coffee shop protocol

The server, the client and the provider talk over one TCP connection each, using text lines.
Every program that follows this file works with the others, whatever its language.

## Framing

- A message is one line of **UTF-8** text ending with `\n` (byte 10). A `\r` right before the `\n` is
  ignored, so `telnet` and `ncat` on Windows work too.
- A line holds at most **1024 bytes**, not counting the `\n`.
- TCP is a stream of bytes, not of messages: one `receive` can return half a line, or several
  lines. The receiver appends what it gets to a buffer, takes out every complete line, and keeps
  the rest for the next `receive`.
- A line longer than 1024 bytes gets `ERROR line too long`; the receiver drops bytes up to the
  next `\n` and goes on with the line after it.
- A line that is not valid UTF-8 gets `ERROR invalid UTF-8`.
- Words are separated by one or more spaces. Commands and product names are not case sensitive.
  Empty lines are ignored.

## Roles

A new connection is a **client**. To be the **provider**, its first message is `HELLO provider`.

| Message | Answer | Notes |
|---|---|---|
| `HELLO client` | `OK client` | optional; only as the first message |
| `HELLO provider` | `OK provider` | only as the first message; only one provider at a time, a second one gets `ERROR provider already connected` and stays a client |

`HELLO` after the first message gets `ERROR HELLO only as the first message`.

## Products

| Product | Preparation time |
|---|---|
| `coffee` | 4 s |
| `tea` | 3 s |
| `croissant` | 2 s |

A quantity is a whole number from 1 to 9999. A list of products is written as pairs
`<product> <quantity>`; the same product written twice counts once with the sum of both quantities.

## Client and provider to server

| Message | Who | Answer |
|---|---|---|
| `MENU` | both | `MENU coffee 4 23 tea 3 12 croissant 2 40` (product, seconds, stock) |
| `ORDER <product> <qty> ...` | client | `ACCEPTED <order> <seconds>`, later `READY <order>`; or `REFUSED ...` |
| `DELIVER <product> <qty> ...` | provider | `DELIVERED coffee 43 tea 12 croissant 50` (the stock after the delivery) |
| `QUIT` | both | `BYE`, then the server closes the connection |

- `ACCEPTED 17 11`: the products are already taken from the stock; order 17 will take 11 seconds
  to prepare once a worker starts it. The client may go on sending messages while it waits.
- `READY 17`: order 17 is prepared. The server sends it on its own, whenever the order is done.
- `REFUSED croissant 1 tea 0`: the order asks for more than the stock has. Every product that is
  short is listed with how many are left. Nothing is taken from the stock.
- A delivery may not bring a product above 100000.
- A client that sends `DELIVER` gets `ERROR only the provider can deliver`.
- The provider that sends `ORDER` gets `ERROR the provider cannot order`.

## Server to provider, on its own

| Message | When |
|---|---|
| `ALERT <product> <stock>` | the stock of a product falls from 5 or more to below 5 |

If no provider is connected at that moment, the provider gets an `ALERT` for every product that is
below 5 as soon as it says `HELLO provider`.

## Errors

Every invalid message gets `ERROR <reason>` and the connection stays open, for example:

```
ERROR unknown command 'FOO'
ERROR unknown product 'muffin'
ERROR bad quantity 'abc' for coffee
ERROR missing quantity for tea
ERROR empty order
ERROR line too long
ERROR invalid UTF-8
ERROR the stock can hold at most 100000 of each product
```

## When a connection ends

- When a client disconnects (closes, is killed, or its network is gone), the server cancels all its
  orders: the waiting ones and the one being prepared. Their products go back to the stock.
- When the provider disconnects, the shop goes on with the stock it has, and another provider may
  connect.
- When the server disappears, the client and the provider say so and end.

## Example

```
> HELLO client
< OK client
> MENU
< MENU coffee 4 23 tea 3 12 croissant 2 1
> ORDER croissant 2 tea 1
< REFUSED croissant 1
> order croissant 1 tea 1 coffee 1
< ACCEPTED 1 9
> MENU
< MENU coffee 4 22 tea 3 11 croissant 2 0
  ... 9 seconds later ...
< READY 1
```

## Saved stock

The stock is kept between runs in a JSON file (by default `lab2/data/stock.json`):

```
{
  "coffee": 43,
  "tea": 12,
  "croissant": 50
}
```
