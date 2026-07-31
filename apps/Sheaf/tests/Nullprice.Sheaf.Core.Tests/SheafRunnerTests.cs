namespace Nullprice.Sheaf.Core.Tests;

public class SheafRunnerTests
{
    [Fact]
    public async Task Merges_pages_from_two_sources_in_source_order()
    {
        using var sandbox = new Sandbox();
        var a = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocument(2)); // widths 100, 101
        var b = sandbox.AddPdf("b.pdf", PdfTestFixtures.BuildDocument(1)); // width 100 (its own page 0)
        var outPath = Path.Combine(sandbox.Out, "merged.pdf");

        var plan = SheafPlanner.Build(
            [new MergeSource(a), new MergeSource(b)],
            [new SheafOutput(outPath, [])]);

        var report = await new SheafRunner().RunAsync(plan);

        Assert.True(report.IsClean);
        var merged = PdfDocument.Open(await File.ReadAllBytesAsync(outPath)).Document!;
        Assert.Equal(3, merged.Pages.Count);
        Assert.Equal(100, PdfTestFixtures.MediaBoxWidth(merged, merged.Pages[0]));
        Assert.Equal(101, PdfTestFixtures.MediaBoxWidth(merged, merged.Pages[1]));
        Assert.Equal(100, PdfTestFixtures.MediaBoxWidth(merged, merged.Pages[2]));
    }

    [Fact]
    public async Task Extracts_a_page_subset_via_page_indices()
    {
        using var sandbox = new Sandbox();
        var a = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocument(3)); // widths 100, 101, 102
        var outPath = Path.Combine(sandbox.Out, "extract.pdf");

        var plan = SheafPlanner.Build(
            [new MergeSource(a, PageIndices: [2, 0])],
            [new SheafOutput(outPath, [])]);

        var report = await new SheafRunner().RunAsync(plan);

        Assert.True(report.IsClean);
        var extracted = PdfDocument.Open(await File.ReadAllBytesAsync(outPath)).Document!;
        Assert.Equal(2, extracted.Pages.Count);
        Assert.Equal(102, PdfTestFixtures.MediaBoxWidth(extracted, extracted.Pages[0]));
        Assert.Equal(100, PdfTestFixtures.MediaBoxWidth(extracted, extracted.Pages[1]));
    }

    [Fact]
    public async Task Delete_operation_removes_a_page()
    {
        using var sandbox = new Sandbox();
        var a = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocument(3)); // widths 100, 101, 102
        var outPath = Path.Combine(sandbox.Out, "deleted.pdf");

        var plan = SheafPlanner.Build(
            [new MergeSource(a)],
            [new SheafOutput(outPath, [new DeletePageOperation(1)])]);

        var report = await new SheafRunner().RunAsync(plan);

        Assert.True(report.IsClean);
        var result = PdfDocument.Open(await File.ReadAllBytesAsync(outPath)).Document!;
        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(100, PdfTestFixtures.MediaBoxWidth(result, result.Pages[0]));
        Assert.Equal(102, PdfTestFixtures.MediaBoxWidth(result, result.Pages[1]));
    }

    [Fact]
    public async Task Rotate_operation_sets_the_page_rotate_value()
    {
        using var sandbox = new Sandbox();
        var a = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocument(1));
        var outPath = Path.Combine(sandbox.Out, "rotated.pdf");

        var plan = SheafPlanner.Build(
            [new MergeSource(a)],
            [new SheafOutput(outPath, [new RotateOperation(0, 90)])]);

        var report = await new SheafRunner().RunAsync(plan);

        Assert.True(report.IsClean);
        var result = PdfDocument.Open(await File.ReadAllBytesAsync(outPath)).Document!;
        var rotate = (result.Objects.Resolve(result.Pages[0].Dictionary.Get("Rotate")) as PdfNumber)?.AsInt;
        Assert.Equal(90, rotate);
    }

    [Fact]
    public async Task Split_produces_independent_outputs_from_one_source()
    {
        using var sandbox = new Sandbox();
        var a = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocument(2)); // widths 100, 101
        var firstOut = Path.Combine(sandbox.Out, "first.pdf");
        var secondOut = Path.Combine(sandbox.Out, "second.pdf");

        var plan = SheafPlanner.Build(
            [new MergeSource(a)],
            [
                new SheafOutput(firstOut, [new DeletePageOperation(1)]),
                new SheafOutput(secondOut, [new DeletePageOperation(0)]),
            ]);

        var report = await new SheafRunner().RunAsync(plan);

        Assert.True(report.IsClean);
        var first = PdfDocument.Open(await File.ReadAllBytesAsync(firstOut)).Document!;
        var second = PdfDocument.Open(await File.ReadAllBytesAsync(secondOut)).Document!;
        Assert.Equal(100, PdfTestFixtures.MediaBoxWidth(first, first.Pages[0]));
        Assert.Equal(101, PdfTestFixtures.MediaBoxWidth(second, second.Pages[0]));
    }

    [Fact]
    public async Task An_unreadable_source_fails_every_output_as_a_reported_result_not_an_exception()
    {
        using var sandbox = new Sandbox();
        var badPath = sandbox.AddPdf("bad.pdf", "not a pdf"u8.ToArray());
        var firstOut = Path.Combine(sandbox.Out, "first.pdf");
        var secondOut = Path.Combine(sandbox.Out, "second.pdf");

        var plan = SheafPlanner.Build(
            [new MergeSource(badPath)],
            [new SheafOutput(firstOut, []), new SheafOutput(secondOut, [])]);

        var report = await new SheafRunner().RunAsync(plan);

        Assert.False(report.IsClean);
        Assert.Equal(2, report.Failed);
        Assert.All(report.Results, r => Assert.Equal(SheafOutcome.Failed, r.Outcome));
    }
}
