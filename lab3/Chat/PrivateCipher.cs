using System.Security.Cryptography;
using System.Text;

namespace Labs.Lab3.Chat;

/// Task 4: the key of the chat and the encryption of the private messages.
///
///   key     PBKDF2 with HMAC-SHA256 (passphrase, salt "lanchat/1 port <port>", 600 000 iterations): 32 bytes
///   cipher  AES-256-GCM: encrypts the text and computes a 16-byte tag over it
///   nonce   12 random bytes, new for every message
///   checked "lanchat/1|<from>|<id>|<to>|<seq>" as associated data: not secret, not sent twice, but
///           covered by the tag, so a message cannot be given another sender, receiver or number
///
/// All the peers derive the same key from the same passphrase, without exchanging anything.
public sealed class PrivateCipher
{
    public const int Iterations = 600_000; // what OWASP recommends for PBKDF2-HMAC-SHA256 (2023)
    public const int KeySize = 32;          // AES-256
    public const int NonceSize = 12;        // the size GCM is designed for
    public const int TagSize = 16;

    private readonly byte[] _key;

    private PrivateCipher(byte[] key) => _key = key;

    /// Slow on purpose (about a third of a second): every guess of an attacker costs the same.
    public static PrivateCipher FromPassphrase(string passphrase, int port)
    {
        // The salt must be the same for every peer, because nothing is exchanged. Making it depend
        // on the chat (the port) at least gives two chats with the same passphrase different keys.
        byte[] salt = Encoding.UTF8.GetBytes($"{Protocol.Version} port {port}");
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Iterations,
            HashAlgorithmName.SHA256, KeySize);
        return new PrivateCipher(key);
    }

    /// The private message with Nonce and Data set: the encrypted text and its tag.
    public Message Encrypt(Message message, string text)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize); // never the same twice (2^96 values)
        byte[] plain = Encoding.UTF8.GetBytes(text);
        byte[] data = new byte[plain.Length + TagSize]; // the ciphertext, then the tag

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), AssociatedData(message));
        return message with { Nonce = Convert.ToBase64String(nonce), Data = Convert.ToBase64String(data) };
    }

    /// The text, or null when the tag does not match: another passphrase, or a changed datagram.
    public string? Decrypt(Message message)
    {
        byte[] nonce = Convert.FromBase64String(message.Nonce!);
        byte[] data = Convert.FromBase64String(message.Data!);
        byte[] plain = new byte[data.Length - TagSize];
        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), plain, AssociatedData(message));
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(plain);
        }
        catch (CryptographicException)
        {
            // AuthenticationTagMismatchException: nothing of the text is returned, not even a part
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null; // a correct tag, but the sender encrypted bytes that are not text
        }
    }

    private static byte[] AssociatedData(Message m) =>
        Encoding.UTF8.GetBytes($"{Protocol.Version}|{m.From}|{m.Id}|{m.To}|{m.Seq}");
}
