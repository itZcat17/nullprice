namespace Nullprice.Sheaf.Core.Tests;

public class AnnotationWriterTests
{
    [Fact]
    public void Highlight_writes_quadpoints_color_and_a_translucent_appearance_stream()
    {
        var (objects, pageRef, trailer) = BuildBlankPage();
        AnnotationWriter.Apply(objects, pageRef, new HighlightEdit(0, X: 100, Y: 200, W: 50, H: 20, ColorHex: "#7F7F00"));

        var doc = WriteAndReopen(objects, trailer);
        var annot = FirstAnnotation(doc);

        Assert.Equal("Highlight", ((PdfName)doc.Objects.Resolve(annot.Get("Subtype"))).Value);

        var quad = (PdfArray)doc.Objects.Resolve(annot.Get("QuadPoints"));
        var values = quad.Items.Select(i => ((PdfNumber)i).Value).ToArray();
        Assert.Equal(new double[] { 100, 220, 150, 220, 100, 200, 150, 200 }, values);

        // The fractional color component (127/255) round-tripping correctly through the writer
        // and back is what proves PdfNumber's IsInteger footgun (defaults to true, which would
        // truncate 0.4980... to 0 if a helper forgot to pass IsInteger: false) is actually avoided.
        var color = (PdfArray)doc.Objects.Resolve(annot.Get("C"));
        Assert.True(Math.Abs(((PdfNumber)color.Items[0]).Value - 127 / 255.0) < 0.01);

        var apStream = ResolveAppearanceStream(doc, annot);
        var content = System.Text.Encoding.ASCII.GetString(doc.GetStreamData(apStream));
        Assert.Contains(" re", content);
        Assert.Contains("/Alpha gs", content);
        var ops = ContentStreamReader.Read(doc.GetStreamData(apStream));
        Assert.Contains(ops, o => o.Operator == "f");
    }

    [Fact]
    public void Underline_and_strikeout_place_their_line_at_different_heights_within_the_box()
    {
        var (objects, pageRef, trailer) = BuildBlankPage();
        AnnotationWriter.Apply(objects, pageRef, new UnderlineEdit(0, X: 0, Y: 0, W: 100, H: 20, ColorHex: "#FF0000"));
        AnnotationWriter.Apply(objects, pageRef, new StrikeOutEdit(0, X: 0, Y: 0, W: 100, H: 20, ColorHex: "#FF0000"));

        var doc = WriteAndReopen(objects, trailer);
        var annots = ((PdfArray)doc.Objects.Resolve(doc.Pages[0].Dictionary.Get("Annots"))).Items
            .Select(i => (PdfDictionary)doc.Objects.Resolve(i)).ToList();

        var underline = annots.Single(a => ((PdfName)doc.Objects.Resolve(a.Get("Subtype"))).Value == "Underline");
        var strikeOut = annots.Single(a => ((PdfName)doc.Objects.Resolve(a.Get("Subtype"))).Value == "StrikeOut");

        var underlineContent = System.Text.Encoding.ASCII.GetString(doc.GetStreamData(ResolveAppearanceStream(doc, underline)));
        var strikeOutContent = System.Text.Encoding.ASCII.GetString(doc.GetStreamData(ResolveAppearanceStream(doc, strikeOut)));

        // Underline sits near the bottom of the box (y ~= 1.6), strikeout through the middle (y = 10).
        Assert.Contains("1.6", underlineContent);
        Assert.Contains(" 10 m", strikeOutContent);
    }

    [Fact]
    public void StickyNote_encodes_unicode_text_as_a_utf16be_text_string_with_bom()
    {
        var (objects, pageRef, trailer) = BuildBlankPage();
        AnnotationWriter.Apply(objects, pageRef, new StickyNoteEdit(0, X: 10, Y: 20, Text: "café — note"));

        var doc = WriteAndReopen(objects, trailer);
        var annot = FirstAnnotation(doc);

        Assert.Equal("Text", ((PdfName)doc.Objects.Resolve(annot.Get("Subtype"))).Value);
        Assert.Null(annot.Get("AP")); // /Subtype /Text relies on the viewer's own standard icon.

        var contents = (PdfString)doc.Objects.Resolve(annot.Get("Contents"));
        Assert.Equal(0xFE, contents.Bytes[0]);
        Assert.Equal(0xFF, contents.Bytes[1]);
        var decoded = System.Text.Encoding.BigEndianUnicode.GetString(contents.Bytes, 2, contents.Bytes.Length - 2);
        Assert.Equal("café — note", decoded);
    }

