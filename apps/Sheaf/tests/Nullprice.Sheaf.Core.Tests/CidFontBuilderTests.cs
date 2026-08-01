namespace Nullprice.Sheaf.Core.Tests;

public class CidFontBuilderTests
{
    private static TrueTypeFont ParseFixture()
    {
        var result = TrueTypeFont.Parse(TrueTypeTestFixtures.Build());
        Assert.Null(result.Problem);
        return result.Font!;
    }

    [Fact]
    public void Embed_wires_a_Type0_font_with_Identity_H_encoding_and_a_CIDFontType2_descendant()
    {
        var font = ParseFixture();
        var subset = TrueTypeSubsetter.Subset(font, ['A', 'B'], "TestFont");

        var objects = new PdfObjectTable();
        var type0Ref = CidFontBuilder.Embed(objects, subset);

        var type0 = (PdfDictionary)objects.Resolve(type0Ref);
        Assert.Equal("Type0", ((PdfName)objects.Resolve(type0.Get("Subtype"))).Value);
        Assert.Equal("Identity-H", ((PdfName)objects.Resolve(type0.Get("Encoding"))).Value);

        var descendants = (PdfArray)objects.Resolve(type0.Get("DescendantFonts"));
        var cidFont = (PdfDictionary)objects.Resolve(descendants.Items[0]);
        Assert.Equal("CIDFontType2", ((PdfName)objects.Resolve(cidFont.Get("Subtype"))).Value);
        Assert.Equal("Identity", ((PdfName)objects.Resolve(cidFont.Get("CIDToGIDMap"))).Value);

        var descriptor = (PdfDictionary)objects.Resolve(cidFont.Get("FontDescriptor"));
        var fontFile = (PdfStream)objects.Resolve(descriptor.Get("FontFile2"));
        Assert.Equal(subset.FontBytes, fontFile.RawBytes);
    }

    [Fact]
    public void W_array_lists_a_width_for_every_cid_starting_at_zero()
    {
        var font = ParseFixture();
        var subset = TrueTypeSubsetter.Subset(font, ['A', 'B'], "TestFont"); // .notdef + A + B = 3 CIDs

        var objects = new PdfObjectTable();
        var type0Ref = CidFontBuilder.Embed(objects, subset);
        var cidFont = ResolveCidFont(objects, type0Ref);

        var w = (PdfArray)objects.Resolve(cidFont.Get("W"));
        Assert.Equal(0, ((PdfNumber)w.Items[0]).AsInt); // starting CID
        var widths = (PdfArray)w.Items[1];
        Assert.Equal(3, widths.Items.Count);
        Assert.All(widths.Items, item => Assert.Equal(500, ((PdfNumber)item).AsInt)); // fixture's flat 500-unit advance
    }

    [Fact]
    public void ToUnicode_cmap_recovers_the_original_codepoint_for_each_cid()
    {
        var font = ParseFixture();
        var subset = TrueTypeSubsetter.Subset(font, ['A', 'B'], "TestFont");

        var objects = new PdfObjectTable();
        var type0Ref = CidFontBuilder.Embed(objects, subset);
        var type0 = (PdfDictionary)objects.Resolve(type0Ref);
        var toUnicodeStream = (PdfStream)objects.Resolve(type0.Get("ToUnicode"));
        var text = System.Text.Encoding.ASCII.GetString(toUnicodeStream.RawBytes);

        var cidForA = subset.CodepointToCid['A'].ToString("X4");
        var unicodeForA = ((int)'A').ToString("X4");
        Assert.Contains($"<{cidForA}> <{unicodeForA}>", text);
        Assert.Contains("beginbfchar", text);
    }

    private static PdfDictionary ResolveCidFont(PdfObjectTable objects, PdfReference type0Ref)
    {
        var type0 = (PdfDictionary)objects.Resolve(type0Ref);
        var descendants = (PdfArray)objects.Resolve(type0.Get("DescendantFonts"));
        return (PdfDictionary)objects.Resolve(descendants.Items[0]);
    }
}
