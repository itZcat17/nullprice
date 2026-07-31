namespace Nullprice.Sheaf.Core;

public enum PdfEncryptionStatus { NotEncrypted, Success, WrongPassword, Unsupported }

public sealed record ParsedPdf(PdfObjectTable Objects, PdfDictionary Trailer, PdfEncryptionStatus Encryption, string? EncryptionMessage = null);

/// <summary>
/// Parses PDF bytes into an object graph (ISO 32000-1 §7.5). Objects are read by byte offset
/// via the cross-reference table rather than sequentially, because that is how a PDF's own
/// structure works: the trailer points at the last xref section, each xref section points at
/// object offsets (or, for compressed objects, at an object stream), xref sections can chain
/// backwards through <c>/Prev</c> across incremental updates, and object streams are only
/// reachable once decompressed.
/// <para>
/// A file whose declared xref can't be parsed at all falls back to a linear scan for
/// "N G obj" markers to rebuild the object table — real-world PDFs frequently have a
/// truncated or corrupted xref section, and this is the difference between opening them and
/// refusing everything.
/// </para>
/// </summary>
public static class PdfParser
{
    public static ParsedPdf Parse(byte[] bytes, string? password = null)
    {
        var offsets = new Dictionary<(int, int), long>();
        var compressed = new Dictionary<(int, int), (int, int)>();
        PdfDictionary trailer;

        try
        {
            trailer = ParseXrefChain(bytes, offsets, compressed);
        }
        catch
        {
            trailer = PdfDictionary.Empty;
        }

        if (offsets.Count == 0 && compressed.Count == 0)
        {
            offsets = LinearScan(bytes);
            if (trailer.Entries.Count == 0) trailer = FindTrailerByScan(bytes) ?? PdfDictionary.Empty;
        }

        var objects = new PdfObjectTable();

        byte[]? fileKey = null;
        var useAes = false;
        (int Number, int Generation)? encryptObjectKey = null;

        if (trailer.Get("Encrypt") is PdfReference encryptRef)
        {
            encryptObjectKey = (encryptRef.Number, encryptRef.Generation);

            // Bootstrapped with no decrypt hook: the /Encrypt dictionary's own O/U strings are
            // always stored in plaintext (ISO 32000-1 §7.6.1) — they're key-derivation
            // material, not document content — so reading it needs no key yet.
            var bootstrapLoader = new PdfObjectLoader(bytes, offsets, compressed, objects);
            var encryptObj = bootstrapLoader.Load(encryptRef.Number, encryptRef.Generation);

            if (encryptObj is not PdfDictionary encryptDict)
            {
                return new ParsedPdf(objects, trailer, PdfEncryptionStatus.Unsupported, "Could not read this PDF's encryption dictionary.");
            }

            var id0 = (((trailer.Get("ID") as PdfArray)?.Items.FirstOrDefault()) as PdfString)?.Bytes ?? [];
            var (settings, unsupportedReason) = PdfSecuritySettings.FromEncryptDictionary(encryptDict, id0);

            if (settings is null)
            {
                return new ParsedPdf(objects, trailer, PdfEncryptionStatus.Unsupported, unsupportedReason);
            }

            if (!PdfSecurityHandler.TryComputeFileKey(settings, password, out var computedKey))
            {
                return new ParsedPdf(objects, trailer, PdfEncryptionStatus.WrongPassword, null);
            }

            fileKey = computedKey;
            useAes = settings.UseAes;
        }

        byte[] DecryptForObjStm(int num, int gen, byte[] raw) =>
            fileKey is null ? raw : PdfSecurityHandler.Decrypt(fileKey, num, gen, useAes, raw);

        var loader = fileKey is null
            ? new PdfObjectLoader(bytes, offsets, compressed, objects)
            : new PdfObjectLoader(bytes, offsets, compressed, objects, DecryptForObjStm);
        loader.LoadAll();

        if (fileKey is not null)
        {
            // Only directly-loaded objects need this — objects extracted from an object
            // stream were already decrypted (as a whole, before decompression) by
            // DecryptForObjStm above, and decrypting them again here would corrupt them.
            DecryptDirectObjects(objects, offsets.Keys, encryptObjectKey, fileKey, useAes);
        }

        if (trailer.Entries.Count == 0 || trailer.Get("Root") is null)
        {
            // A scanned/rebuilt table has no trailer telling us which object is the Catalog —
            // find the one object that looks like it (ISO 32000-1 §7.7.2: /Type /Catalog).
            var root = FindCatalog(objects);
            if (root is not null) trailer = trailer.With("Root", root);
        }

        var status = fileKey is not null ? PdfEncryptionStatus.Success : PdfEncryptionStatus.NotEncrypted;
        return new ParsedPdf(objects, trailer, status);
    }

