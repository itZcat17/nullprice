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
}
