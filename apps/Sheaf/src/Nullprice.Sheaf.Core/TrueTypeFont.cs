using static Nullprice.Sheaf.Core.SfntBytes;

namespace Nullprice.Sheaf.Core;

public enum TrueTypeParseProblem { NotClassicTrueType, VariableFont, RestrictedLicense, Malformed }

public sealed record TrueTypeParseResult(TrueTypeFont? Font, TrueTypeParseProblem? Problem, string? Message)
{
    public static TrueTypeParseResult Ok(TrueTypeFont font) => new(font, null, null);
    public static TrueTypeParseResult Fail(TrueTypeParseProblem problem, string message) => new(null, problem, message);
}

/// <summary>
/// Read-only model of a classic TrueType (<c>glyf</c>/<c>loca</c>) sfnt, parsed just far enough
/// to support subsetting (<see cref="TrueTypeSubsetter"/>): cmap (codepoint→glyph lookup),
/// glyf/loca (glyph outline data, for closure and re-emission), hmtx/hhea (advance widths), and
/// the handful of head/OS-2/post fields a PDF FontDescriptor needs. Nothing about rendering the
/// outlines themselves is modeled — Sheaf never rasterizes a glyph, only copies its bytes.
///
/// <see cref="Parse"/> refuses, with a specific <see cref="TrueTypeParseProblem"/> rather than
/// a best-effort read, anything outside classic-TrueType v1 scope: OpenType/CFF-flavored fonts
/// (no glyf table), variable fonts (an <c>fvar</c> table present — only one static instance's
/// outlines could ever be picked, and picking one silently would be surprising), and fonts
/// whose <c>OS/2.fsType</c> forbids embedding.
/// </summary>
public sealed class TrueTypeFont
{
    private readonly byte[] _bytes;
    private readonly Dictionary<int, int> _cmap;
    private readonly int[] _locaOffsets;
    private readonly int _glyfOffset;
    private readonly (ushort AdvanceWidth, short Lsb)[] _hmtx;

    public int UnitsPerEm { get; }
    public int NumGlyphs { get; }
    public short Ascent { get; }
    public short Descent { get; }
    public short XMin { get; }
    public short YMin { get; }
    public short XMax { get; }
    public short YMax { get; }
    public double ItalicAngle { get; }
    public bool IsFixedPitch { get; }
    public short CapHeight { get; }

    /// <summary>The original head table's 54 raw bytes (all fields sfnt tables actually define)
    /// — used as the starting point for the subsetted font's own head table, since most fields
    /// (created/modified dates, font direction hint, flags, …) carry over unchanged.</summary>
    public byte[] RawHeadTable { get; }

    /// <summary>The post table's fixed 32-byte header (italicAngle, underline metrics,
    /// isFixedPitch, memory-usage hints) — present regardless of post table version, and
    /// carried over as-is into the subsetted font's format-3 post table (which drops only the
    /// per-glyph PostScript name data that follows this header in formats 1/2).</summary>
    public byte[] RawPostHeader { get; }

    /// <summary>The original OS/2 table's raw bytes, copied through unchanged into the
    /// subsetted font. A handful of its fields (average char width, min/max covered character,
    /// Unicode/codepage range bits) become slightly stale after subsetting, but none of that is
    /// load-bearing for rendering — what turned out to matter, found empirically during this
    /// milestone's own end-to-end verification, is that Windows' font loader (which
    /// <c>Windows.Data.Pdf</c> sits on top of) silently refuses to rasterize *any* text from a
    /// CID font whose embedded sfnt has no OS/2 table at all, even though nothing about the
    /// <c>glyf</c>/<c>loca</c>/<c>cmap</c> data itself was wrong.</summary>
    public byte[] RawOS2Table { get; }

