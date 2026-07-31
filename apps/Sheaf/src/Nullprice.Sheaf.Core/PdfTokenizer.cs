namespace Nullprice.Sheaf.Core;

public enum PdfTokenKind
{
    Number, Name, StringLiteral, HexString, ArrayOpen, ArrayClose,
    DictOpen, DictClose, Keyword, EndOfInput,
}

public readonly record struct PdfToken(PdfTokenKind Kind, string Text, byte[]? Bytes, double Number, bool NumberIsInteger, long Position);

/// <summary>Byte-level lexer for PDF syntax (ISO 32000-1 §7.2). Operates directly on a whole
/// in-memory buffer with a seekable <see cref="Position"/> rather than a forward-only stream,
/// because that is how PDF itself is structured: every object is found by byte offset via the
/// cross-reference table, not by reading the file start to end.</summary>
public sealed class PdfTokenizer(byte[] buffer, long start = 0)
{
    private readonly byte[] _buf = buffer;
    private long _pos = start;

    public long Position { get => _pos; set => _pos = value; }

    public PdfToken Next()
    {
        SkipWhitespaceAndComments();
        if (_pos >= _buf.Length) return new PdfToken(PdfTokenKind.EndOfInput, "", null, 0, true, _pos);

        var start0 = _pos;
        var c = _buf[_pos];

        if (c == (byte)'/') return ReadName();
        if (c == (byte)'(') return ReadLiteralString();
        if (c == (byte)'<')
        {
            if (_pos + 1 < _buf.Length && _buf[_pos + 1] == (byte)'<')
            {
                _pos += 2;
                return new PdfToken(PdfTokenKind.DictOpen, "<<", null, 0, true, start0);
            }
            return ReadHexString();
        }
        if (c == (byte)'>')
        {
            if (_pos + 1 < _buf.Length && _buf[_pos + 1] == (byte)'>')
            {
                _pos += 2;
                return new PdfToken(PdfTokenKind.DictClose, ">>", null, 0, true, start0);
            }
            _pos++; // stray '>' shouldn't occur in valid input; skip rather than loop forever
            return Next();
        }
        if (c == (byte)'[') { _pos++; return new PdfToken(PdfTokenKind.ArrayOpen, "[", null, 0, true, start0); }
        if (c == (byte)']') { _pos++; return new PdfToken(PdfTokenKind.ArrayClose, "]", null, 0, true, start0); }
        if (c == (byte)'{' || c == (byte)'}') { _pos++; return Next(); } // PostScript calculator syntax: not evaluated

        if (IsNumberStart(c)) return ReadNumber();

        return ReadKeyword();
    }

    public PdfToken Peek()
    {
        var save = _pos;
        var t = Next();
        _pos = save;
        return t;
    }

