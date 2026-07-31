namespace Nullprice.Sheaf.Core.Tests;

public class PdfWriterRoundTripTests
{
    [Fact]
    public void Writer_output_reopens_with_the_same_page_count_and_media_boxes()
    {
        var bytes = PdfTestFixtures.BuildDocument(3);

        var result = PdfDocument.Open(bytes);

        Assert.Equal(PdfOpenStatus.Success, result.Status);
        var doc = result.Document!;
        Assert.Equal(3, doc.Pages.Count);
        for (var i = 0; i < 3; i++)
            Assert.Equal(100 + i, PdfTestFixtures.MediaBoxWidth(doc, doc.Pages[i]));
    }

    [Fact]
    public void Writer_output_preserves_page_content_stream_bytes()
    {
        var bytes = PdfTestFixtures.BuildDocument(1);
        var doc = PdfDocument.Open(bytes).Document!;

        var page = doc.Pages[0];
        var contentStream = doc.Objects.Resolve(page.Dictionary.Get("Contents")) as PdfStream;
        var content = System.Text.Encoding.ASCII.GetString(doc.GetStreamData(contentStream!));

        Assert.Contains("(Page 0)", content);
    }

    [Fact]
    public void Writer_output_starts_with_the_pdf_header()
    {
        var bytes = PdfTestFixtures.BuildDocument(1);
        var header = System.Text.Encoding.ASCII.GetString(bytes, 0, 5);
        Assert.Equal("%PDF-", header);
    }

    [Fact]
    public void Empty_document_opens_with_zero_pages()
    {
        var bytes = PdfTestFixtures.BuildDocument(0);
        var result = PdfDocument.Open(bytes);

        Assert.Equal(PdfOpenStatus.Success, result.Status);
        Assert.Empty(result.Document!.Pages);
    }
}