    [Fact]
    public void LineShape_with_arrow_writes_open_arrow_line_ending_and_a_filled_arrowhead()
    {
        var (objects, pageRef, trailer) = BuildBlankPage();
        AnnotationWriter.Apply(objects, pageRef, new LineShapeEdit(0, X1: 10, Y1: 10, X2: 110, Y2: 10, ColorHex: "#0000FF", LineWidth: 2, Arrow: true));

        var doc = WriteAndReopen(objects, trailer);
        var annot = FirstAnnotation(doc);

        Assert.Equal("Line", ((PdfName)doc.Objects.Resolve(annot.Get("Subtype"))).Value);
        var line = (PdfArray)doc.Objects.Resolve(annot.Get("L"));
        Assert.Equal(new double[] { 10, 10, 110, 10 }, line.Items.Select(i => ((PdfNumber)i).Value).ToArray());

        var le = (PdfArray)doc.Objects.Resolve(annot.Get("LE"));
        Assert.Equal("OpenArrow", ((PdfName)le.Items[1]).Value);

        var apStream = ResolveAppearanceStream(doc, annot);
        var ops = ContentStreamReader.Read(doc.GetStreamData(apStream));
        Assert.Contains(ops, o => o.Operator == "f"); // the arrowhead triangle is filled
    }

    [Theory]
    [InlineData(null)]
    [InlineData("#00FF00")]
    public void RectShape_writes_square_subtype_with_optional_interior_color(string? fillHex)
    {
        var (objects, pageRef, trailer) = BuildBlankPage();
        AnnotationWriter.Apply(objects, pageRef, new RectShapeEdit(0, X: 20, Y: 30, W: 40, H: 25, ColorHex: "#000000", LineWidth: 1, FillHex: fillHex));

        var doc = WriteAndReopen(objects, trailer);
        var annot = FirstAnnotation(doc);

        Assert.Equal("Square", ((PdfName)doc.Objects.Resolve(annot.Get("Subtype"))).Value);
        Assert.Equal(fillHex is not null, annot.Get("IC") is not null);

        var apStream = ResolveAppearanceStream(doc, annot);
        var content = System.Text.Encoding.ASCII.GetString(doc.GetStreamData(apStream));
        Assert.Contains(" re", content);
        var ops = ContentStreamReader.Read(doc.GetStreamData(apStream));
        Assert.Contains(ops, o => o.Operator == (fillHex is not null ? "B" : "S"));
    }

    [Fact]
    public void EllipseShape_writes_circle_subtype_with_a_four_bezier_appearance()
    {
        var (objects, pageRef, trailer) = BuildBlankPage();
        AnnotationWriter.Apply(objects, pageRef, new EllipseShapeEdit(0, X: 0, Y: 0, W: 100, H: 50, ColorHex: "#123456", LineWidth: 2, FillHex: null));

        var doc = WriteAndReopen(objects, trailer);
        var annot = FirstAnnotation(doc);

        Assert.Equal("Circle", ((PdfName)doc.Objects.Resolve(annot.Get("Subtype"))).Value);

        var ops = ContentStreamReader.Read(doc.GetStreamData(ResolveAppearanceStream(doc, annot)));
        Assert.Equal(4, ops.Count(o => o.Operator == "c")); // four Bezier curves approximate the ellipse
        Assert.Contains(ops, o => o.Operator == "h"); // closes the path back to the start point
    }

    [Fact]
    public void Ink_writes_one_stroke_per_entry_in_inklist_and_the_appearance_traces_every_point()
    {
        var (objects, pageRef, trailer) = BuildBlankPage();
        IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes =
        [
            [(10, 10), (20, 15), (30, 10)],
            [(50, 50), (60, 55)],
        ];
        AnnotationWriter.Apply(objects, pageRef, new InkEdit(0, strokes, ColorHex: "#333333", LineWidth: 3));

        var doc = WriteAndReopen(objects, trailer);
        var annot = FirstAnnotation(doc);

        Assert.Equal("Ink", ((PdfName)doc.Objects.Resolve(annot.Get("Subtype"))).Value);
        var inkList = (PdfArray)doc.Objects.Resolve(annot.Get("InkList"));
        Assert.Equal(2, inkList.Items.Count);
        Assert.Equal(6, ((PdfArray)inkList.Items[0]).Items.Count); // 3 points * 2 coords
        Assert.Equal(4, ((PdfArray)inkList.Items[1]).Items.Count); // 2 points * 2 coords

        var ops = ContentStreamReader.Read(doc.GetStreamData(ResolveAppearanceStream(doc, annot)));
        Assert.Equal(2, ops.Count(o => o.Operator == "m")); // one moveto per stroke
        Assert.Equal(3, ops.Count(o => o.Operator == "l")); // (3-1) + (2-1) linetos across both strokes
    }

