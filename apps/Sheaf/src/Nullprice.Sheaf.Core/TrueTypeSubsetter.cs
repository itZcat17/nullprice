using static Nullprice.Sheaf.Core.SfntBytes;

namespace Nullprice.Sheaf.Core;

/// <summary>The result of subsetting a <see cref="TrueTypeFont"/> down to exactly the glyphs a
/// piece of text needs: a fresh, minimal, well-formed sfnt plus everything
/// <see cref="CidFontBuilder"/> needs to wire it into a PDF <c>/Type0</c> font — the codepoint
/// to CID mapping, each CID's advance width already converted to PDF's fixed 1000-units/em
/// glyph space, and the source font's own units-per-em (needed for a few FontDescriptor
/// metrics that aren't pre-converted here).</summary>
public sealed record TrueTypeSubsetResult(
    byte[] FontBytes,
    string BaseFontName,
    IReadOnlyDictionary<int, int> CodepointToCid,
    IReadOnlyDictionary<int, int> CidToWidthPdfUnits,
    int UnitsPerEm,
    int Ascent,
    int Descent,
    int CapHeight,
    double ItalicAngle,
    bool IsFixedPitch,
    int XMin,
    int YMin,
    int XMax,
    int YMax);

/// <summary>
/// Builds a minimal embeddable subset of a classic TrueType font containing only the glyphs a
/// specific run of text needs (plus <c>.notdef</c>), as a <c>/CIDFontType2</c>-ready sfnt with
/// <c>/CIDToGIDMap /Identity</c> — CIDs *are* the new compacted glyph IDs directly, so no
/// indirection table is needed since Sheaf controls both the content-stream codes it emits and
/// the embedded font's glyph ordering.
///
/// The riskiest correctness properties here, each deliberately isolated and tested on its own:
/// hint instruction bytecode must be stripped from *every* kept glyph (not just the global
/// cvt/fpgm/prep tables) or a glyph's own hint program is left calling into tables that no
/// longer exist; every re-emitted glyph's length must be padded to an even byte count, because
/// the short <c>loca</c> format stores <c>offset / 2</c> as a <c>uint16</c> and cannot represent
/// an odd offset at all; and every advance width must be converted from the source font's own
/// units-per-em (commonly 2048) into PDF's fixed 1000-units/em glyph space, or every glyph
/// renders roughly twice as wide as it should.
/// </summary>
public static class TrueTypeSubsetter
{
    public static TrueTypeSubsetResult Subset(TrueTypeFont font, IReadOnlyCollection<int> codepoints, string sourceFontName)
    {
        var codepointToOldGid = new Dictionary<int, int>();
        foreach (var cp in codepoints)
        {
            if (font.TryGetGlyphId(cp, out var gid) && gid != 0)
                codepointToOldGid[cp] = gid;
        }

        var closure = ComputeClosure(font, codepointToOldGid.Values);
        closure.Add(0); // .notdef is always kept, and always maps to new GID 0

        var sortedOld = closure.OrderBy(g => g).ToList();
        var oldToNew = new Dictionary<int, int> { [0] = 0 };
        var nextNew = 1;
        foreach (var old in sortedOld)
        {
            if (old == 0) continue;
            oldToNew[old] = nextNew++;
        }
        var newToOld = oldToNew.ToDictionary(kv => kv.Value, kv => kv.Key);
        var newGlyphCount = oldToNew.Count;

        var (glyfBytes, locaOffsets) = BuildGlyfAndLoca(font, newToOld, newGlyphCount, oldToNew);
        var useShortLoca = glyfBytes.Length <= 0x1FFFE;
        var locaBytes = BuildLoca(locaOffsets, useShortLoca);
        var hmtxBytes = BuildHmtx(font, newToOld, newGlyphCount);
        var headBytes = BuildHead(font, useShortLoca);
        var maxpBytes = BuildMaxp(font, newGlyphCount);
        var hheaBytes = BuildHhea(font, newGlyphCount);
        var postBytes = BuildPost(font);
        var baseFontName = $"{GenerateSubsetTag()}+{SanitizeForPostScriptName(sourceFontName)}";
        var nameBytes = BuildName(baseFontName);

        var tables = new Dictionary<string, byte[]>
        {
            ["glyf"] = glyfBytes,
            ["loca"] = locaBytes,
            ["cmap"] = BuildIdentityCmap(newGlyphCount),
            ["hmtx"] = hmtxBytes,
            ["head"] = headBytes,
            ["maxp"] = maxpBytes,
            ["hhea"] = hheaBytes,
            ["post"] = postBytes,
            ["name"] = nameBytes,
        };
        // Copied through unchanged — a few fields (average char width, min/max covered
        // character) become slightly stale after subsetting, but its mere *presence* is what
        // matters: Windows' font loader (which Windows.Data.Pdf sits on top of) was found,
        // during this milestone's own end-to-end verification, to silently refuse to rasterize
        // any text at all from a CID font whose embedded sfnt has no OS/2 table.
        if (font.RawOS2Table.Length > 0) tables["OS/2"] = font.RawOS2Table;
        var fontBytes = SfntWriter.Build(tables);

        var codepointToCid = codepointToOldGid.ToDictionary(kv => kv.Key, kv => oldToNew[kv.Value]);
        var cidToWidthPdfUnits = new Dictionary<int, int>();
        for (var newGid = 0; newGid < newGlyphCount; newGid++)
        {
            var (advanceWidth, _) = font.GetHMetric(newToOld[newGid]);
            cidToWidthPdfUnits[newGid] = ToPdfGlyphSpace(advanceWidth, font.UnitsPerEm);
        }

        return new TrueTypeSubsetResult(
            fontBytes, baseFontName, codepointToCid, cidToWidthPdfUnits, font.UnitsPerEm,
            Ascent: ToPdfGlyphSpace(font.Ascent, font.UnitsPerEm),
            Descent: ToPdfGlyphSpace(font.Descent, font.UnitsPerEm),
            CapHeight: ToPdfGlyphSpace(font.CapHeight, font.UnitsPerEm),
            ItalicAngle: font.ItalicAngle,
            IsFixedPitch: font.IsFixedPitch,
            XMin: ToPdfGlyphSpace(font.XMin, font.UnitsPerEm),
            YMin: ToPdfGlyphSpace(font.YMin, font.UnitsPerEm),
            XMax: ToPdfGlyphSpace(font.XMax, font.UnitsPerEm),
            YMax: ToPdfGlyphSpace(font.YMax, font.UnitsPerEm));
    }

