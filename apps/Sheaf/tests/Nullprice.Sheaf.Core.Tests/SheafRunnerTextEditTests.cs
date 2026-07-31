namespace Nullprice.Sheaf.Core.Tests;

public class SheafRunnerTextEditTests
{
    [Fact]
    public async Task Text_edit_replaces_the_shown_text_in_the_written_output()
    {
        using var sandbox = new Sandbox();
        var sourcePath = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocumentWithTrueTypeText("Hello"));
        var outPath = Path.Combine(sandbox.Out, "edited.pdf");

        var sourceDoc = PdfDocument.Open(await File.ReadAllBytesAsync(sourcePath)).Document!;
        var contentBytes = sourceDoc.GetStreamData((PdfStream)sourceDoc.Objects.Resolve(sourceDoc.Pages[0].Dictionary.Get("Contents")));
        var located = ContentStreamTextEditor.FindTextAt(contentBytes, x: 55, y: 705);
        Assert.NotNull(located);

        var edit = new TextEdit(PageIndex: 0, located!.OperatorIndex, "World", located.FontResourceName!);
        var plan = SheafPlanner.Build(
            [new MergeSource(sourcePath)],
            [new SheafOutput(outPath, [], TextEdits: [edit])]);

        var report = await new SheafRunner().RunAsync(plan);

        Assert.True(report.IsClean);
        var resultDoc = PdfDocument.Open(await File.ReadAllBytesAsync(outPath)).Document!;
        var resultContent = System.Text.Encoding.ASCII.GetString(
            resultDoc.GetStreamData((PdfStream)resultDoc.Objects.Resolve(resultDoc.Pages[0].Dictionary.Get("Contents"))));

        Assert.Contains("World", resultContent);
        Assert.DoesNotContain("Hello", resultContent);
    }

    [Fact]
    public async Task A_character_missing_from_the_font_fails_that_output_rather_than_writing_garbage()
    {
        using var sandbox = new Sandbox();
        // Font only declares codes 32-126 (see fixture); '€' (U+20AC via WinAnsi code 0x80) isn't in range.
        var sourcePath = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocumentWithTrueTypeText("Hello"));
        var outPath = Path.Combine(sandbox.Out, "edited.pdf");

        var edit = new TextEdit(PageIndex: 0, OperatorIndex: 3, "H€", "F1");
        var plan = SheafPlanner.Build(
            [new MergeSource(sourcePath)],
            [new SheafOutput(outPath, [], TextEdits: [edit])]);

        var report = await new SheafRunner().RunAsync(plan);

        Assert.False(report.IsClean);
        Assert.Equal(SheafOutcome.Failed, report.Results.Single().Outcome);
    }
}