    [Fact]
    public void Multiple_annotations_on_the_same_page_accumulate_instead_of_overwriting()
    {
        var (objects, pageRef, trailer) = BuildBlankPage();
        AnnotationWriter.Apply(objects, pageRef, new HighlightEdit(0, 0, 0, 10, 10, "#FFFF00"));
        AnnotationWriter.Apply(objects, pageRef, new StickyNoteEdit(0, 20, 20, "note one"));
        AnnotationWriter.Apply(objects, pageRef, new RectShapeEdit(0, 30, 30, 10, 10, "#000000", 1, null));

        var doc = WriteAndReopen(objects, trailer);
        var annots = (PdfArray)doc.Objects.Resolve(doc.Pages[0].Dictionary.Get("Annots"));

        Assert.Equal(3, annots.Items.Count);
        var subtypes = annots.Items
            .Select(i => (PdfDictionary)doc.Objects.Resolve(i))
            .Select(d => ((PdfName)doc.Objects.Resolve(d.Get("Subtype"))).Value)
            .ToList();
        Assert.Equal(["Highlight", "Text", "Square"], subtypes);
    }

    [Fact]
    public void FreeText_embeds_a_subsetted_font_and_draws_the_typed_text_with_it()
    {
        var (objects, pageRef, trailer) = BuildBlankPage();
        AnnotationWriter.Apply(objects, pageRef, new FreeTextEdit(
            0, X: 50, Y: 700, FontSize: 24, Text: "AB", ColorHex: "#123456",
            FontBytes: TrueTypeTestFixtures.Build(), FontFamilyName: "Test Font"));

        var doc = WriteAndReopen(objects, trailer);
        var annot = FirstAnnotation(doc);

        Assert.Equal("FreeText", ((PdfName)doc.Objects.Resolve(annot.Get("Subtype"))).Value);

        var contents = (PdfString)doc.Objects.Resolve(annot.Get("Contents"));
        var decoded = System.Text.Encoding.BigEndianUnicode.GetString(contents.Bytes, 2, contents.Bytes.Length - 2);
        Assert.Equal("AB", decoded);

        var apStream = ResolveAppearanceStream(doc, annot);
        var apResources = (PdfDictionary)doc.Objects.Resolve(apStream.Dictionary.Get("Resources"));
        var apFonts = (PdfDictionary)doc.Objects.Resolve(apResources.Get("Font"));
        var embeddedType0 = (PdfDictionary)doc.Objects.Resolve(apFonts.Get("F1"));
        Assert.Equal("Type0", ((PdfName)doc.Objects.Resolve(embeddedType0.Get("Subtype"))).Value);
        Assert.Equal("Identity-H", ((PdfName)doc.Objects.Resolve(embeddedType0.Get("Encoding"))).Value);

        var ops = ContentStreamReader.Read(doc.GetStreamData(apStream));
        Assert.Contains(ops, o => o.Operator == "Tj");
        var tjOp = ops.Single(o => o.Operator == "Tj");
        var shownBytes = ((PdfString)tjOp.Operands[0]).Bytes;
        Assert.Equal(4, shownBytes.Length); // two CIDs, 2 bytes each, for "AB"
    }

    [Fact]
    public void FreeText_with_an_unparseable_font_is_skipped_rather_than_throwing()
    {
        var (objects, pageRef, trailer) = BuildBlankPage();
        AnnotationWriter.Apply(objects, pageRef, new FreeTextEdit(
            0, X: 50, Y: 700, FontSize: 24, Text: "AB", ColorHex: "#123456",
            FontBytes: new byte[16], FontFamilyName: "Not A Real Font"));

        var doc = WriteAndReopen(objects, trailer);
        Assert.Null(doc.Pages[0].Dictionary.Get("Annots"));
    }

    [Fact]
    public void ImageStamp_embeds_a_DCTDecode_image_xobject_and_draws_it_scaled_and_positioned()
    {
        var (objects, pageRef, trailer) = BuildBlankPage();
        var fakeJpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }; // a real decode isn't needed to test the PDF wiring
        AnnotationWriter.Apply(objects, pageRef, new ImageStampEdit(
            0, X: 100, Y: 200, W: 150, H: 75, JpegBytes: fakeJpegBytes, PixelWidth: 300, PixelHeight: 150));

        var doc = WriteAndReopen(objects, trailer);
        var annot = FirstAnnotation(doc);