    /// <summary>Converts a value from the source font's own units-per-em (commonly 2048, but
    /// varies by font) into PDF glyph space, which is always fixed at 1000 units/em regardless
    /// of the embedded font's own unitsPerEm (ISO 32000-1 §9.7.4.3) — skipping this conversion
    /// is the single most consequential bug this milestone could ship with, since every glyph
    /// would still *parse* fine but render at roughly double (2048/1000) its correct width.</summary>
    public static int ToPdfGlyphSpace(double valueInFontUnits, int unitsPerEm) =>
        (int)Math.Round(valueInFontUnits * 1000.0 / unitsPerEm);

    // ---- glyph closure ----------------------------------------------------------------------

    private static HashSet<int> ComputeClosure(TrueTypeFont font, IEnumerable<int> roots)
    {
        var closure = new HashSet<int>();
        var stack = new Stack<int>(roots);

        while (stack.Count > 0)
        {
            var gid = stack.Pop();
            if (!closure.Add(gid)) continue; // already visited — guards against a cyclic composite reference

            var data = font.GetGlyphData(gid);
            if (data.Length < 10) continue; // no outline (e.g. space)
            if (ReadInt16BE(data, 0) >= 0) continue; // simple glyph, no components to chase

            foreach (var component in CompositeGlyphWalker.EnumerateComponents(data))
                stack.Push(component.GlyphIndex);
        }

        return closure;
    }

    // ---- glyf / loca --------------------------------------------------------------------------

