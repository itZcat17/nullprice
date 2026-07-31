namespace Nullprice.Sheaf.Core.Tests;

public class EmbeddedFontExtractorTests
{
    [Fact]
    public void Extracts_code_to_unicode_and_widths_for_a_simple_truetype_font()
    {
        var doc = PdfDocument.Open(PdfTestFixtures.BuildDocumentWithTrueTypeText("Hi")).Document!;
        var fontDict = PdfTestFixtures.ResolveFont(doc, "F1");

        var extracted = EmbeddedFontExtractor.Extract(doc.Objects, fontDict);

        Assert.NotNull(extracted);
        Assert.Equal('H', extracted!.CodeToUnicode[(int)'H']);
        Assert.True(extracted.UnicodeToCode.ContainsKey('H'));
        Assert.Equal(0.5, extracted.CodeToWidthEm[(int)'H'], 3);
    }

    [Fact]
    public void Returns_null_for_a_non_truetype_font()
    {
        var fontDict = new PdfDictionary(new Dictionary<string, PdfObject> { ["Subtype"] = new PdfName("Type1") });

        Assert.Null(EmbeddedFontExtractor.Extract(new PdfObjectTable(), fontDict));
    }

    [Fact]
    public void Returns_null_when_there_is_no_embedded_font_file()
    {
        var fontDict = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Subtype"] = new PdfName("TrueType"),
            ["FirstChar"] = new PdfNumber(32),
            ["Widths"] = new PdfArray([new PdfNumber(500)]),
        });

        Assert.Null(EmbeddedFontExtractor.Extract(new PdfObjectTable(), fontDict));
    }

    [Fact]
    public void Differences_override_the_base_winansi_mapping()
    {
        var objects = new PdfObjectTable();

        var fontFileRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(fontFileRef.Number, fontFileRef.Generation, new PdfStream(PdfDictionary.Empty, [0x00]));

        var descriptorRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(descriptorRef.Number, descriptorRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["FontFile2"] = fontFileRef,
        }));

        // Code 65 ('A' under plain WinAnsi) remapped to "bullet" via /Differences.
        var encodingRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(encodingRef.Number, encodingRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Differences"] = new PdfArray([new PdfNumber(65), new PdfName("bullet")]),
        }));

        var fontDict = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Subtype"] = new PdfName("TrueType"),
            ["FirstChar"] = new PdfNumber(65),
            ["Widths"] = new PdfArray([new PdfNumber(500)]),
            ["FontDescriptor"] = descriptorRef,
            ["Encoding"] = encodingRef,
        });

        var extracted = EmbeddedFontExtractor.Extract(objects, fontDict);

        Assert.NotNull(extracted);
        Assert.Equal(0x2022, extracted!.CodeToUnicode[65]);
    }
}