    private static bool IsWhitespace(byte b) => b is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    private static bool IsDelimiter(byte b) =>
        b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static bool IsNumberStart(byte b) => b is (byte)'+' or (byte)'-' or (byte)'.' || (b >= (byte)'0' && b <= (byte)'9');

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _buf.Length)
        {
            var b = _buf[_pos];
            if (IsWhitespace(b)) { _pos++; continue; }
            if (b == (byte)'%')
            {
                while (_pos < _buf.Length && _buf[_pos] != 0x0A && _buf[_pos] != 0x0D) _pos++;
                continue;
            }
            break;
        }
    }

    private PdfToken ReadName()
    {
        var start0 = _pos;
        _pos++; // skip '/'
        var bytes = new List<byte>();
        while (_pos < _buf.Length)
        {
            var b = _buf[_pos];
            if (IsWhitespace(b) || IsDelimiter(b)) break;
            if (b == (byte)'#' && _pos + 2 < _buf.Length && IsHexDigit(_buf[_pos + 1]) && IsHexDigit(_buf[_pos + 2]))
            {
                bytes.Add((byte)((HexVal(_buf[_pos + 1]) << 4) | HexVal(_buf[_pos + 2])));
                _pos += 3;
                continue;
            }
            bytes.Add(b);
            _pos++;
        }
        return new PdfToken(PdfTokenKind.Name, System.Text.Encoding.Latin1.GetString(bytes.ToArray()), bytes.ToArray(), 0, true, start0);
    }

    private PdfToken ReadLiteralString()
    {
        var start0 = _pos;
        _pos++; // skip '('
        var bytes = new List<byte>();
        var depth = 1;
        while (_pos < _buf.Length && depth > 0)
        {
            var b = _buf[_pos];
            if (b == (byte)'\\')
            {
                _pos++;
                if (_pos >= _buf.Length) break;
                var e = _buf[_pos];
                switch (e)
                {
                    case (byte)'n': bytes.Add(0x0A); _pos++; break;
                    case (byte)'r': bytes.Add(0x0D); _pos++; break;
                    case (byte)'t': bytes.Add(0x09); _pos++; break;
                    case (byte)'b': bytes.Add(0x08); _pos++; break;
                    case (byte)'f': bytes.Add(0x0C); _pos++; break;
                    case (byte)'(': bytes.Add((byte)'('); _pos++; break;
                    case (byte)')': bytes.Add((byte)')'); _pos++; break;
                    case (byte)'\\': bytes.Add((byte)'\\'); _pos++; break;
                    case 0x0D:
                        _pos++;
                        if (_pos < _buf.Length && _buf[_pos] == 0x0A) _pos++;
                        break; // backslash-newline is a line continuation: produces nothing
                    case 0x0A:
                        _pos++;
                        break;
                    default:
                        if (e is >= (byte)'0' and <= (byte)'7')
                        {
                            var val = 0;
                            var n = 0;
                            while (n < 3 && _pos < _buf.Length && _buf[_pos] is >= (byte)'0' and <= (byte)'7')
                            {
                                val = (val << 3) | (_buf[_pos] - (byte)'0');
                                _pos++; n++;
                            }
                            bytes.Add((byte)(val & 0xFF));
                        }
                        else
                        {
                            bytes.Add(e); // an unrecognized escape keeps the literal character (spec-permitted)
                            _pos++;
                        }
                        break;
                }
                continue;
            }
            if (b == (byte)'(') { depth++; bytes.Add(b); _pos++; continue; }
            if (b == (byte)')') { depth--; _pos++; if (depth > 0) bytes.Add(b); continue; }
            bytes.Add(b);
            _pos++;
        }
        return new PdfToken(PdfTokenKind.StringLiteral, "", bytes.ToArray(), 0, true, start0);
    }

    private PdfToken ReadHexString()
    {
        var start0 = _pos;
        _pos++; // skip '<'
        var digits = new List<byte>();
        while (_pos < _buf.Length && _buf[_pos] != (byte)'>')
        {
            var b = _buf[_pos];
            if (IsHexDigit(b)) digits.Add(b);
            _pos++;
        }
        if (_pos < _buf.Length) _pos++; // skip '>'
        if (digits.Count % 2 == 1) digits.Add((byte)'0');
        var bytes = new byte[digits.Count / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)((HexVal(digits[2 * i]) << 4) | HexVal(digits[2 * i + 1]));
        return new PdfToken(PdfTokenKind.HexString, "", bytes, 0, true, start0);
    }

    private PdfToken ReadNumber()
    {
        var start0 = _pos;
        var sb = new System.Text.StringBuilder();
        var isInteger = true;
        while (_pos < _buf.Length)
        {
            var b = _buf[_pos];
            if (b is (byte)'+' or (byte)'-' or (byte)'0' or (byte)'1' or (byte)'2' or (byte)'3' or (byte)'4'
                or (byte)'5' or (byte)'6' or (byte)'7' or (byte)'8' or (byte)'9')
            {
                sb.Append((char)b); _pos++;
            }
            else if (b == (byte)'.')
            {
                isInteger = false;
                sb.Append((char)b); _pos++;
            }
            else break;
        }
        _ = double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value);
        return new PdfToken(PdfTokenKind.Number, sb.ToString(), null, value, isInteger, start0);
    }

    private PdfToken ReadKeyword()
    {
        var start0 = _pos;
        var sb = new System.Text.StringBuilder();
        while (_pos < _buf.Length)
        {
            var b = _buf[_pos];
            if (IsWhitespace(b) || IsDelimiter(b)) break;
            sb.Append((char)b);
            _pos++;
        }
        if (sb.Length == 0) { _pos++; return Next(); } // shouldn't be reachable given the guards above; defensive only
        return new PdfToken(PdfTokenKind.Keyword, sb.ToString(), null, 0, true, start0);
    }

    private static bool IsHexDigit(byte b) => (b is >= (byte)'0' and <= (byte)'9') || (b is >= (byte)'a' and <= (byte)'f') || (b is >= (byte)'A' and <= (byte)'F');

    private static int HexVal(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
        _ => 0,
    };
}