    private static (byte[] Glyf, List<int> LocaOffsets) BuildGlyfAndLoca(
        TrueTypeFont font, Dictionary<int, int> newToOld, int newGlyphCount, Dictionary<int, int> oldToNew)
    {
        using var buffer = new MemoryStream();
        var locaOffsets = new List<int> { 0 };

        for (var newGid = 0; newGid < newGlyphCount; newGid++)
        {
            var raw = font.GetGlyphData(newToOld[newGid]);
            var stripped = raw.Length == 0 ? raw : StripHintsAndRewriteComponents(raw, oldToNew);
            buffer.Write(stripped);

            // The short loca format stores offset/2 as a uint16 — an odd cumulative offset is
            // structurally unrepresentable, not merely suboptimal, so every glyph is padded to
            // an even length regardless of which loca format ends up chosen for this font.
            if (stripped.Length % 2 != 0) buffer.WriteByte(0);

            locaOffsets.Add((int)buffer.Length);
        }

        return (buffer.ToArray(), locaOffsets);
    }

    private static byte[] StripHintsAndRewriteComponents(byte[] raw, Dictionary<int, int> oldToNew)
    {
        var numberOfContours = ReadInt16BE(raw, 0);
        return numberOfContours >= 0
            ? StripSimpleGlyphHints(raw)
            : RewriteCompositeGlyph(raw, oldToNew);
    }

    /// <summary>Removes a simple glyph's hint instruction bytecode entirely (not merely the
    /// global cvt/fpgm/prep tables it might reference) — a glyph's own hint program can contain
    /// CALL/MIAP/MIRP opcodes into those global tables, and dropping only the globals while
    /// leaving the glyph's local bytecode in place would leave those references dangling.
    /// Removing the whole per-glyph program sidesteps that risk rather than trying to detect
    /// which references are safe.</summary>
    private static byte[] StripSimpleGlyphHints(byte[] raw)
    {
        var numberOfContours = ReadInt16BE(raw, 0);
        var instrLenOffset = 10 + numberOfContours * 2;
        if (instrLenOffset + 2 > raw.Length) return raw; // malformed/truncated — leave untouched rather than throw

        var instructionLength = ReadUInt16BE(raw, instrLenOffset);
        var afterInstructions = instrLenOffset + 2 + instructionLength;
        if (afterInstructions > raw.Length) return raw;

        var result = new byte[raw.Length - instructionLength];
        Array.Copy(raw, 0, result, 0, instrLenOffset);
        WriteUInt16BE(result, instrLenOffset, 0);
        Array.Copy(raw, afterInstructions, result, instrLenOffset + 2, raw.Length - afterInstructions);
        return result;
    }

    private static byte[] RewriteCompositeGlyph(byte[] raw, Dictionary<int, int> oldToNew)
    {
        var components = CompositeGlyphWalker.EnumerateComponents(raw);
        var last = components[^1];

        // A trailing hint instruction block (present only when the last component's
        // WE_HAVE_INSTRUCTIONS flag is set) is dropped the same way simple-glyph hints are —
        // whole program removed, never left dangling.
        var keepLength = last.HasInstructionsFlag ? last.NextOffset : raw.Length;

        var result = new byte[keepLength];
        Array.Copy(raw, 0, result, 0, keepLength);

        foreach (var component in components)
            WriteUInt16BE(result, component.GlyphIndexByteOffset, (ushort)oldToNew[component.GlyphIndex]);

        if (last.HasInstructionsFlag)
        {
            var flagsOffset = last.GlyphIndexByteOffset - 2;
            var flags = ReadUInt16BE(result, flagsOffset);
            WriteUInt16BE(result, flagsOffset, (ushort)(flags & ~0x0100));
        }

        return result;
    }

