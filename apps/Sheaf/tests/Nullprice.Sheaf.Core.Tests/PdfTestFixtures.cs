namespace Nullprice.Sheaf.Core.Tests;

/// <summary>Builds minimal, valid multi-page PDFs entirely through the object model plus
/// <see cref="PdfWriter"/> — not hand-typed bytes — so tests exercise the real write path.
/// Each page's MediaBox width is distinct (100 + page index), which gives tests a cheap way
/// to identify a page after it's been merged, reordered, or rotated.</summary>
internal static class PdfTestFixtures
{
    public static byte[] BuildDocument(int pageCount)
    {
        var objects = new PdfObjectTable();
        var pageRefs = new List<PdfReference>();

        for (var i = 0; i < pageCount; i++)
        {
            var contentBytes = System.Text.Encoding.ASCII.GetBytes($"BT /F1 12 Tf 10 10 Td (Page {i}) Tj ET");
            var contentNum = objects.Allocate();
            objects.Set(contentNum, 0, new PdfStream(PdfDictionary.Empty, contentBytes));

            var pageNum = objects.Allocate();
            var pageRef = new PdfReference(pageNum, 0);
            objects.Set(pageNum, 0, new PdfDictionary(new Dictionary<string, PdfObject>
            {
                ["Type"] = new PdfName("Page"),
                ["MediaBox"] = new PdfArray([new PdfNumber(0), new PdfNumber(0), new PdfNumber(100 + i), new PdfNumber(200)]),
                ["Contents"] = new PdfReference(contentNum, 0),
                ["Resources"] = PdfDictionary.Empty,
            }));
            pageRefs.Add(pageRef);
        }

        var pagesRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(pagesRef.Number, pagesRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Pages"),
            ["Kids"] = new PdfArray(pageRefs.Cast<PdfObject>().ToList()),
            ["Count"] = new PdfNumber(pageCount),
        }));

        foreach (var pageRef in pageRefs)
        {
            if (objects.TryGet(pageRef.Number, pageRef.Generation, out var obj) && obj is PdfDictionary d)
                objects.Set(pageRef.Number, pageRef.Generation, d.With("Parent", pagesRef));
        }

        var rootRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(rootRef.Number, rootRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Catalog"),
            ["Pages"] = pagesRef,
        }));

        var trailer = new PdfDictionary(new Dictionary<string, PdfObject> { ["Root"] = rootRef });
        return PdfWriter.Write(PdfDocument.Create(objects, trailer));
    }

    public static int MediaBoxWidth(PdfDocument doc, PdfPageRef page)
    {
        var box = doc.Objects.Resolve(page.Dictionary.Get("MediaBox")) as PdfArray;
        return (int)(box!.Items[2] as PdfNumber)!.Value;
    }

    /// <summary>A single-page document with a text run ("Secret" at (50,700), 24pt) and a
    /// JPEG-filtered image XObject ("Im1", placed at (100,100) sized 50x50 via <c>cm</c>) — a
    /// fixture for redaction and compression tests, which need real, locatable content rather
    /// than the plain text-only pages <see cref="BuildDocument"/> produces.</summary>
    public static byte[] BuildDocumentWithTextAndImage(int imageByteCount = 200)
    {
        var objects = new PdfObjectTable();

        var imageRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(imageRef.Number, imageRef.Generation, new PdfStream(new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("XObject"),
            ["Subtype"] = new PdfName("Image"),
            ["Filter"] = new PdfName("DCTDecode"),
            ["Width"] = new PdfNumber(10),
            ["Height"] = new PdfNumber(10),
        }), new byte[imageByteCount]));

        var resources = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["XObject"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["Im1"] = imageRef }),
        });

        var contentText = "BT /F1 24 Tf 50 700 Td (Secret) Tj ET\nq 50 0 0 50 100 100 cm /Im1 Do Q";
        var contentRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(contentRef.Number, contentRef.Generation, new PdfStream(PdfDictionary.Empty, System.Text.Encoding.ASCII.GetBytes(contentText)));

        var pageRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(pageRef.Number, pageRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Page"),
            ["MediaBox"] = new PdfArray([new PdfNumber(0), new PdfNumber(0), new PdfNumber(612), new PdfNumber(792)]),
            ["Contents"] = contentRef,
            ["Resources"] = resources,
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
        return PdfWriter.Write(PdfDocument.Create(objects, trailer));
    }

    public static PdfStream FirstImageXObject(PdfDocument doc, int pageIndex = 0)
    {
        var resources = doc.Objects.Resolve(doc.Pages[pageIndex].Dictionary.Get("Resources")) as PdfDictionary;
        var xobjects = doc.Objects.Resolve(resources!.Get("XObject")) as PdfDictionary;
        var value = xobjects!.Entries.First().Value;
        return (PdfStream)doc.Objects.Resolve(value);
    }

    /// <summary>A single-page document whose text is drawn with a simple TrueType font
    /// resource named "F1", covering character codes 32-126 (so any plain-ASCII edit stays
    /// within its declared range) at a flat 500/1000-em width. The FontFile2 bytes are an
    /// inert placeholder — <see cref="EmbeddedFontExtractor"/> never parses font byte content,
    /// only /Widths, /Encoding, /Differences, and /FirstChar, so nothing here needs a real
    /// sfnt (that's exercised separately once font subsetting exists).</summary>
    public static byte[] BuildDocumentWithTrueTypeText(string text, double x = 50, double y = 700, double fontSize = 24)
    {
        var objects = new PdfObjectTable();

        var fontFileRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(fontFileRef.Number, fontFileRef.Generation, new PdfStream(PdfDictionary.Empty, [0x00, 0x01, 0x00, 0x00]));

        var descriptorRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(descriptorRef.Number, descriptorRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("FontDescriptor"),
            ["FontName"] = new PdfName("TestFont"),
            ["FontFile2"] = fontFileRef,
        }));

        const int firstChar = 32;
        const int lastChar = 126;
        var widths = Enumerable.Range(firstChar, lastChar - firstChar + 1).Select(_ => (PdfObject)new PdfNumber(500)).ToList();

        var fontRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(fontRef.Number, fontRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Font"),
            ["Subtype"] = new PdfName("TrueType"),
            ["BaseFont"] = new PdfName("TestFont"),
            ["FirstChar"] = new PdfNumber(firstChar),
            ["LastChar"] = new PdfNumber(lastChar),
            ["Widths"] = new PdfArray(widths),
            ["FontDescriptor"] = descriptorRef,
        }));

        var resources = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Font"] = new PdfDictionary(new Dictionary<string, PdfObject> { ["F1"] = fontRef }),
        });

        var contentText = $"BT /F1 {fontSize} Tf {x} {y} Td ({text}) Tj ET";
        var contentRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(contentRef.Number, contentRef.Generation, new PdfStream(PdfDictionary.Empty, System.Text.Encoding.ASCII.GetBytes(contentText)));

        var pageRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(pageRef.Number, pageRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Page"),
            ["MediaBox"] = new PdfArray([new PdfNumber(0), new PdfNumber(0), new PdfNumber(612), new PdfNumber(792)]),
            ["Contents"] = contentRef,
            ["Resources"] = resources,
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
        return PdfWriter.Write(PdfDocument.Create(objects, trailer));
    }

    public static PdfDictionary ResolveFont(PdfDocument doc, string resourceName, int pageIndex = 0)
    {
        var resources = doc.Objects.Resolve(doc.Pages[pageIndex].Dictionary.Get("Resources")) as PdfDictionary;
        var fonts = doc.Objects.Resolve(resources!.Get("Font")) as PdfDictionary;
        return (PdfDictionary)doc.Objects.Resolve(fonts!.Get(resourceName));
    }
}