    /// <summary>The original maxp table's raw bytes (always 32, version-1.0 form — see
    /// <see cref="Parse"/>), copied through into the subsetted font with only
    /// <c>numGlyphs</c> patched. The profiling fields (maxPoints, maxContours, …) are upper
    /// bounds a reader uses for buffer sizing, not exactness requirements — a subset's actual
    /// complexity is always ≤ the original's, so the original's own values remain safe, if
    /// slightly generous, over-estimates.</summary>
    public byte[] RawMaxpTable { get; }

    private TrueTypeFont(
        byte[] bytes, Dictionary<int, int> cmap, int[] locaOffsets, int glyfOffset, (ushort, short)[] hmtx,
        int unitsPerEm, int numGlyphs, short ascent, short descent, short xMin, short yMin, short xMax, short yMax,
        double italicAngle, bool isFixedPitch, short capHeight, byte[] rawHeadTable, byte[] rawPostHeader, byte[] rawOs2Table,
        byte[] rawMaxpTable)
    {
        _bytes = bytes;
        _cmap = cmap;
        _locaOffsets = locaOffsets;
        _glyfOffset = glyfOffset;
        _hmtx = hmtx;
        UnitsPerEm = unitsPerEm;
        NumGlyphs = numGlyphs;
        Ascent = ascent;
        Descent = descent;
        XMin = xMin;
        YMin = yMin;
        XMax = xMax;
        YMax = yMax;
        ItalicAngle = italicAngle;
        IsFixedPitch = isFixedPitch;
        CapHeight = capHeight;
        RawHeadTable = rawHeadTable;
        RawPostHeader = rawPostHeader;
        RawOS2Table = rawOs2Table;
        RawMaxpTable = rawMaxpTable;
    }

    public bool TryGetGlyphId(int codepoint, out int glyphId) => _cmap.TryGetValue(codepoint, out glyphId);

    /// <summary>The glyph's raw <c>glyf</c> table bytes (header + outline data, whatever its
    /// length), or an empty array for a glyph with no outline (e.g. space) or an out-of-range
    /// ID.</summary>
    public byte[] GetGlyphData(int glyphId)
    {
        if (glyphId < 0 || glyphId + 1 >= _locaOffsets.Length) return [];
        var start = _locaOffsets[glyphId];
        var end = _locaOffsets[glyphId + 1];
        if (end <= start) return [];

        var result = new byte[end - start];
        Array.Copy(_bytes, _glyfOffset + start, result, 0, end - start);
        return result;
    }

    public (ushort AdvanceWidth, short Lsb) GetHMetric(int glyphId) => _hmtx[glyphId];