    /// <summary>A trivial code-equals-glyph-ID cmap. Never actually consulted on the
    /// <c>Identity-H</c> rendering path per spec (content-stream code → CID via Identity →
    /// GID via <c>/CIDToGIDMap /Identity</c> → glyf directly, ISO 32000-1 §9.7.4.2) — the
    /// original design assumed omitting <c>cmap</c> entirely was safe and merely worth an
    /// empirical check before treating it as settled. That check (see this milestone's own
    /// verification notes) found at least one real-world renderer that silently refuses to
    /// draw any text at all from a CID font with no cmap table, so one is always included now.</summary>
    private static byte[] BuildIdentityCmap(int glyphCount)
    {
        var maxCode = Math.Max(0, glyphCount - 1);
        const int segCount = 2; // one real segment [0, maxCode] plus the mandatory terminator segment
        var segCountX2 = segCount * 2;

        var subtable = new byte[14 + segCountX2 * 4 + 2];
        WriteUInt16BE(subtable, 0, 4); // format
        WriteUInt16BE(subtable, 2, (ushort)subtable.Length);
        WriteUInt16BE(subtable, 4, 0); // language
        WriteUInt16BE(subtable, 6, (ushort)segCountX2);

        var endCodeOffset = 14;
        var startCodeOffset = endCodeOffset + segCountX2 + 2; // +2 skips reservedPad
        var idDeltaOffset = startCodeOffset + segCountX2;
        var idRangeOffsetOffset = idDeltaOffset + segCountX2;

        WriteUInt16BE(subtable, endCodeOffset, (ushort)maxCode);
        WriteUInt16BE(subtable, endCodeOffset + 2, 0xFFFF);
        WriteUInt16BE(subtable, startCodeOffset, 0);
        WriteUInt16BE(subtable, startCodeOffset + 2, 0xFFFF);
        WriteUInt16BE(subtable, idDeltaOffset, 0); // code + 0 = gid, i.e. identity
        WriteUInt16BE(subtable, idDeltaOffset + 2, 1); // conventional terminator-segment delta
        WriteUInt16BE(subtable, idRangeOffsetOffset, 0);
        WriteUInt16BE(subtable, idRangeOffsetOffset + 2, 0);

        var bytes = new byte[4 + 8 + subtable.Length];
        WriteUInt16BE(bytes, 0, 0); // version
        WriteUInt16BE(bytes, 2, 1); // numTables
        WriteUInt16BE(bytes, 4, 3); // platformID: Windows
        WriteUInt16BE(bytes, 6, 1); // encodingID: Unicode BMP
        WriteUInt32BE(bytes, 8, 12); // offset to subtable
        Array.Copy(subtable, 0, bytes, 12, subtable.Length);
        return bytes;
    }

    private static byte[] BuildLoca(List<int> offsets, bool useShortFormat)
    {
        if (useShortFormat)
        {
            var bytes = new byte[offsets.Count * 2];
            for (var i = 0; i < offsets.Count; i++)
                WriteUInt16BE(bytes, i * 2, (ushort)(offsets[i] / 2));
            return bytes;
        }
        else
        {
            var bytes = new byte[offsets.Count * 4];
            for (var i = 0; i < offsets.Count; i++)
                WriteUInt32BE(bytes, i * 4, (uint)offsets[i]);
            return bytes;
        }
    }

    // ---- hmtx / hhea --------------------------------------------------------------------------

    /// <summary>Every new glyph gets a full {advanceWidth, lsb} pair — the tail-compression
    /// scheme full fonts use (fewer explicit entries than glyphs, reusing the last width) buys
    /// nothing at subset scale and just adds another place to get the count wrong.</summary>
    private static byte[] BuildHmtx(TrueTypeFont font, Dictionary<int, int> newToOld, int newGlyphCount)
    {
        var bytes = new byte[newGlyphCount * 4];
        for (var newGid = 0; newGid < newGlyphCount; newGid++)
        {
            var (advanceWidth, lsb) = font.GetHMetric(newToOld[newGid]);
            WriteUInt16BE(bytes, newGid * 4, advanceWidth);
            WriteUInt16BE(bytes, newGid * 4 + 2, unchecked((ushort)lsb));
        }
        return bytes;
    }

