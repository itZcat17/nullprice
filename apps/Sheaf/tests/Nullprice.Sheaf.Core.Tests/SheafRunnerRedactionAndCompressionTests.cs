namespace Nullprice.Sheaf.Core.Tests;

public class SheafRunnerRedactionAndCompressionTests
{
    [Fact]
    public async Task Redaction_removes_targeted_text_from_the_written_output()
    {
        using var sandbox = new Sandbox();
        var source = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocumentWithTextAndImage());
        var outPath = Path.Combine(sandbox.Out, "redacted.pdf");

        var regions = new[] { new RedactionRegion(PageIndex: 0, X: 40, Y: 690, Width: 200, Height: 60) };
        var plan = SheafPlanner.Build(
            [new MergeSource(source)],
            [new SheafOutput(outPath, [], Redactions: regions)]);

        var report = await new SheafRunner().RunAsync(plan);

        Assert.True(report.IsClean);
        var doc = PdfDocument.Open(await File.ReadAllBytesAsync(outPath)).Document!;
        var contentStream = doc.Objects.Resolve(doc.Pages[0].Dictionary.Get("Contents")) as PdfStream;
        var content = System.Text.Encoding.ASCII.GetString(doc.GetStreamData(contentStream!));

        Assert.DoesNotContain("Secret", content);
    }

    [Fact]
    public async Task Redaction_leaves_unrelated_text_and_the_image_alone()
    {
        using var sandbox = new Sandbox();
        var source = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocumentWithTextAndImage());
        var outPath = Path.Combine(sandbox.Out, "redacted.pdf");

        // A region nowhere near either the text or the image.
        var regions = new[] { new RedactionRegion(PageIndex: 0, X: 500, Y: 500, Width: 20, Height: 20) };
        var plan = SheafPlanner.Build(
            [new MergeSource(source)],
            [new SheafOutput(outPath, [], Redactions: regions)]);

        var report = await new SheafRunner().RunAsync(plan);

        Assert.True(report.IsClean);
        var doc = PdfDocument.Open(await File.ReadAllBytesAsync(outPath)).Document!;
        var contentStream = doc.Objects.Resolve(doc.Pages[0].Dictionary.Get("Contents")) as PdfStream;
        var content = System.Text.Encoding.ASCII.GetString(doc.GetStreamData(contentStream!));

        Assert.Contains("Secret", content);
        Assert.Contains("Do", content);
    }

    [Fact]
    public async Task Compression_shrinks_the_image_bytes_in_the_written_output()
    {
        using var sandbox = new Sandbox();
        var source = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocumentWithTextAndImage(imageByteCount: 200));
        var outPath = Path.Combine(sandbox.Out, "compressed.pdf");

        var plan = SheafPlanner.Build(
            [new MergeSource(source)],
            [new SheafOutput(outPath, [], Compression: new CompressionSettings(50))]);

        var report = await new SheafRunner(new FakeRecompressor()).RunAsync(plan);

        Assert.True(report.IsClean);
        var doc = PdfDocument.Open(await File.ReadAllBytesAsync(outPath)).Document!;
        var image = PdfTestFixtures.FirstImageXObject(doc);

        Assert.Equal(101, image.RawBytes.Length); // FakeRecompressor halves (200/2 + 1)
    }

    [Fact]
    public async Task Compression_without_a_configured_recompressor_fails_that_output()
    {
        using var sandbox = new Sandbox();
        var source = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocumentWithTextAndImage());
        var outPath = Path.Combine(sandbox.Out, "compressed.pdf");

        var plan = SheafPlanner.Build(
            [new MergeSource(source)],
            [new SheafOutput(outPath, [], Compression: new CompressionSettings(50))]);

        var report = await new SheafRunner().RunAsync(plan); // no recompressor configured

        Assert.False(report.IsClean);
        Assert.Equal(SheafOutcome.Failed, report.Results.Single().Outcome);
    }
}