    /// <param name="ttcFontIndex">Which face to read out of a TrueType Collection (<c>ttcf</c>)
    /// file. Ignored for a plain sfnt. Only face 0 is meaningfully exercised in v1 — an explicit,
    /// stated simplification rather than a silent one: most system TTCs are used via their first
    /// face, and full multi-face UI is a future gap, not attempted here.</param>
    public static TrueTypeParseResult Parse(byte[] bytes, int ttcFontIndex = 0)
    {
        try
        {
            var directoryStart = 0;
            if (bytes.Length >= 16 && Tag(bytes, 0) == "ttcf")
            {
                var numFonts = (int)ReadUInt32BE(bytes, 8);
                if (ttcFontIndex < 0 || ttcFontIndex >= numFonts)
                    return TrueTypeParseResult.Fail(TrueTypeParseProblem.Malformed, "This font collection has no face at the requested index.");
                directoryStart = (int)ReadUInt32BE(bytes, 12 + ttcFontIndex * 4);
            }

            var numTables = ReadUInt16BE(bytes, directoryStart + 4);
            var tables = new Dictionary<string, (int Offset, int Length)>();
            for (var i = 0; i < numTables; i++)
            {
                var recordOffset = directoryStart + 12 + i * 16;
                var tag = Tag(bytes, recordOffset);
                var offset = (int)ReadUInt32BE(bytes, recordOffset + 8);
                var length = (int)ReadUInt32BE(bytes, recordOffset + 12);
                tables[tag] = (offset, length);
            }

            if (tables.ContainsKey("fvar"))
                return TrueTypeParseResult.Fail(TrueTypeParseProblem.VariableFont,
                    "This is a variable font — there's no single fixed set of outlines to embed, so it isn't supported yet.");

            if (!tables.ContainsKey("glyf") || !tables.ContainsKey("loca"))
                return TrueTypeParseResult.Fail(TrueTypeParseProblem.NotClassicTrueType,
                    "This isn't a classic TrueType outline font (no glyf/loca tables) — likely an OpenType/CFF font, which isn't supported yet.");

            tables.TryGetValue("OS/2", out var os2);
            if (os2.Length > 0)
            {
                var fsType = ReadUInt16BE(bytes, os2.Offset + 8);
                if ((fsType & 0x0002) != 0)
                    return TrueTypeParseResult.Fail(TrueTypeParseProblem.RestrictedLicense, "This font's license doesn't permit embedding it.");
            }

            if (!tables.TryGetValue("head", out var head))
                return TrueTypeParseResult.Fail(TrueTypeParseProblem.Malformed, "This font is missing its 'head' table.");
            var unitsPerEm = ReadUInt16BE(bytes, head.Offset + 18);
            var indexToLocFormat = ReadInt16BE(bytes, head.Offset + 50);
            var xMin = ReadInt16BE(bytes, head.Offset + 36);
            var yMin = ReadInt16BE(bytes, head.Offset + 38);
            var xMax = ReadInt16BE(bytes, head.Offset + 40);
            var yMax = ReadInt16BE(bytes, head.Offset + 42);
            var headBytes = new byte[54];
            Array.Copy(bytes, head.Offset, headBytes, 0, Math.Min(54, head.Length));

            if (!tables.TryGetValue("maxp", out var maxp))
                return TrueTypeParseResult.Fail(TrueTypeParseProblem.Malformed, "This font is missing its 'maxp' table.");
            var numGlyphs = ReadUInt16BE(bytes, maxp.Offset + 4);
            // Every classic-TrueType (glyf-outline) font's maxp is version 1.0 (32 bytes) —
            // version 0.5 is defined only for CFF-flavored fonts, which Parse already refused
            // above, so this table is always copied through in full.
            var maxpBytes = new byte[Math.Max(32, maxp.Length)];
            Array.Copy(bytes, maxp.Offset, maxpBytes, 0, maxp.Length);

            if (!tables.TryGetValue("hhea", out var hhea))
                return TrueTypeParseResult.Fail(TrueTypeParseProblem.Malformed, "This font is missing its 'hhea' table.");
            var ascent = ReadInt16BE(bytes, hhea.Offset + 4);
            var descent = ReadInt16BE(bytes, hhea.Offset + 6);
            var numberOfHMetrics = ReadUInt16BE(bytes, hhea.Offset + 34);

            if (!tables.TryGetValue("hmtx", out var hmtx))
                return TrueTypeParseResult.Fail(TrueTypeParseProblem.Malformed, "This font is missing its 'hmtx' table.");

            var hmtxEntries = new (ushort, short)[numGlyphs];
            ushort lastAdvance = 0;
            for (var i = 0; i < numGlyphs; i++)
            {
                if (i < numberOfHMetrics)
                {
                    lastAdvance = ReadUInt16BE(bytes, hmtx.Offset + i * 4);
                    hmtxEntries[i] = (lastAdvance, ReadInt16BE(bytes, hmtx.Offset + i * 4 + 2));
                }
                else
                {
                    var lsbOffset = hmtx.Offset + numberOfHMetrics * 4 + (i - numberOfHMetrics) * 2;
                    hmtxEntries[i] = (lastAdvance, ReadInt16BE(bytes, lsbOffset));
                }
            }

            var loca = tables["loca"];
            var locaCount = numGlyphs + 1;
            var locaOffsets = new int[locaCount];
            if (indexToLocFormat == 0)
            {
                for (var i = 0; i < locaCount; i++)
                    locaOffsets[i] = ReadUInt16BE(bytes, loca.Offset + i * 2) * 2;
            }
            else
            {
                for (var i = 0; i < locaCount; i++)
                    locaOffsets[i] = (int)ReadUInt32BE(bytes, loca.Offset + i * 4);
            }

            var glyfOffset = tables["glyf"].Offset;
            var cmap = ParseCmap(bytes, tables);

            double italicAngle = 0;
            var isFixedPitch = false;
            var postHeader = new byte[32];
            if (tables.TryGetValue("post", out var post) && post.Length >= 32)
            {
                Array.Copy(bytes, post.Offset, postHeader, 0, 32);
                italicAngle = ReadFixed(bytes, post.Offset + 4);
                isFixedPitch = ReadUInt32BE(bytes, post.Offset + 12) != 0;
            }

            var capHeight = ascent;
            if (os2.Length >= 90 && ReadUInt16BE(bytes, os2.Offset) >= 2)
                capHeight = ReadInt16BE(bytes, os2.Offset + 88);

            var os2Bytes = new byte[os2.Length];
            if (os2.Length > 0) Array.Copy(bytes, os2.Offset, os2Bytes, 0, os2.Length);

            var font = new TrueTypeFont(
                bytes, cmap, locaOffsets, glyfOffset, hmtxEntries,
                unitsPerEm, numGlyphs, ascent, descent, xMin, yMin, xMax, yMax,
                italicAngle, isFixedPitch, capHeight, headBytes, postHeader, os2Bytes, maxpBytes);
            return TrueTypeParseResult.Ok(font);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return TrueTypeParseResult.Fail(TrueTypeParseProblem.Malformed, "This font's data is truncated or malformed.");
        }
    }

