namespace Nullprice.Sheaf.Core.Tests;

public class PageTreeInheritanceTests
{
    [Fact]
    public void Page_without_its_own_mediabox_inherits_the_pages_nodes_mediabox()
    {
        var objects = new PdfObjectTable();

        var pageRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(pageRef.Number, pageRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Page"), // deliberately no MediaBox of its own
        }));

        var pagesRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(pagesRef.Number, pagesRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Pages"),
            ["Kids"] = new PdfArray([pageRef]),
            ["Count"] = new PdfNumber(1),
            ["MediaBox"] = new PdfArray([new PdfNumber(0), new PdfNumber(0), new PdfNumber(612), new PdfNumber(792)]),
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
        var bytes = PdfWriter.Write(PdfDocument.Create(objects, trailer));

        var doc = PdfDocument.Open(bytes).Document!;
        var box = doc.Objects.Resolve(doc.Pages[0].Dictionary.Get("MediaBox")) as PdfArray;

        Assert.Equal(612, (int)(box!.Items[2] as PdfNumber)!.Value);
    }
}