    /// <summary>Decrypts every string and stream belonging to an object that was loaded
    /// directly by file offset — everything except the <c>/Encrypt</c> dictionary itself
    /// (never encrypted) and cross-reference streams (ISO 32000-1 §7.5.8.2 — never encrypted;
    /// harmless to skip specially since Sheaf always discards them anyway, being unreachable
    /// from Root and superseded by a fresh xref table on every write).</summary>
    private static void DecryptDirectObjects(
        PdfObjectTable objects, IEnumerable<(int Number, int Generation)> directKeys,
        (int Number, int Generation)? skipKey, byte[] fileKey, bool useAes)
    {
        foreach (var key in directKeys)
        {
            if (skipKey.HasValue && key == skipKey.Value) continue;
            if (!objects.TryGet(key.Number, key.Generation, out var value)) continue;

            objects.Set(key.Number, key.Generation, DecryptValue(value, key.Number, key.Generation, fileKey, useAes));
        }
    }

    private static PdfObject DecryptValue(PdfObject value, int num, int gen, byte[] fileKey, bool useAes) => value switch
    {
        PdfString s => new PdfString(PdfSecurityHandler.Decrypt(fileKey, num, gen, useAes, s.Bytes), s.WasHex),
        PdfArray a => new PdfArray(a.Items.Select(i => DecryptValue(i, num, gen, fileKey, useAes)).ToList()),
        PdfDictionary d => new PdfDictionary(d.Entries.ToDictionary(kv => kv.Key, kv => DecryptValue(kv.Value, num, gen, fileKey, useAes))),
        PdfStream s => new PdfStream((PdfDictionary)DecryptValue(s.Dictionary, num, gen, fileKey, useAes), PdfSecurityHandler.Decrypt(fileKey, num, gen, useAes, s.RawBytes)),
        _ => value,
    };

    // ---- cross-reference chain ---------------------------------------------

    private static PdfDictionary ParseXrefChain(byte[] bytes, Dictionary<(int, int), long> offsets, Dictionary<(int, int), (int, int)> compressed)
    {
        var merged = new Dictionary<string, PdfObject>();
        var visited = new HashSet<long>();
        long? pos = FindStartXref(bytes);

        while (pos is { } p && p >= 0 && p < bytes.Length && visited.Add(p))
        {
            var tok = new PdfTokenizer(bytes, p);
            var peek = tok.Peek();

            var sectionTrailer = peek.Kind == PdfTokenKind.Keyword && peek.Text == "xref"
                ? ParseClassicXrefSection(tok, offsets)
                : ParseXrefStreamSection(bytes, p, offsets, compressed);

            foreach (var (k, v) in sectionTrailer.Entries)
                merged.TryAdd(k, v); // earlier (more recent) sections win over older /Prev ones

            pos = sectionTrailer.Get("Prev") is PdfNumber prev ? (long)prev.Value : null;
        }

        return new PdfDictionary(merged);
    }

    private static long? FindStartXref(byte[] bytes)
    {
        var marker = System.Text.Encoding.Latin1.GetBytes("startxref");
        var idx = LastIndexOf(bytes, marker, bytes.Length - 1);
        if (idx < 0) return null;
        var tok = new PdfTokenizer(bytes, idx + marker.Length);
        var t = tok.Next();
        return t.Kind == PdfTokenKind.Number ? (long)t.Number : null;
    }

    private static PdfDictionary ParseClassicXrefSection(PdfTokenizer tok, Dictionary<(int, int), long> offsets)
    {
        tok.Next(); // "xref"
        while (true)
        {
            var save = tok.Position;
            var t = tok.Next();
            if (t.Kind == PdfTokenKind.Keyword && t.Text == "trailer") break;
            if (t.Kind != PdfTokenKind.Number) { tok.Position = save; break; }

            var startNum = (int)t.Number;
            var count = (int)tok.Next().Number;
            for (var i = 0; i < count; i++)
            {
                var offsetTok = tok.Next();
                var genTok = tok.Next();
                var kindTok = tok.Next();
                if (kindTok.Text != "n") continue; // "f" (free) entries have nothing to load

                var num = startNum + i;
                var gen = (int)genTok.Number;
                offsets.TryAdd((num, gen), (long)offsetTok.Number); // newest xref section wins
            }
        }

        var parser = new PdfObjectParser(tok);
        return parser.ParseValue() as PdfDictionary ?? PdfDictionary.Empty;
    }