    // ---- cmap -----------------------------------------------------------------------------

    /// <summary>Picks the best available subtable — full-Unicode format 12 (3,10), then BMP
    /// format 4 (3,1), then any "platform 0" Unicode variant, falling back to a (3,0) symbol
    /// subtable only if nothing better exists. Symbol fonts (Wingdings/Webdings/Symbol, all
    /// installed by default on Windows) store their glyphs in the U+F000–U+F0FF Private Use
    /// Area, so both the raw PUA codepoint and the bare byte value are indexed — a caller asking
    /// for plain 'A' (U+0041) still finds what the font stores at U+F041.</summary>
    private static Dictionary<int, int> ParseCmap(byte[] bytes, Dictionary<string, (int Offset, int Length)> tables)
    {
        var result = new Dictionary<int, int>();
        if (!tables.TryGetValue("cmap", out var cmapTable)) return result;

        var numSubtables = ReadUInt16BE(bytes, cmapTable.Offset + 2);
        int? unicodeSubtable = null;
        var unicodeScore = -1;
        int? symbolSubtable = null;

        for (var i = 0; i < numSubtables; i++)
        {
            var recOffset = cmapTable.Offset + 4 + i * 8;
            var platformId = ReadUInt16BE(bytes, recOffset);
            var encodingId = ReadUInt16BE(bytes, recOffset + 2);
            var subtableOffset = cmapTable.Offset + (int)ReadUInt32BE(bytes, recOffset + 4);

            if (platformId == 3 && encodingId == 0) { symbolSubtable = subtableOffset; continue; }

            var score = (platformId, encodingId) switch
            {
                (3, 10) => 100,
                (0, 4) or (0, 6) => 90,
                (3, 1) => 80,
                (0, _) => 60,
                _ => -1, // e.g. Macintosh (platform 1) — not Unicode-keyed, never preferred over even the symbol fallback below
            };
            if (score > unicodeScore) { unicodeScore = score; unicodeSubtable = subtableOffset; }
        }

        if (unicodeSubtable is { } uOffset)
        {
            ParseCmapSubtable(bytes, uOffset, result);
        }
        else if (symbolSubtable is { } sOffset)
        {
            var raw = new Dictionary<int, int>();
            ParseCmapSubtable(bytes, sOffset, raw);
            foreach (var (cp, gid) in raw)
            {
                result.TryAdd(cp, gid);
                result.TryAdd(cp & 0xFF, gid);
            }
        }

        return result;
    }

