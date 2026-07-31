namespace Nullprice.Sheaf.Core;

/// <summary>One operator plus the operands that preceded it, in source order (ISO 32000-1
/// §7.8.2 — content streams write operands before their operator, e.g. "1 0 0 1 0 0 cm").</summary>
public sealed record ContentOp(string Operator, IReadOnlyList<PdfObject> Operands);

/// <summary>Reads a decoded content stream into a flat list of operator/operand tuples.
/// Content streams share PDF's general value syntax, so this reuses
/// <see cref="PdfTokenizer"/>/<see cref="PdfObjectParser"/> for operands and only adds the
/// operator-keyword framing on top.</summary>
public static class ContentStreamReader
{
    public static List<ContentOp> Read(byte[] bytes)
    {
        var ops = new List<ContentOp>();
        var tok = new PdfTokenizer(bytes);
        var objParser = new PdfObjectParser(tok);
        var operands = new List<PdfObject>();

        while (true)
        {
            var save = tok.Position;
            var t = tok.Next();
            if (t.Kind == PdfTokenKind.EndOfInput) break;

            if (t.Kind is PdfTokenKind.Number or PdfTokenKind.Name or PdfTokenKind.StringLiteral
                or PdfTokenKind.HexString or PdfTokenKind.ArrayOpen or PdfTokenKind.DictOpen)
            {
                tok.Position = save;
                operands.Add(objParser.ParseValue());
                continue;
            }

            if (t.Kind != PdfTokenKind.Keyword) continue; // stray delimiter; drop and resync

            if (t.Text is "true" or "false" or "null")
            {
                tok.Position = save;
                operands.Add(objParser.ParseValue());
                continue;
            }

            if (t.Text == "BI")
            {
                SkipInlineImage(bytes, tok);
                operands = [];
                continue;
            }

            ops.Add(new ContentOp(t.Text, operands));
            operands = [];
        }

        return ops;
    }

    /// <summary>Inline images ("BI ... ID &lt;raw bytes&gt; EI") are out of scope for v1's
    /// redaction and text editing — neither looks for anything that isn't a <c>Do</c>-drawn
    /// XObject — so this only needs to skip past the raw data cleanly enough for the
    /// tokenizer to resynchronize on whatever follows.</summary>
    private static void SkipInlineImage(byte[] bytes, PdfTokenizer tok)
    {
        var marker = System.Text.Encoding.Latin1.GetBytes("EI");
        var pos = tok.Position;
        while (pos < bytes.Length - 1)
        {
            if (bytes[pos] == marker[0] && bytes[pos + 1] == marker[1] &&
                (pos + 2 >= bytes.Length || bytes[pos + 2] is 0x20 or 0x0A or 0x0D))
            {
                tok.Position = pos + 2;
                return;
            }
            pos++;
        }
        tok.Position = bytes.Length;
    }
}
