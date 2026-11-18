using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Labs.Lab3.Chat;

/// One datagram of the chat: one JSON object in UTF-8, for example
///   {"proto":"lanchat/1","type":"chat","from":"Ana","id":"5f0c9a1e7b2d4c33","seq":4,"text":"hello"}
/// UDP keeps the boundaries of datagrams, so one datagram is always exactly one message:
/// no delimiter and no length prefix are needed.
public sealed record Message(string Type, string From, string Id)
{
    public long Seq { get; init; }       // chat, private, ack: numbers the messages of one peer, from 1
    public string? To { get; init; }     // private: the name of the receiver
    public string? Text { get; init; }   // chat, and private when the chat has no passphrase
    public string? Nonce { get; init; }  // private with a passphrase: 12 bytes, Base64
    public string? Data { get; init; }   // private with a passphrase: ciphertext + 16-byte tag, Base64

    // Non-ASCII letters (ă, ș, д) stay as UTF-8 instead of ă: shorter datagrams.
    // Quotes, backslashes and control characters are still escaped, so the JSON stays valid.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public byte[] Encode()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("proto", Protocol.Version);
            json.WriteString("type", Type);
            json.WriteString("from", From);
            json.WriteString("id", Id);
            if (Seq > 0)
                json.WriteNumber("seq", Seq);
            if (To != null)
                json.WriteString("to", To);
            if (Text != null)
                json.WriteString("text", Text);
            if (Nonce != null)
                json.WriteString("nonce", Nonce);
            if (Data != null)
                json.WriteString("data", Data);
            json.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// The message in the datagram, or null with the reason why it is not a valid message of the chat.
    /// Never throws: a datagram from another program or random bytes only make it return null.
    public static Message? Decode(byte[] datagram, int length, out string error)
    {
        if (!Utf8.IsValid(datagram.AsSpan(0, length)))
        {
            error = "not UTF-8 text";
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(datagram.AsMemory(0, length));
            return Check(document.RootElement, out error);
        }
        catch (JsonException)
        {
            error = "not JSON";
            return null;
        }
    }

    private static Message? Check(JsonElement root, out string error)
    {
        error = "";
        if (root.ValueKind != JsonValueKind.Object)
            return Invalid("not a JSON object", out error);
        if (String(root, "proto") != Protocol.Version)
            return Invalid($"not a {Protocol.Version} message", out error);

        string? type = String(root, "type");
        if (type == null || !Kind.All.Contains(type))
            return Invalid("unknown type", out error);
        string? from = String(root, "from");
        if (!Protocol.IsValidName(from))
            return Invalid("invalid name in 'from'", out error);
        string? id = String(root, "id");
        if (!Protocol.IsValidId(id))
            return Invalid("invalid 'id'", out error);

        var message = new Message(type, from!, id!);
        if (type is Kind.Chat or Kind.Private or Kind.Ack)
        {
            if (!root.TryGetProperty("seq", out var seq) || seq.ValueKind != JsonValueKind.Number
                || !seq.TryGetInt64(out long number) || number < 1)
                return Invalid("missing or invalid 'seq'", out error);
            message = message with { Seq = number };
        }

        if (type == Kind.Chat)
        {
            string? text = String(root, "text");
            if (text is not { Length: >= 1 and <= Protocol.MaxText })
                return Invalid($"text missing or longer than {Protocol.MaxText} characters", out error);
            message = message with { Text = text };
        }
        else if (type == Kind.Private)
        {
            string? to = String(root, "to");
            if (!Protocol.IsValidName(to))
                return Invalid("invalid name in 'to'", out error);
            message = message with { To = to };

            string? text = String(root, "text");
            string? nonce = String(root, "nonce");
            string? data = String(root, "data");
            if (text != null)
            {
                if (text.Length is < 1 or > Protocol.MaxText)
                    return Invalid($"text empty or longer than {Protocol.MaxText} characters", out error);
                message = message with { Text = text };
            }
            else if (nonce != null && data != null)
            {
                if (Base64Length(nonce) != PrivateCipher.NonceSize || Base64Length(data) < PrivateCipher.TagSize)
                    return Invalid("invalid 'nonce' or 'data'", out error);
                message = message with { Nonce = nonce, Data = data };
            }
            else
            {
                return Invalid("private message without 'text' or 'nonce' and 'data'", out error);
            }
        }
        return message;
    }

    private static Message? Invalid(string reason, out string error)
    {
        error = reason;
        return null;
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// Number of bytes in a Base64 text, or -1 when it is not Base64.
    private static int Base64Length(string text)
    {
        var bytes = new byte[text.Length];
        return Convert.TryFromBase64String(text, bytes, out int length) ? length : -1;
    }
}