    private static void ParseCmapSubtable(byte[] bytes, int offset, Dictionary<int, int> result)
    {
        switch (ReadUInt16BE(bytes, offset))
        {
            case 0: ParseCmapFormat0(bytes, offset, result); break;
            case 4: ParseCmapFormat4(bytes, offset, result); break;
            case 6: ParseCmapFormat6(bytes, offset, result); break;
            case 12: ParseCmapFormat12(bytes, offset, result); break;
        }
    }

    private static void ParseCmapFormat0(byte[] bytes, int offset, Dictionary<int, int> result)
    {
        for (var c = 0; c < 256; c++)
        {
            var gid = bytes[offset + 6 + c];
            if (gid != 0) result[c] = gid;
        }
    }

    private static void ParseCmapFormat4(byte[] bytes, int offset, Dictionary<int, int> result)
    {
        var segCountX2 = ReadUInt16BE(bytes, offset + 6);
        var segCount = segCountX2 / 2;
        var endCodeOffset = offset + 14;
        var startCodeOffset = endCodeOffset + segCountX2 + 2; // +2 skips reservedPad
        var idDeltaOffset = startCodeOffset + segCountX2;
        var idRangeOffsetOffset = idDeltaOffset + segCountX2;

        for (var seg = 0; seg < segCount; seg++)
        {
            var endCode = ReadUInt16BE(bytes, endCodeOffset + seg * 2);
            var startCode = ReadUInt16BE(bytes, startCodeOffset + seg * 2);
            var idDelta = ReadInt16BE(bytes, idDeltaOffset + seg * 2);
            var idRangeOffset = ReadUInt16BE(bytes, idRangeOffsetOffset + seg * 2);

            if (startCode == 0xFFFF && endCode == 0xFFFF) continue; // terminator segment

            for (var c = startCode; c <= endCode && c != 0xFFFF; c++)
            {
                int gid;
                if (idRangeOffset == 0)
                {
                    gid = (c + idDelta) & 0xFFFF;
                }
                else
                {
                    var glyphIndexAddress = idRangeOffsetOffset + seg * 2 + idRangeOffset + (c - startCode) * 2;
                    var rawGid = ReadUInt16BE(bytes, glyphIndexAddress);
                    gid = rawGid == 0 ? 0 : (rawGid + idDelta) & 0xFFFF;
                }
                if (gid != 0) result[c] = gid;
            }
        }
    }

    private static void ParseCmapFormat6(byte[] bytes, int offset, Dictionary<int, int> result)
    {
        var firstCode = ReadUInt16BE(bytes, offset + 6);
        var entryCount = ReadUInt16BE(bytes, offset + 8);
        for (var i = 0; i < entryCount; i++)
        {
            var gid = ReadUInt16BE(bytes, offset + 10 + i * 2);
            if (gid != 0) result[firstCode + i] = gid;
        }
    }

    private static void ParseCmapFormat12(byte[] bytes, int offset, Dictionary<int, int> result)
    {
        var numGroups = (int)ReadUInt32BE(bytes, offset + 12);
        for (var g = 0; g < numGroups; g++)
        {
            var groupOffset = offset + 16 + g * 12;
            var startCharCode = ReadUInt32BE(bytes, groupOffset);
            var endCharCode = ReadUInt32BE(bytes, groupOffset + 4);
            var startGlyphId = ReadUInt32BE(bytes, groupOffset + 8);
            for (var c = startCharCode; c <= endCharCode; c++)
            {
                var gid = (int)(startGlyphId + (c - startCharCode));
                if (gid != 0) result[(int)c] = gid;
            }
        }
    }
}
