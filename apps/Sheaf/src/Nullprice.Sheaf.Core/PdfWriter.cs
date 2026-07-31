namespace Nullprice.Sheaf.Core;

/// <summary>Serializes a <see cref="PdfDocument"/> to bytes as a fresh file: every indirect
/// object is renumbered from 1 and written with a brand-new classic cross-reference table.
/// There is no incremental-update ("append a new revision") support in v1 — a full rewrite is
/// simpler, and it's also what every operation Sheaf supports (merge, split, redact,
/// recompress, decrypt) naturally wants: a clean standalone output file, not a patch on top of
/// the input.</summary>
public static class PdfWriter
{
    public static byte[] Write(PdfDocument document)
    {
        var renumber = new Dictionary<(int, int), int>();
        var next = 1;
        foreach (var key in document.Objects.All.Keys.OrderBy(k => k.Number).ThenBy(k => k.Generation))
        {
            renumber[key] = next++;
        }

        using var buffer = new MemoryStream();
        var offsets = new long[next];

        void WriteText(string s)
        {
            var b = System.Text.Encoding.Latin1.GetBytes(s);
            buffer.Write(b, 0, b.Length);
        }
        void WriteRaw(byte[] b) => buffer.Write(b, 0, b.Length);

        WriteText("%PDF-1.7\n%âãÏÓ\n");

        foreach (var (key, newNum) in renumber.OrderBy(kv => kv.Value))
        {
            offsets[newNum] = buffer.Position;
            WriteText($"{newNum} 0 obj\n");
            WriteObject(document.Objects.All[key], renumber, WriteText, WriteRaw);
            WriteText("\nendobj\n");
        }

        var xrefOffset = buffer.Position;
        WriteText("xref\n0 " + next + "\n");
        WriteText("0000000000 65535 f \n");
        for (var i = 1; i < next; i++)
            WriteText($"{offsets[i]:D10} 00000 n \n");

        // WriteObject already remaps every PdfReference it writes (see the PdfReference case
        // below), so the trailer's Root is passed through in its original, pre-renumber form
        // — remapping it here too would double-apply renumber and point at the wrong object.
        var trailerEntries = new Dictionary<string, PdfObject> { ["Size"] = new PdfNumber(next) };
        if (document.Trailer.Get("Root") is { } root) trailerEntries["Root"] = root;

        WriteText("trailer\n");
        WriteObject(new PdfDictionary(trailerEntries), renumber, WriteText, WriteRaw);
        WriteText("\nstartxref\n" + xrefOffset + "\n%%EOF");

        return buffer.ToArray();
    }

    private static void WriteObject(PdfObject obj, Dictionary<(int, int), int> renumber, Action<string> writeText, Action<byte[]> writeRaw)
    {
        switch (obj)
        {
            case PdfNull:
                writeText("null");
                break;
            case PdfBoolean b:
                writeText(b.Value ? "true" : "false");
                break;
            case PdfNumber n:
                writeText(FormatNumber(n));
                break;
            case PdfName nm:
                writeText("/" + EscapeName(nm.Value));
                break;
            case PdfString s:
                writeText(FormatString(s));
                break;
            case PdfReference r:
                var mapped = renumber.TryGetValue((r.Number, r.Generation), out var num) ? num : 0;
                writeText($"{mapped} 0 R");
                break;
            case PdfArray a:
                writeText("[");
                for (var i = 0; i < a.Items.Count; i++)
                {
                    if (i > 0) writeText(" ");
                    WriteObject(a.Items[i], renumber, writeText, writeRaw);
                }
                writeText("]");
                break;
            case PdfDictionary d:
                writeText("<<");
                foreach (var (k, v) in d.Entries)
                {
                    writeText("/" + EscapeName(k) + " ");
                    WriteObject(v, renumber, writeText, writeRaw);
                }
                writeText(">>");
                break;
            case PdfStream stream:
                var dict = stream.Dictionary.With("Length", new PdfNumber(stream.RawBytes.Length));
                WriteObject(dict, renumber, writeText, writeRaw);
                writeText("\nstream\n");
                writeRaw(stream.RawBytes);
                writeText("\nendstream");
                break;
        }
    }

    private static string FormatNumber(PdfNumber n)
    {
        if (n.IsInteger || n.Value == Math.Floor(n.Value))
            return ((long)n.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return n.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string EscapeName(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in name)
        {
            if (ch is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%' or '#' || ch <= ' ' || ch > '~')
                sb.Append('#').Append(((int)ch).ToString("X2"));
            else
                sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string FormatString(PdfString s)
    {
        var sb = new System.Text.StringBuilder("(");
        foreach (var b in s.Bytes)
        {
            switch (b)
            {
                case (byte)'(': sb.Append("\\("); break;
                case (byte)')': sb.Append("\\)"); break;
                case (byte)'\\': sb.Append("\\\\"); break;
                case 0x0D: sb.Append("\\r"); break;
                case 0x0A: sb.Append("\\n"); break;
                default:
                    if (b < 0x20 || b > 0x7E) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    else sb.Append((char)b);
                    break;
            }
        }
        sb.Append(')');
        return sb.ToString();
    }
}
