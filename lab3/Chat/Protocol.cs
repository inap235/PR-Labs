using System.Net;
using System.Security.Cryptography;

namespace Labs.Lab3.Chat;

/// The rules every peer of the chat follows (lab3/PROTOCOL.md), whatever its language.
public static class Protocol
{
    /// Every datagram carries it: a datagram without it is not a message of this chat.
    public const string Version = "lanchat/1";

    /// The multicast group of the general channel. 239.255.0.0/16 is the "local scope" of
    /// multicast (RFC 2365): meant for one organisation's network, routers do not pass it outside.
    public static readonly IPAddress Group = IPAddress.Parse("239.255.42.42");

    public const int MaxText = 500;      // characters of one message
    public const int MaxName = 16;       // characters of a name
    public const int MaxDatagram = 4096; // bytes; the largest valid message is about 2 300 bytes

    public static readonly TimeSpan JoinWait = TimeSpan.FromSeconds(1);   // answers to hello come within it
    public static readonly TimeSpan AliveEvery = TimeSpan.FromSeconds(5); // "I am still here"
    public static readonly TimeSpan GoneAfter = TimeSpan.FromSeconds(15); // silent this long = gone
    public static readonly TimeSpan AckWait = TimeSpan.FromSeconds(1);    // a private message is sent again
    public const int SendAttempts = 3;                                    // ... at most this many times in all

    /// 1 to 16 letters, digits, '_' or '-': no spaces, so "/msg name text" can be split.
    public static bool IsValidName(string? name) =>
        name is { Length: >= 1 and <= MaxName } && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    /// The id of a running peer: 8 random bytes as 16 lowercase hex digits.
    public static bool IsValidId(string? id) =>
        id is { Length: 16 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    /// Names are unique without regard to case: "ana" cannot join next to "Ana".
    public static bool SameName(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// The kinds of datagrams ("type" in the JSON).
public static class Kind
{
    public const string Hello = "hello";     // to the group:  I want to join with this name
    public const string Here = "here";       // to the new peer: I am online (answer to hello)
    public const string Taken = "taken";     // to the new peer: this name is mine (answer to hello)
    public const string Join = "join";       // to the group:  I have joined
    public const string Alive = "alive";     // to the group:  every 5 s, I am still here
    public const string Bye = "bye";         // to the group:  I leave (/quit)
    public const string Chat = "chat";       // to the group:  a message of the general channel
    public const string Private = "private"; // to one peer:   a private message
    public const string Ack = "ack";         // to one peer:   your private message with this seq arrived

    public static readonly HashSet<string> All = [Hello, Here, Taken, Join, Alive, Bye, Chat, Private, Ack];
}