    private static byte[] BuildHhea(TrueTypeFont font, int newGlyphCount)
    {
        var bytes = new byte[36];
        WriteUInt32BE(bytes, 0, 0x00010000);
        WriteUInt16BE(bytes, 4, unchecked((ushort)font.Ascent));
        WriteUInt16BE(bytes, 6, unchecked((ushort)font.Descent));
        WriteUInt16BE(bytes, 34, (ushort)newGlyphCount);
        return bytes;
    }

    // ---- head / maxp / post / name -------------------------------------------------------------

    private static byte[] BuildHead(TrueTypeFont font, bool useShortLoca)
    {
        var bytes = new byte[54];
        Array.Copy(font.RawHeadTable, bytes, Math.Min(54, font.RawHeadTable.Length));
        WriteUInt32BE(bytes, 8, 0); // checkSumAdjustment — recomputed by SfntWriter once the whole file is assembled
        WriteUInt16BE(bytes, 50, (ushort)(useShortLoca ? 0 : 1));
        return bytes;
    }

    private static byte[] BuildMaxp(TrueTypeFont font, int newGlyphCount)
    {
        // Version 0.5 is defined only for CFF-flavored fonts — a glyf-outline font must declare
        // version 1.0, or a strict font loader can refuse the whole file as internally
        // inconsistent even though nothing about the glyph data itself is wrong (found
        // empirically during this milestone's own end-to-end verification). The version-1.0
        // profiling fields (maxPoints, maxContours, …) are copied through from the original
        // as safe, if slightly generous, upper bounds — only numGlyphs actually needs updating.
        var bytes = new byte[32];
        Array.Copy(font.RawMaxpTable, bytes, Math.Min(32, font.RawMaxpTable.Length));
        WriteUInt32BE(bytes, 0, 0x00010000);
        WriteUInt16BE(bytes, 4, (ushort)newGlyphCount);
        return bytes;
    }

    private static byte[] BuildPost(TrueTypeFont font)
    {
        // Format 3: the same fixed 32-byte header every post table has, declaring "no per-glyph
        // PostScript names provided" — which is exactly true here, since format 1/2's per-glyph
        // name arrays are the part being dropped, not anything CID/Identity-H rendering needs.
        var bytes = new byte[32];
        Array.Copy(font.RawPostHeader, bytes, 32);
        WriteUInt32BE(bytes, 0, 0x00030000);
        return bytes;
    }

    private static byte[] BuildName(string baseFontName)
    {
        var stringBytes = System.Text.Encoding.BigEndianUnicode.GetBytes(baseFontName);

        var bytes = new byte[6 + 12 + stringBytes.Length];
        WriteUInt16BE(bytes, 0, 0); // format
        WriteUInt16BE(bytes, 2, 1); // count
        WriteUInt16BE(bytes, 4, 18); // stringOffset (6-byte header + 1 * 12-byte record)

        WriteUInt16BE(bytes, 6, 3); // platformID: Windows
        WriteUInt16BE(bytes, 8, 1); // encodingID: Unicode BMP
        WriteUInt16BE(bytes, 10, 0x0409); // languageID: en-US
        WriteUInt16BE(bytes, 12, 6); // nameID: PostScript name
        WriteUInt16BE(bytes, 14, (ushort)stringBytes.Length);
        WriteUInt16BE(bytes, 16, 0); // offset within string storage

        Array.Copy(stringBytes, 0, bytes, 18, stringBytes.Length);
        return bytes;
    }

    private static string GenerateSubsetTag()
    {
        var chars = new char[6];
        for (var i = 0; i < 6; i++) chars[i] = (char)('A' + Random.Shared.Next(26));
        return new string(chars);
    }

    /// <summary>PostScript names (used for both the sfnt's own nameID 6 and the PDF's
    /// <c>/BaseFont</c>) forbid spaces and a handful of delimiter characters — strips anything
    /// outside <c>[A-Za-z0-9]</c> rather than escaping it, since a display name is all this is
    /// used for here.</summary>
    private static string SanitizeForPostScriptName(string name)
    {
        var chars = name.Where(char.IsLetterOrDigit).ToArray();
        return chars.Length == 0 ? "Font" : new string(chars);
    }
}
