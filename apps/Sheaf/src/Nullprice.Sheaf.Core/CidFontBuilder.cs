namespace Nullprice.Sheaf.Core;

/// <summary>
/// Wraps a <see cref="TrueTypeSubsetResult"/> into a PDF <c>/Type0</c> composite font (ISO
/// 32000-1 §9.7): <c>/Encoding /Identity-H</c> means content-stream show operators carry
/// 2-byte CIDs directly, and <c>/CIDToGIDMap /Identity</c> means those CIDs are also glyph
/// IDs directly — both are exactly what <see cref="TrueTypeSubsetter"/> already produces (new
/// compacted glyph IDs *are* the CIDs), so no extra indirection table is needed here. A
/// <c>/ToUnicode</c> CMap is included so copy/paste and text extraction still recover the
/// real characters even though the content stream itself only carries CIDs.
/// </summary>
public static class CidFontBuilder
{
    /// <summary>Builds the font objects and returns a reference to the <c>/Type0</c> dictionary
    /// — ready to drop straight into a page or appearance stream's <c>/Font</c> resource dict.</summary>
    public static PdfReference Embed(PdfObjectTable destination, TrueTypeSubsetResult subset)
    {
        var fontFileRef = new PdfReference(destination.Allocate(), 0);
        destination.Set(fontFileRef.Number, fontFileRef.Generation, new PdfStream(
            new PdfDictionary(new Dictionary<string, PdfObject> { ["Length1"] = new PdfNumber(subset.FontBytes.Length) }),
            subset.FontBytes));

        // Every one of these (FontBBox/Flags/ItalicAngle/Ascent/Descent/CapHeight/StemV) is a
        // *required* FontDescriptor entry per ISO 32000-1 Table 122, not an optional nicety —
        // omitting them produced a font some real viewers (Windows.Data.Pdf among them, caught
        // during this milestone's own end-to-end verification) silently refuse to render at all,
        // rather than erroring. StemV has no direct TrueType equivalent; 80 is the same nominal
        // default other font-embedding tools fall back to when a real value isn't derivable.
        var flags = (subset.IsFixedPitch ? 1 : 0) | 4;
        var descriptorRef = new PdfReference(destination.Allocate(), 0);
        destination.Set(descriptorRef.Number, descriptorRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("FontDescriptor"),
            ["FontName"] = new PdfName(subset.BaseFontName),
            ["Flags"] = new PdfNumber(flags),
            ["FontBBox"] = new PdfArray([new PdfNumber(subset.XMin), new PdfNumber(subset.YMin), new PdfNumber(subset.XMax), new PdfNumber(subset.YMax)]),
            ["ItalicAngle"] = new PdfNumber(subset.ItalicAngle, IsInteger: subset.ItalicAngle == Math.Floor(subset.ItalicAngle)),
            ["Ascent"] = new PdfNumber(subset.Ascent),
            ["Descent"] = new PdfNumber(subset.Descent),
            ["CapHeight"] = new PdfNumber(subset.CapHeight),
            ["StemV"] = new PdfNumber(80),
            ["FontFile2"] = fontFileRef,
        }));

        var cidFontRef = new PdfReference(destination.Allocate(), 0);
        destination.Set(cidFontRef.Number, cidFontRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("CIDFontType2"),
            ["BaseFont"] = new PdfName(subset.BaseFontName),
            ["CIDSystemInfo"] = new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Registry"] = new PdfString(System.Text.Encoding.ASCII.GetBytes("Adobe")),
                ["Ordering"] = new PdfString(System.Text.Encoding.ASCII.GetBytes("Identity")),
                ["Supplement"] = new PdfNumber(0),
            }),
            ["FontDescriptor"] = descriptorRef,
            ["CIDToGIDMap"] = new PdfName("Identity"),
            ["W"] = BuildWidthsArray(subset.CidToWidthPdfUnits),
        }));

        var toUnicodeRef = new PdfReference(destination.Allocate(), 0);
        destination.Set(toUnicodeRef.Number, toUnicodeRef.Generation,
            new PdfStream(PdfDictionary.Empty, BuildToUnicodeCMap(subset.CodepointToCid)));

        var type0Ref = new PdfReference(destination.Allocate(), 0);
        destination.Set(type0Ref.Number, type0Ref.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("Type0"),
            ["BaseFont"] = new PdfName(subset.BaseFontName),
            ["Encoding"] = new PdfName("Identity-H"),
            ["DescendantFonts"] = new PdfArray([cidFontRef]),
            ["ToUnicode"] = toUnicodeRef,
        }));

        return type0Ref;
    }

    /// <summary>Every kept CID is consecutive starting at 0 (by construction — see
    /// <see cref="TrueTypeSubsetter"/>'s new-GID assignment), so the whole width table fits the
    /// single-range <c>c [w1 w2 … wn]</c> form of <c>/W</c> (ISO 32000-1 §9.7.4.3) starting at
    /// CID 0 — no need to build the more general list-of-ranges form.</summary>
    private static PdfArray BuildWidthsArray(IReadOnlyDictionary<int, int> cidToWidthPdfUnits)
    {
        var widths = cidToWidthPdfUnits.OrderBy(kv => kv.Key).Select(kv => (PdfObject)new PdfNumber(kv.Value)).ToList();
        return new PdfArray([new PdfNumber(0), new PdfArray(widths)]);
    }

    private static byte[] BuildToUnicodeCMap(IReadOnlyDictionary<int, int> codepointToCid)
    {
        var cidToCodepoint = new Dictionary<int, int>();
        foreach (var (codepoint, cid) in codepointToCid)
            cidToCodepoint[cid] = codepoint; // documented edge case: if two codepoints share a glyph, only one wins here

        var entries = cidToCodepoint.OrderBy(kv => kv.Key).ToList();
        var sb = new System.Text.StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n");
        sb.Append("12 dict begin\n");
        sb.Append("begincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n");
        sb.Append("/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

        for (var i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.Append($"{chunk.Count} beginbfchar\n");
            foreach (var (cid, codepoint) in chunk)
                sb.Append($"<{cid:X4}> <{CmapHexFor(codepoint)}>\n");
            sb.Append("endbfchar\n");
        }

        sb.Append("endcmap\n");
        sb.Append("CMapName currentdict /CMap defineresource pop\n");
        sb.Append("end\n");
        sb.Append("end\n");

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string CmapHexFor(int codepoint)
    {
        if (codepoint <= 0xFFFF) return codepoint.ToString("X4");

        var sb = new System.Text.StringBuilder();
        foreach (var ch in char.ConvertFromUtf32(codepoint))
            sb.Append(((int)ch).ToString("X4"));
        return sb.ToString();
    }
}