        Assert.Equal("Stamp", ((PdfName)doc.Objects.Resolve(annot.Get("Subtype"))).Value);
        var rect = (PdfArray)doc.Objects.Resolve(annot.Get("Rect"));
        Assert.Equal(new double[] { 100, 200, 250, 275 }, rect.Items.Select(i => ((PdfNumber)i).Value).ToArray());

        var apStream = ResolveAppearanceStream(doc, annot);
        var apResources = (PdfDictionary)doc.Objects.Resolve(apStream.Dictionary.Get("Resources"));
        var apXObjects = (PdfDictionary)doc.Objects.Resolve(apResources.Get("XObject"));
        var imageXObject = (PdfStream)doc.Objects.Resolve(apXObjects.Get("Im1"));

        Assert.Equal("Image", ((PdfName)doc.Objects.Resolve(imageXObject.Dictionary.Get("Subtype"))).Value);
        Assert.Equal("DCTDecode", ((PdfName)doc.Objects.Resolve(imageXObject.Dictionary.Get("Filter"))).Value);
        Assert.Equal(300, ((PdfNumber)doc.Objects.Resolve(imageXObject.Dictionary.Get("Width"))).AsInt);
        Assert.Equal(150, ((PdfNumber)doc.Objects.Resolve(imageXObject.Dictionary.Get("Height"))).AsInt);
        // DCTDecode (JPEG) is a filter Sheaf's FilterCodec never decodes (Core has no JPEG
        // codec — it only ever copies these bytes through), so the raw stream bytes are the
        // right thing to compare, not GetStreamData (which would throw UnsupportedFilterException).
        Assert.Equal(fakeJpegBytes, imageXObject.RawBytes);

        var ops = ContentStreamReader.Read(doc.GetStreamData(apStream));
        var cmOp = ops.Single(o => o.Operator == "cm");
        var cmValues = cmOp.Operands.Select(o => ((PdfNumber)o).Value).ToArray();
        Assert.Equal(new double[] { 150, 0, 0, 75, 100, 200 }, cmValues); // [W 0 0 H X Y] places the unit-square image
        Assert.Contains(ops, o => o.Operator == "Do" && ((PdfName)o.Operands[0]).Value == "Im1");
    }

    // ---- fixtures -------------------------------------------------------------------------

    private static (PdfObjectTable Objects, PdfReference PageRef, PdfDictionary Trailer) BuildBlankPage()
    {
        var objects = new PdfObjectTable();

        var contentRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(contentRef.Number, contentRef.Generation, new PdfStream(PdfDictionary.Empty, []));

        var pageRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(pageRef.Number, pageRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Page"),
            ["MediaBox"] = new PdfArray([new PdfNumber(0), new PdfNumber(0), new PdfNumber(612), new PdfNumber(792)]),
            ["Contents"] = contentRef,
            ["Resources"] = PdfDictionary.Empty,
        }));

        var pagesRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(pagesRef.Number, pagesRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Pages"),
            ["Kids"] = new PdfArray([pageRef]),
            ["Count"] = new PdfNumber(1),
        }));

        var pageDict = (PdfDictionary)objects.All[(pageRef.Number, pageRef.Generation)];
        objects.Set(pageRef.Number, pageRef.Generation, pageDict.With("Parent", pagesRef));

        var rootRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(rootRef.Number, rootRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Catalog"),
            ["Pages"] = pagesRef,
        }));

        var trailer = new PdfDictionary(new Dictionary<string, PdfObject> { ["Root"] = rootRef });
        return (objects, pageRef, trailer);
    }

    private static PdfDocument WriteAndReopen(PdfObjectTable objects, PdfDictionary trailer)
    {
        var bytes = PdfWriter.Write(PdfDocument.Create(objects, trailer));
        var opened = PdfDocument.Open(bytes);
        Assert.Equal(PdfOpenStatus.Success, opened.Status);
        return opened.Document!;
    }

    private static PdfDictionary FirstAnnotation(PdfDocument doc, int pageIndex = 0)
    {
        var annots = (PdfArray)doc.Objects.Resolve(doc.Pages[pageIndex].Dictionary.Get("Annots"));
        return (PdfDictionary)doc.Objects.Resolve(annots.Items[0]);
    }

    private static PdfStream ResolveAppearanceStream(PdfDocument doc, PdfDictionary annot)
    {
        var ap = (PdfDictionary)doc.Objects.Resolve(annot.Get("AP"));
        return (PdfStream)doc.Objects.Resolve(ap.Get("N"));
    }
}
