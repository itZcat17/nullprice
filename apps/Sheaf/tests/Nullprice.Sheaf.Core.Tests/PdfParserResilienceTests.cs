namespace Nullprice.Sheaf.Core.Tests;

public class PdfParserResilienceTests
{
    [Fact]
    public void A_broken_startxref_still_opens_via_linear_scan()
    {
        var bytes = PdfTestFixtures.BuildDocument(2);
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        var corrupted = System.Text.Encoding.Latin1.GetBytes(text.Replace("startxref", "xxxxxxxxx"));

        var result = PdfDocument.Open(corrupted);

        Assert.Equal(PdfOpenStatus.Success, result.Status);
        Assert.Equal(2, result.Document!.Pages.Count);
    }

    [Fact]
    public void Garbage_bytes_report_a_parse_error_rather_than_throwing()
    {
        var result = PdfDocument.Open("this is not a pdf at all"u8.ToArray());

        Assert.Equal(PdfOpenStatus.ParseError, result.Status);
        Assert.Null(result.Document);
    }
}
