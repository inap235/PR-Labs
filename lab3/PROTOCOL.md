# LAN chat protocol (`lanchat/1`)

Every peer of the chat follows this file, whatever its language: the C# peer (`lab3/Chat/`) and
the Java peer (`lab3/chat-java/`) talk to each other because of it. There is no server.

## Transport

- UDP over IPv4. **One message is one datagram**; UDP keeps the boundaries of datagrams, so there
  is no delimiter and no length prefix.
- All the peers of one chat use the same **port** (the chat port, default 5000).
- Every peer has **two sockets**:

| Socket | Bound to | Options | Receives |
|---|---|---|---|
| shared | `0.0.0.0:<chat port>` | `SO_REUSEADDR` before bind; joined to the group `239.255.42.42` | the general channel: `hello`, `join`, `alive`, `bye`, `chat` |
| own | `0.0.0.0:0` (a free port the system chooses) | `IP_MULTICAST_IF` = the chosen network, `IP_MULTICAST_TTL` = 1, `SO_BROADCAST` | what is for this peer only: `here`, `taken`, `private`, `ack` |

- **Every datagram is sent from the own socket.** Its source address and port are the address of
  the peer in the chat: the others send their answers and private messages there.
- The general channel goes to the multicast group `239.255.42.42:<chat port>`, or, if the peer was
  started with `--broadcast`, to the broadcast address of its network (for example
  `192.168.1.255:<chat port>`). Every shared socket receives both, so the two kinds of peers can
  be in one chat.
- Nothing for one peer only is ever sent to the chat port: several peers of one computer share it,
  and a unicast datagram to a shared port reaches only one of them.

## Datagram format

A datagram is one JSON object in UTF-8. Fields:

| Field | Type | In | Meaning |
|---|---|---|---|
| `proto` | string | all | always `"lanchat/1"` |
| `type` | string | all | `hello`, `here`, `taken`, `join`, `alive`, `bye`, `chat`, `private`, `ack` |
| `from` | string | all | the name of the sender |
| `id` | string | all | the id of the running sender: 16 lowercase hex digits (8 random bytes), new at every start |
| `seq` | number | `chat`, `private`, `ack` | numbers the `chat` and `private` messages of one sender, from 1; in `ack`, the `seq` of the private message it confirms |
| `to` | string | `private` | the name of the receiver |
| `text` | string | `chat`; `private` without a passphrase | 1 to 500 characters |
| `nonce` | string | `private` with a passphrase | 12 bytes, Base64 |
| `data` | string | `private` with a passphrase | the encrypted text followed by the 16-byte tag, Base64 |

Other fields are ignored. Example:

```
{"proto":"lanchat/1","type":"chat","from":"Ana","id":"5f0c9a1e7b2d4c33","seq":4,"text":"hello everyone"}
```

- A **name** has 1 to 16 letters, digits, `_` or `-`. Names are unique and **not case
  sensitive**: `ana` cannot join next to `Ana`, and `/msg ana` reaches `Ana`.