    private static PdfDictionary ParseXrefStreamSection(
        byte[] bytes, long pos, Dictionary<(int, int), long> offsets, Dictionary<(int, int), (int, int)> compressed)
    {
        var bootstrapLoader = new PdfObjectLoader(bytes, new Dictionary<(int, int), long>(), new Dictionary<(int, int), (int, int)>(), new PdfObjectTable());
        if (bootstrapLoader.ParseIndirectObjectAt(pos) is not PdfStream stream) return PdfDictionary.Empty;

        var decoded = FilterCodec.Decode(stream.Dictionary, stream.RawBytes);
        var w = (stream.Dictionary.Get("W") as PdfArray)?.Items.Select(i => (i as PdfNumber)?.AsInt ?? 0).ToArray() ?? [1, 1, 1];
        var size = (stream.Dictionary.Get("Size") as PdfNumber)?.AsInt ?? 0;
        var index = (stream.Dictionary.Get("Index") as PdfArray)?.Items.Select(i => (i as PdfNumber)?.AsInt ?? 0).ToArray() ?? [0, size];

        var entryLen = w.Sum();
        if (entryLen <= 0) return stream.Dictionary;

        var p = 0;
        for (var pair = 0; pair + 1 < index.Length; pair += 2)
        {
            var first = index[pair];
            var count = index[pair + 1];
            for (var i = 0; i < count && p + entryLen <= decoded.Length; i++)
            {
                var objNum = first + i;
                var f1 = w[0] == 0 ? 1 : ReadBigEndian(decoded, p, w[0]);
                var f2 = ReadBigEndian(decoded, p + w[0], w[1]);
                var f3 = w[2] == 0 ? 0 : ReadBigEndian(decoded, p + w[0] + w[1], w[2]);
                p += entryLen;

                switch (f1)
                {
                    case 1:
                        offsets.TryAdd(((int)objNum, (int)f3), f2);
                        break;
                    case 2:
                        compressed.TryAdd(((int)objNum, 0), ((int)f2, (int)f3));
                        break;
                    // f1 == 0: free object, nothing to record
                }
            }
        }

        return stream.Dictionary;
    }

    private static long ReadBigEndian(byte[] data, int offset, int length)
    {
        long v = 0;
        for (var i = 0; i < length; i++) v = (v << 8) | data[offset + i];
        return v;
    }

    // ---- fallback recovery for a broken/missing xref -----------------------

    private static Dictionary<(int, int), long> LinearScan(byte[] bytes)
    {
        var result = new Dictionary<(int, int), long>();
        var marker = System.Text.Encoding.Latin1.GetBytes(" obj");

        for (var i = 0; i < bytes.Length - marker.Length; i++)
        {
            if (!Matches(bytes, i, marker)) continue;

            // Walk backwards over "N G" immediately preceding " obj".
            var j = i - 1;
            while (j >= 0 && bytes[j] == ' ') j--;
            var genEnd = j + 1;
            while (j >= 0 && bytes[j] is >= (byte)'0' and <= (byte)'9') j--;
            var genStart = j + 1;
            if (genStart == genEnd) continue;

            while (j >= 0 && bytes[j] == ' ') j--;
            var numEnd = j + 1;
            while (j >= 0 && bytes[j] is >= (byte)'0' and <= (byte)'9') j--;
            var numStart = j + 1;
            if (numStart == numEnd) continue;

            if (!int.TryParse(System.Text.Encoding.Latin1.GetString(bytes, numStart, numEnd - numStart), out var num)) continue;
            if (!int.TryParse(System.Text.Encoding.Latin1.GetString(bytes, genStart, genEnd - genStart), out var gen)) continue;

            result[(num, gen)] = numStart; // a later occurrence (an updated object) overwrites the earlier one
        }

        return result;
    }

    private static PdfDictionary? FindTrailerByScan(byte[] bytes)
    {
        var marker = System.Text.Encoding.Latin1.GetBytes("trailer");
        var idx = LastIndexOf(bytes, marker, bytes.Length - 1);
        if (idx < 0) return null;
        var tok = new PdfTokenizer(bytes, idx + marker.Length);
        return new PdfObjectParser(tok).ParseValue() as PdfDictionary;
    }

    private static PdfReference? FindCatalog(PdfObjectTable objects)
    {
        foreach (var (key, value) in objects.All)
        {
            if (value is PdfDictionary d && (d.Get("Type") as PdfName)?.Value == "Catalog")
                return new PdfReference(key.Number, key.Generation);
        }
        return null;
    }

    private static bool Matches(byte[] bytes, int at, byte[] pattern)
    {
        if (at + pattern.Length > bytes.Length) return false;
        for (var i = 0; i < pattern.Length; i++)
            if (bytes[at + i] != pattern[i]) return false;
        return true;
    }

    private static int LastIndexOf(byte[] bytes, byte[] pattern, int from)
    {
        for (var i = Math.Min(from, bytes.Length - pattern.Length); i >= 0; i--)
        {
            if (Matches(bytes, i, pattern)) return i;
        }
        return -1;
    }
}
