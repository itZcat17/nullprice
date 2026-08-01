namespace Nullprice.Sheaf.Core.Tests;

/// <summary>Builds a tiny synthetic sfnt (assembled through the real <see cref="SfntWriter"/>,
/// not hand-typed bytes end-to-end — mirroring how <c>PdfTestFixtures</c> builds fixtures
/// through <see cref="PdfWriter"/> itself) with a controlled glyph graph: two independent
/// simple glyphs, a composite glyph referencing both (proves the closure pulls in components),
/// and a deliberately cyclic pair of composite glyphs (proves the closure's cycle guard
/// terminates). A synthetic fixture is necessary for the cyclic case specifically — no real
/// font would ever contain a glyph reference cycle, so <c>TrueTypeFontTests</c>' approach of
/// exercising real system fonts can't cover it.</summary>
internal static class TrueTypeTestFixtures
{
    public const int GidNotdef = 0;
    public const int GidA = 1;
    public const int GidB = 2;
    public const int GidComposite = 3; // 'C' -> references A and B
    public const int GidCycle1 = 4;    // 'D' -> references GidCycle2
    public const int GidCycle2 = 5;    // no codepoint of its own -> references GidCycle1, completing the cycle

    public static byte[] Build()
    {
        var glyphs = new List<byte[]>
        {
            Array.Empty<byte>(), // .notdef
            BuildSimpleGlyphWithHints(),
            BuildSimpleGlyphWithoutHints(),
            BuildCompositeGlyph([GidA, GidB]),
            BuildCompositeGlyph([GidCycle2]),
            BuildCompositeGlyph([GidCycle1]),
        };

        var glyf = new List<byte>();
        var loca = new List<int> { 0 };
        foreach (var g in glyphs)
        {
            glyf.AddRange(g);
            loca.Add(glyf.Count);
        }

        var tables = new Dictionary<string, byte[]>
        {
            ["glyf"] = glyf.ToArray(),
            ["loca"] = BuildLongLoca(loca),
            ["cmap"] = BuildCmapFormat0(new Dictionary<int, int> { ['A'] = GidA, ['B'] = GidB, ['C'] = GidComposite, ['D'] = GidCycle1 }),
            ["head"] = BuildHead(unitsPerEm: 1000, indexToLocFormat: 1),
            ["maxp"] = BuildMaxp(glyphs.Count),
            ["hhea"] = BuildHhea(ascent: 800, descent: -200, numberOfHMetrics: glyphs.Count),
            ["hmtx"] = BuildHmtx(glyphs.Count, advanceWidth: 500),
            ["post"] = new byte[32],
        };

        return SfntWriter.Build(tables);
    }

    private static byte[] BuildSimpleGlyphWithHints()
    {
        var bytes = new List<byte>();
        WriteInt16BE(bytes, 1); // numberOfContours
        bytes.AddRange(new byte[8]); // bbox
        WriteUInt16BE(bytes, 2); // endPtsOfContours[0]
        WriteUInt16BE(bytes, 4); // instructionLength
        bytes.AddRange([0xAA, 0xBB, 0xCC, 0xDD]); // fake hint bytecode
        bytes.AddRange([0x11, 0x22, 0x33]); // arbitrary trailing flags/coords data — odd length on purpose
        return bytes.ToArray();
    }

    private static byte[] BuildSimpleGlyphWithoutHints()
    {
        var bytes = new List<byte>();
        WriteInt16BE(bytes, 1);
        bytes.AddRange(new byte[8]);
        WriteUInt16BE(bytes, 1);
        WriteUInt16BE(bytes, 0); // instructionLength
        bytes.Add(0x99); // arbitrary trailing byte — odd length on purpose
        return bytes.ToArray();
    }

    private static byte[] BuildCompositeGlyph(IReadOnlyList<int> componentGlyphIds)
    {
        var bytes = new List<byte>();
        WriteInt16BE(bytes, -1); // numberOfContours: composite marker
        bytes.AddRange(new byte[8]); // bbox

        for (var i = 0; i < componentGlyphIds.Count; i++)
        {
            var moreComponents = i < componentGlyphIds.Count - 1;
            WriteUInt16BE(bytes, (ushort)(moreComponents ? 0x0020 : 0x0000));
            WriteUInt16BE(bytes, (ushort)componentGlyphIds[i]);
            bytes.AddRange(new byte[2]); // 2-byte args, not words — values irrelevant to these tests
        }

        return bytes.ToArray();
    }

    private static byte[] BuildLongLoca(List<int> offsets)
    {
        var bytes = new byte[offsets.Count * 4];
        for (var i = 0; i < offsets.Count; i++)
            WriteUInt32BE(bytes, i * 4, (uint)offsets[i]);
        return bytes;
    }

    private static byte[] BuildCmapFormat0(Dictionary<int, int> charToGid)
    {
        var subtable = new byte[262];
        WriteUInt16BE(subtable, 0, 0); // format
        WriteUInt16BE(subtable, 2, 262); // length
        WriteUInt16BE(subtable, 4, 0); // language
        foreach (var (cp, gid) in charToGid) subtable[6 + cp] = (byte)gid;

        var bytes = new byte[4 + 8 + subtable.Length];
        WriteUInt16BE(bytes, 0, 0); // version
        WriteUInt16BE(bytes, 2, 1); // numTables
        WriteUInt16BE(bytes, 4, 3); // platformID: Windows
        WriteUInt16BE(bytes, 6, 1); // encodingID: Unicode BMP
        WriteUInt32BE(bytes, 8, 12); // offset to subtable
        Array.Copy(subtable, 0, bytes, 12, subtable.Length);
        return bytes;
    }

    private static byte[] BuildHead(int unitsPerEm, short indexToLocFormat)
    {
        var bytes = new byte[54];
        WriteUInt16BE(bytes, 18, (ushort)unitsPerEm);
        WriteInt16BE(bytes, 50, indexToLocFormat);
        return bytes;
    }

    private static byte[] BuildMaxp(int numGlyphs)
    {
        var bytes = new byte[6];
        WriteUInt32BE(bytes, 0, 0x00005000);
        WriteUInt16BE(bytes, 4, (ushort)numGlyphs);
        return bytes;
    }

    private static byte[] BuildHhea(short ascent, short descent, int numberOfHMetrics)
    {
        var bytes = new byte[36];
        WriteUInt32BE(bytes, 0, 0x00010000);
        WriteInt16BE(bytes, 4, ascent);
        WriteInt16BE(bytes, 6, descent);
        WriteUInt16BE(bytes, 34, (ushort)numberOfHMetrics);
        return bytes;
    }

    private static byte[] BuildHmtx(int numGlyphs, ushort advanceWidth)
    {
        var bytes = new byte[numGlyphs * 4];
        for (var i = 0; i < numGlyphs; i++)
            WriteUInt16BE(bytes, i * 4, advanceWidth);
        return bytes;
    }

    // ---- local byte helpers (this file has no access to Core's internal SfntBytes) ----

    private static void WriteUInt16BE(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static void WriteInt16BE(List<byte> bytes, short value) => WriteUInt16BE(bytes, unchecked((ushort)value));

    private static void WriteUInt16BE(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }

    private static void WriteInt16BE(byte[] bytes, int offset, short value) => WriteUInt16BE(bytes, offset, unchecked((ushort)value));

    private static void WriteUInt32BE(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }
}
