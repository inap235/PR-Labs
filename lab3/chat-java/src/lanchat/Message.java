package lanchat;

import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.Base64;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;

/// One datagram of the chat: one JSON object in UTF-8, exactly as in lab3/PROTOCOL.md, for example
///   {"proto":"lanchat/1","type":"chat","from":"Ana","id":"5f0c9a1e7b2d4c33","seq":4,"text":"hello"}
/// seq is 0 when the kind has none; to, text, nonce and data are null when absent.
record Message(String type, String from, String id, long seq, String to, String text, String nonce, String data) {

    static final String PROTO = "lanchat/1";
    static final int MAX_TEXT = 500;
    static final int MAX_NAME = 16;

    static final String HELLO = "hello", HERE = "here", TAKEN = "taken", JOIN = "join", ALIVE = "alive",
            BYE = "bye", CHAT = "chat", PRIVATE = "private", ACK = "ack";
    static final Set<String> KINDS = Set.of(HELLO, HERE, TAKEN, JOIN, ALIVE, BYE, CHAT, PRIVATE, ACK);

    /// Thrown by decode, with the reason why the datagram is not a valid message.
    static final class Invalid extends Exception {
        Invalid(String reason) {
            super(reason);
        }
    }

    static Message of(String type, String from, String id) {
        return new Message(type, from, id, 0, null, null, null, null);
    }

    Message withSeq(long s) {
        return new Message(type, from, id, s, to, text, nonce, data);
    }

    Message withTo(String t) {
        return new Message(type, from, id, seq, t, text, nonce, data);
    }

    Message withText(String t) {
        return new Message(type, from, id, seq, to, t, nonce, data);
    }

    Message withEncrypted(String n, String d) {
        return new Message(type, from, id, seq, to, null, n, d);
    }

    byte[] encode() {
        Map<String, Object> json = new LinkedHashMap<>();
        json.put("proto", PROTO);
        json.put("type", type);
        json.put("from", from);
        json.put("id", id);
        if (seq > 0)
            json.put("seq", seq);
        if (to != null)
            json.put("to", to);
        if (text != null)
            json.put("text", text);
        if (nonce != null)
            json.put("nonce", nonce);
        if (data != null)
            json.put("data", data);
        return Json.write(json).getBytes(StandardCharsets.UTF_8);
    }

    /// The message in the datagram. Never anything but Invalid for bytes that are not one.
    static Message decode(byte[] datagram, int length) throws Invalid {
        String text;
        try {
            text = StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT)
                    .decode(ByteBuffer.wrap(datagram, 0, length)).toString();
        } catch (CharacterCodingException e) {
            throw new Invalid("not UTF-8 text");
        }
        Object parsed;
        try {
            parsed = Json.parse(text);
        } catch (IllegalArgumentException e) {
            throw new Invalid("not JSON");
        }
        if (!(parsed instanceof Map<?, ?> root))
            throw new Invalid("not a JSON object");
        if (!PROTO.equals(root.get("proto")))
            throw new Invalid("not a " + PROTO + " message");

        String type = string(root, "type");
        if (type == null || !KINDS.contains(type))
            throw new Invalid("unknown type");
        String from = string(root, "from");
        if (!isValidName(from))
            throw new Invalid("invalid name in 'from'");
        String id = string(root, "id");
        if (!isValidId(id))
            throw new Invalid("invalid 'id'");
        Message message = of(type, from, id);

        if (type.equals(CHAT) || type.equals(PRIVATE) || type.equals(ACK)) {
            if (!(root.get("seq") instanceof Long seq) || seq < 1)
                throw new Invalid("missing or invalid 'seq'");
            message = message.withSeq(seq);
        }
        if (type.equals(CHAT)) {
            String t = string(root, "text");
            if (t == null || t.isEmpty() || t.length() > MAX_TEXT)
                throw new Invalid("text missing or longer than " + MAX_TEXT + " characters");
            message = message.withText(t);
        } else if (type.equals(PRIVATE)) {
            String to = string(root, "to");
            if (!isValidName(to))
                throw new Invalid("invalid name in 'to'");
            message = message.withTo(to);
            String t = string(root, "text");
            String nonce = string(root, "nonce");
            String data = string(root, "data");
            if (t != null) {
                if (t.isEmpty() || t.length() > MAX_TEXT)
                    throw new Invalid("text empty or longer than " + MAX_TEXT + " characters");
                message = message.withText(t);
            } else if (nonce != null && data != null) {
                if (base64Length(nonce) != PrivateCipher.NONCE_SIZE || base64Length(data) < PrivateCipher.TAG_SIZE)
                    throw new Invalid("invalid 'nonce' or 'data'");
                message = message.withEncrypted(nonce, data);
            } else {
                throw new Invalid("private message without 'text' or 'nonce' and 'data'");
            }
        }
        return message;
    }

    /// 1 to 16 letters, digits, '_' or '-'.
    static boolean isValidName(String name) {
        if (name == null || name.isEmpty() || name.length() > MAX_NAME)
            return false;
        for (char c : name.toCharArray()) {
            if (!Character.isLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }
        return true;
    }

    /// 16 lowercase hex digits.
    static boolean isValidId(String id) {
        return id != null && id.matches("[0-9a-f]{16}");
    }

    private static String string(Map<?, ?> root, String name) {
        return root.get(name) instanceof String s ? s : null;
    }

    private static int base64Length(String text) {
        try {
            return Base64.getDecoder().decode(text).length;
        } catch (IllegalArgumentException e) {
            return -1;
        }
    }
}
