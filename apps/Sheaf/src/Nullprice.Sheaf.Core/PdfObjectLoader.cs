namespace Nullprice.Sheaf.Core;

/// <summary>Loads and memoizes indirect objects by file offset (or, for compressed objects,
/// by which object stream holds them). Understands "N G obj ... stream ... endstream
/// ... endobj" extraction, including resolving an indirect <c>/Length</c> by loading that
/// object first — the two are interdependent, since a stream can't be sliced out of the file
/// until its length is known, and the length might itself be a separate indirect object.</summary>
internal sealed class PdfObjectLoader(
    byte[] bytes,
    Dictionary<(int Number, int Generation), long> offsets,
    Dictionary<(int Number, int Generation), (int StreamNumber, int Index)> compressed,
    PdfObjectTable table)
{
    private readonly HashSet<(int, int)> _loading = [];

    public void LoadAll()
    {
        foreach (var key in offsets.Keys.ToList()) Load(key.Number, key.Generation);
        foreach (var key in compressed.Keys.ToList()) Load(key.Number, key.Generation);
    }

    public PdfObject Load(int number, int generation)
    {
        if (table.TryGet(number, generation, out var existing)) return existing;
        if (!_loading.Add((number, generation))) return PdfNull.Instance; // reference cycle guard

        var result = PdfNull.Instance as PdfObject;
        try
        {
            if (offsets.TryGetValue((number, generation), out var offset))
            {
                result = ParseIndirectObjectAt(offset);
            }
            else if (compressed.TryGetValue((number, generation), out var loc))
            {
                result = LoadFromObjectStream(loc.StreamNumber, loc.Index);
            }
        }
        catch
        {
            result = PdfNull.Instance;
        }

        table.Set(number, generation, result);
        _loading.Remove((number, generation));
        return result;
    }

    public PdfObject ParseIndirectObjectAt(long offset)
    {
        var tok = new PdfTokenizer(bytes, offset);
        _ = tok.Next(); // object number
        _ = tok.Next(); // generation
        var objTok = tok.Next();
        if (objTok.Kind != PdfTokenKind.Keyword || objTok.Text != "obj")
        {
            // The offset didn't land cleanly on "N G obj" — a stale entry from a rebuilt or
            // malformed xref table. Give up on this one object rather than misparsing
            // whatever byte we happened to land on.
            return PdfNull.Instance;
        }

        var objParser = new PdfObjectParser(tok);
        var value = objParser.ParseValue();

        var afterValue = tok.Position;
        var next = tok.Next();
        if (next.Kind == PdfTokenKind.Keyword && next.Text == "stream" && value is PdfDictionary dict)
        {
            var dataStart = tok.Position;
            if (dataStart < bytes.Length && bytes[dataStart] == 0x0D) dataStart++;
            if (dataStart < bytes.Length && bytes[dataStart] == 0x0A) dataStart++;

            var length = ResolveLength(dict);
            byte[] raw = length is >= 0 && dataStart + length.Value <= bytes.Length
                ? bytes[(int)dataStart..(int)(dataStart + length.Value)]
                : ScanForEndstream(dataStart);

            return new PdfStream(dict, raw);
        }

        tok.Position = afterValue;
        return value;
    }

    private long? ResolveLength(PdfDictionary dict)
    {
        return dict.Get("Length") switch
        {
            PdfNumber n => (long)n.Value,
            PdfReference r when Load(r.Number, r.Generation) is PdfNumber rn => (long)rn.Value,
            _ => null,
        };
    }

    /// <summary>Fallback for when /Length can't be resolved (missing, or an indirect
    /// reference this loader can't reach yet): scan forward for the literal "endstream"
    /// keyword. Binary stream data could in principle contain that byte sequence by chance,
    /// but that is rare enough to accept as a resilience trade-off over refusing the object
    /// outright.</summary>
    private byte[] ScanForEndstream(long dataStart)
    {
        var marker = System.Text.Encoding.Latin1.GetBytes("endstream");
        for (var i = dataStart; i <= bytes.Length - marker.Length; i++)
        {
            var match = true;
            for (var j = 0; j < marker.Length; j++)
            {
                if (bytes[i + j] != marker[j]) { match = false; break; }
            }
            if (!match) continue;

            var end = i;
            if (end > dataStart && bytes[end - 1] == 0x0A) end--;
            if (end > dataStart && bytes[end - 1] == 0x0D) end--;
            return bytes[(int)dataStart..(int)end];
        }
        return bytes[(int)dataStart..];
    }

    private PdfObject LoadFromObjectStream(int streamNumber, int index)
    {
        if (Load(streamNumber, 0) is not PdfStream stm) return PdfNull.Instance;

        var decoded = FilterCodec.Decode(stm.Dictionary, stm.RawBytes, table);
        var n = (stm.Dictionary.Get("N") as PdfNumber)?.AsInt ?? 0;
        var first = (stm.Dictionary.Get("First") as PdfNumber)?.AsInt ?? 0;

        var headerTok = new PdfTokenizer(decoded, 0);
        var pairs = new List<(int Num, int Offset)>();
        for (var i = 0; i < n; i++)
        {
            var numTok = headerTok.Next();
            var offTok = headerTok.Next();
            pairs.Add(((int)numTok.Number, (int)offTok.Number));
        }

        if (index < 0 || index >= pairs.Count) return PdfNull.Instance;
        var bodyTok = new PdfTokenizer(decoded, first + pairs[index].Offset);
        var bodyParser = new PdfObjectParser(bodyTok);
        return bodyParser.ParseValue();
    }
}
