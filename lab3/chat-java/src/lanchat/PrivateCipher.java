package lanchat;

import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.SecureRandom;
import java.util.Base64;
import javax.crypto.AEADBadTagException;
import javax.crypto.Cipher;
import javax.crypto.SecretKeyFactory;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.PBEKeySpec;
import javax.crypto.spec.SecretKeySpec;

/// Task 4, the same as the C# peer (lab3/Chat/PrivateCipher.cs), so that both derive the same key
/// and read each other's private messages:
///   key     PBKDF2WithHmacSHA256(passphrase, salt "lanchat/1 port <port>", 600 000 iterations): 32 bytes
///   cipher  AES-256-GCM, a new random 12-byte nonce for every message, 16-byte tag after the ciphertext
///   checked "lanchat/1|<from>|<id>|<to>|<seq>" as associated data
final class PrivateCipher {
    static final int ITERATIONS = 600_000;
    static final int NONCE_SIZE = 12;
    static final int TAG_SIZE = 16;

    private final SecretKeySpec key;
    private final SecureRandom random = new SecureRandom();

    PrivateCipher(String passphrase, int port) throws GeneralSecurityException {
        byte[] salt = (Message.PROTO + " port " + port).getBytes(StandardCharsets.UTF_8);
        // PBEKeySpec takes the passphrase as chars; this implementation turns them into UTF-8 bytes, like C#
        var spec = new PBEKeySpec(passphrase.toCharArray(), salt, ITERATIONS, 256);
        byte[] bytes = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).getEncoded();
        key = new SecretKeySpec(bytes, "AES");
    }

    Message encrypt(Message message, String text) throws GeneralSecurityException {
        byte[] nonce = new byte[NONCE_SIZE];
        random.nextBytes(nonce);
        Cipher aes = Cipher.getInstance("AES/GCM/NoPadding");
        aes.init(Cipher.ENCRYPT_MODE, key, new GCMParameterSpec(TAG_SIZE * 8, nonce));
        aes.updateAAD(associatedData(message));
        byte[] data = aes.doFinal(text.getBytes(StandardCharsets.UTF_8)); // ciphertext, then tag
        Base64.Encoder base64 = Base64.getEncoder();
        return message.withEncrypted(base64.encodeToString(nonce), base64.encodeToString(data));
    }

    /// The text, or null when the tag does not match: another passphrase, or a changed datagram.
    String decrypt(Message message) {
        try {
            byte[] nonce = Base64.getDecoder().decode(message.nonce());
            byte[] data = Base64.getDecoder().decode(message.data());
            Cipher aes = Cipher.getInstance("AES/GCM/NoPadding");
            aes.init(Cipher.DECRYPT_MODE, key, new GCMParameterSpec(TAG_SIZE * 8, nonce));
            aes.updateAAD(associatedData(message));
            byte[] plain = aes.doFinal(data);
            return StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(CodingErrorAction.REPORT)
                    .decode(ByteBuffer.wrap(plain)).toString();
        } catch (AEADBadTagException | CharacterCodingException e) {
            return null;
        } catch (GeneralSecurityException e) {
            return null;
        }
    }

    private static byte[] associatedData(Message m) {
        return (Message.PROTO + "|" + m.from() + "|" + m.id() + "|" + m.to() + "|" + m.seq()).getBytes(StandardCharsets.UTF_8);
    }
}