- A **character** is a UTF-16 code unit (`String.Length` in C#, `String.length()` in Java).
- Non-ASCII text may be sent as UTF-8 or as `\uXXXX` escapes; both are valid JSON.

## Kinds of datagrams

| `type` | Sent to | When | Fields |
|---|---|---|---|
| `hello` | general channel | a peer starts: "I want to join with this name" | |
| `here` | the source of the `hello` | at once, by every peer that is online | |
| `taken` | the source of the `hello` (or of `join`, `alive`, ...) | by the peer that has that name | |
| `join` | general channel | 1 s after `hello`, if no `taken` came: "I have joined" | |
| `alive` | general channel | every 5 s while online | |
| `bye` | general channel | `/quit`, end of input, Ctrl+C | |
| `chat` | general channel | a line typed without `/` | `seq`, `text` |
| `private` | the own socket of the receiver | `/msg <name> <text>` | `seq`, `to`, and `text` or `nonce` + `data` |
| `ack` | the source of the `private` | the private message was received (also for a copy) | `seq` |

## Joining (Task 2)

1. The new peer sends `hello` and waits **1 second**.
2. Every peer that is online answers `here` at once; the new peer adds them to its list of the
   peers online, with the source endpoint of the answer.
3. The peer that already has the name answers `taken` instead. The new peer prints that the name
   is used and ends (exit code 3); it never sends `join`.
4. Two peers that start with **the same name at the same moment** both see the other's `hello`
   while they are still joining: the one with the **smaller `id`** (compared as text) answers
   `taken`, the other one gives up.
5. After the second without `taken`, the new peer sends `join`. The others add it then, and show
   `* Ana joined the chat`. A `join` replaces an older peer with the same name: that one is gone
   (killed and started again), or it would have answered `taken`.

Peers that are still joining do not answer `hello`; they learn each other from `join`.

## Staying online, leaving

- Every peer sends `alive` every **5 seconds**. Any datagram from a peer counts as a sign of life.
- A peer that has not been heard from for **15 seconds** is gone:
  `* Ion left the chat (not heard from for 15 s)`.
- `bye` removes the peer at once: `* Ion left the chat`.
- An `alive`, `chat` or `private` from a peer that is not in the list adds it if its name is free
  (its `join` was lost).
- Two peers **online with the same name** (their `hello` answers were lost): when one hears the
  other (`join`, `alive`, ...), the one with the smaller `id` keeps the name and sends `taken`; the
  other one leaves with `bye` (exit code 3).

## Private messages (Task 3)

- One `private` datagram, sent only to the **own socket** of the receiver, from the list of peers
  online. A private message to a name that is not online is not sent: the user gets an error.
- The receiver drops a `private` that arrives on its shared socket, or whose `to` is not its name.
- The receiver answers every valid `private` with `ack` (same `seq`), also a copy it has already
  shown, because a copy means that its first `ack` was lost.
- The sender sends **the same datagram** again after **1 second** without an `ack`, at most
  **3 times** in all. Then it tells the user that the message was probably not delivered.
- Every message is shown once: the receiver remembers the `(id, seq)` of the last 1000 messages.

## Encryption (Task 4)

With `--passphrase`, every `private` message is encrypted; the general channel is not.

- **Key**: PBKDF2 with HMAC-SHA256, password = the passphrase in UTF-8,
  salt = `"lanchat/1 port <chat port>"` in UTF-8 (for example `lanchat/1 port 5000`),
  **600 000** iterations, 32 bytes.
- **Cipher**: AES-256-GCM with a 16-byte tag.
- **Nonce**: 12 random bytes from a cryptographic random generator, new for every message. A
  message sent again (no `ack`) is the same datagram, with the same nonce and ciphertext.
- **Associated data** (covered by the tag, not sent): the UTF-8 bytes of
  `lanchat/1|<from>|<id>|<to>|<seq>`, for example `lanchat/1|Ana|5f0c9a1e7b2d4c33|Ion|7`.
- `data` = the ciphertext of the text (UTF-8), then the tag. No `text` field.
- A peer with a passphrase drops a `private` without `nonce` and `data`, and one whose tag does not
  match (another passphrase, or a changed datagram). A peer without a passphrase drops an
  encrypted one. A dropped private message gets no `ack`.

## Invalid datagrams

A datagram is dropped, and logged with the reason, when it is:

- longer than **4096 bytes** (the longest valid message is about 2 300 bytes);
- not UTF-8, not JSON, or not a JSON object;
- without `"proto":"lanchat/1"`, or with an unknown `type`;
- with an invalid `from`, `id`, `seq`, `to`, `text`, `nonce` or `data` (see the table above);
- a `chat` or `private` from a name that belongs to another `id`;
- a copy of a message already shown, or an `ack` that no private message waits for.

The chat goes on after any of them.

## Log file

Each peer writes one line for every datagram it sends, receives or drops, and `----` lines for
other events:

```
00:23:21.528  sent  239.255.42.42:5000     chat       104 B
00:23:21.529  recv  192.168.100.15:60685   chat       104 B  own datagram, not shown again
00:23:21.538  recv  192.168.100.15:63791   chat        98 B
00:23:21.545  sent  192.168.100.15:60686   private    119 B
00:23:21.547  recv  192.168.100.15:60686   ack         79 B
00:23:21.556  drop  192.168.100.15:63792   ?           24 B  not JSON
00:19:39.193  ----  Victim0 at 192.168.100.15:53068 not heard from for 15 s: gone
```

Columns: time, `sent` / `recv` / `drop`, the endpoint of the other side, the kind (`?` when the
datagram is not a message), the size in bytes, a note or the reason of the drop.
