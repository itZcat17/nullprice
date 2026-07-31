namespace Nullprice.Sheaf.Core;

/// <summary>Parses a single PDF value (number, name, string, array, dictionary, or indirect
/// reference) from a <see cref="PdfTokenizer"/>. Shared by object parsing and content-stream
/// operand parsing, since both use the same value syntax.</summary>
public sealed class PdfObjectParser(PdfTokenizer tokenizer)
{
    public PdfObject ParseValue()
    {
        var t = tokenizer.Next();
        return t.Kind switch
        {
            PdfTokenKind.Number => TryParseReference(t) ?? new PdfNumber(t.Number, t.NumberIsInteger),
            PdfTokenKind.Name => new PdfName(t.Text),
            PdfTokenKind.StringLiteral => new PdfString(t.Bytes!, WasHex: false),
            PdfTokenKind.HexString => new PdfString(t.Bytes!, WasHex: true),
            PdfTokenKind.ArrayOpen => ParseArray(),
            PdfTokenKind.DictOpen => ParseDictionary(),
            PdfTokenKind.Keyword when t.Text == "true" => new PdfBoolean(true),
            PdfTokenKind.Keyword when t.Text == "false" => new PdfBoolean(false),
            PdfTokenKind.Keyword when t.Text == "null" => PdfNull.Instance,
            // Anything else (a stray/unrecognized keyword) degrades to null for this one value
            // rather than throwing and losing the rest of an otherwise-readable object.
            _ => PdfNull.Instance,
        };
    }

    /// <summary>PDF spells an indirect reference as three tokens, "N G R". A bare integer is
    /// far more common, so this speculatively reads ahead and rewinds if the next two tokens
    /// don't complete the pattern — <see cref="PdfTokenizer.Position"/> makes that rewind cheap.</summary>
    private PdfObject? TryParseReference(PdfToken firstNumber)
    {
        if (!firstNumber.NumberIsInteger || firstNumber.Number < 0) return null;
        var afterFirst = tokenizer.Position;

        var second = tokenizer.Next();
        if (second.Kind == PdfTokenKind.Number && second.NumberIsInteger && second.Number >= 0)
        {
            var third = tokenizer.Next();
            if (third.Kind == PdfTokenKind.Keyword && third.Text == "R")
            {
                return new PdfReference((int)firstNumber.Number, (int)second.Number);
            }
        }

        tokenizer.Position = afterFirst;
        return null;
    }

    private PdfArray ParseArray()
    {
        var items = new List<PdfObject>();
        while (true)
        {
            var save = tokenizer.Position;
            var peek = tokenizer.Next();
            if (peek.Kind is PdfTokenKind.ArrayClose or PdfTokenKind.EndOfInput) break;
            tokenizer.Position = save;
            items.Add(ParseValue());
        }
        return new PdfArray(items);
    }

    private PdfDictionary ParseDictionary()
    {
        var entries = new Dictionary<string, PdfObject>();
        while (true)
        {
            var t = tokenizer.Next();
            if (t.Kind is PdfTokenKind.DictClose or PdfTokenKind.EndOfInput) break;
            if (t.Kind != PdfTokenKind.Name) continue; // skip malformed entries rather than aborting the dict
            entries[t.Text] = ParseValue();
        }
        return new PdfDictionary(entries);
    }
}
