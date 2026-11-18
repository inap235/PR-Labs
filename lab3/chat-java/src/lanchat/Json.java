package lanchat;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/// Just enough JSON for the datagrams of the chat (Java has no JSON in its standard library).
/// Writes a flat object of strings and numbers. Reads any JSON text, so that a datagram from
/// another program is recognised as not being a message of the chat, instead of crashing it:
/// objects become Maps, arrays Lists, numbers Long (or Double), true/false Boolean.
final class Json {
    private final String text;
    private int pos;

    private Json(String text) {
        this.text = text;
    }

    static String write(Map<String, Object> object) {
        StringBuilder out = new StringBuilder("{");
        for (Map.Entry<String, Object> entry : object.entrySet()) {
            if (out.length() > 1)
                out.append(',');
            string(out, entry.getKey());
            out.append(':');
            if (entry.getValue() instanceof String s)
                string(out, s);
            else
                out.append(entry.getValue());
        }
        return out.append('}').toString();
    }

    /// Quotes, backslashes and control characters are escaped; everything else (ă, д) stays as it is.
    private static void string(StringBuilder out, String s) {
        out.append('"');
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c == '"' || c == '\\')
                out.append('\\').append(c);
            else if (c < 0x20)
                out.append(String.format("\\u%04x", (int) c));
            else
                out.append(c);
        }
        out.append('"');
    }

    /// The value of a JSON text; IllegalArgumentException when it is not valid JSON.
    static Object parse(String text) {
        Json json = new Json(text);
        json.space();
        Object value = json.value(0);
        json.space();
        if (json.pos != text.length())
            throw json.error("text after the value");
        return value;
    }

    private Object value(int depth) {
        if (depth > 32)
            throw error("nested too deep");
        if (pos >= text.length())
            throw error("missing value");
        char c = text.charAt(pos);
        if (c == '{')
            return object(depth);
        if (c == '[')
            return array(depth);
        if (c == '"')
            return string();
        if (text.startsWith("true", pos)) {
            pos += 4;
            return Boolean.TRUE;
        }
        if (text.startsWith("false", pos)) {
            pos += 5;
            return Boolean.FALSE;
        }
        if (text.startsWith("null", pos)) {
            pos += 4;
            return null;
        }
        return number();
    }

    private Map<String, Object> object(int depth) {
        Map<String, Object> map = new LinkedHashMap<>();
        pos++; // {
        space();
        if (peek() == '}') {
            pos++;
            return map;
        }
        while (true) {
            space();
            if (peek() != '"')
                throw error("a name in quotes expected");
            String name = string();
            space();
            expect(':');
            space();
            map.put(name, value(depth + 1));
            space();
            if (peek() == ',') {
                pos++;
                continue;
            }
            expect('}');
            return map;
        }
    }

    private List<Object> array(int depth) {
        List<Object> list = new ArrayList<>();
        pos++; // [
        space();
        if (peek() == ']') {
            pos++;
            return list;
        }
        while (true) {
            space();
            list.add(value(depth + 1));
            space();
            if (peek() == ',') {
                pos++;
                continue;
            }
            expect(']');
            return list;
        }
    }

    private String string() {
        StringBuilder out = new StringBuilder();
        pos++; // opening quote
        while (true) {
            if (pos >= text.length())
                throw error("unterminated string");
            char c = text.charAt(pos++);
            if (c == '"')
                return out.toString();
            if (c < 0x20)
                throw error("control character in a string");
            if (c != '\\') {
                out.append(c);
                continue;
            }
            if (pos >= text.length())
                throw error("unterminated escape");
            char e = text.charAt(pos++);
            switch (e) {
                case '"', '\\', '/' -> out.append(e);
                case 'b' -> out.append('\b');
                case 'f' -> out.append('\f');
                case 'n' -> out.append('\n');
                case 'r' -> out.append('\r');
                case 't' -> out.append('\t');
                case 'u' -> {
                    if (pos + 4 > text.length())
                        throw error("short \\u escape");
                    try {
                        out.append((char) Integer.parseInt(text.substring(pos, pos + 4), 16));
                    } catch (NumberFormatException ex) {
                        throw error("bad \\u escape");
                    }
                    pos += 4;
                }
                default -> throw error("bad escape");
            }
        }
    }

    private Object number() {
        int start = pos;
        if (peek() == '-')
            pos++;
        while (pos < text.length() && "0123456789.eE+-".indexOf(text.charAt(pos)) >= 0)
            pos++;
        String number = text.substring(start, pos);
        if (!number.matches("-?(0|[1-9][0-9]*)(\\.[0-9]+)?([eE][+-]?[0-9]+)?"))
            throw error("not a value");
        try {
            return Long.parseLong(number);
        } catch (NumberFormatException e) {
            return Double.parseDouble(number); // a fraction, an exponent, or too big for a long
        }
    }

    private void space() {
        while (pos < text.length() && " \t\r\n".indexOf(text.charAt(pos)) >= 0)
            pos++;
    }

    private char peek() {
        return pos < text.length() ? text.charAt(pos) : '\0';
    }

    private void expect(char c) {
        if (peek() != c)
            throw error("'" + c + "' expected");
        pos++;
    }

    private IllegalArgumentException error(String what) {
        return new IllegalArgumentException(what + " at " + pos);
    }
}
