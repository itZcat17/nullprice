namespace Nullprice.Sheaf.Core.Tests;

public class SheafPlannerTests
{
    [Fact]
    public void Refuses_when_output_path_equals_a_source_path()
    {
        using var sandbox = new Sandbox();
        var source = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocument(1));

        var plan = SheafPlanner.Build(
            [new MergeSource(source)],
            [new SheafOutput(source, [])]);

        Assert.False(plan.IsRunnable);
        Assert.Contains(plan.Problems, p => p.Message.Contains("overwrite"));
    }

    [Fact]
    public void Refuses_when_two_outputs_share_a_path()
    {
        using var sandbox = new Sandbox();
        var source = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocument(1));
        var outPath = Path.Combine(sandbox.Out, "merged.pdf");

        var plan = SheafPlanner.Build(
            [new MergeSource(source)],
            [new SheafOutput(outPath, []), new SheafOutput(outPath, [])]);

        Assert.False(plan.IsRunnable);
        Assert.Contains(plan.Problems, p => p.Message.Contains("Two outputs"));
    }

    [Fact]
    public void Reports_a_missing_source_file()
    {
        var plan = SheafPlanner.Build(
            [new MergeSource(@"C:\nowhere\missing.pdf")],
            [new SheafOutput(@"C:\nowhere\out.pdf", [])]);

        Assert.False(plan.IsRunnable);
        Assert.Contains(plan.Problems, p => p.Message.StartsWith("Not found"));
    }

    [Fact]
    public void A_valid_source_and_output_produce_a_runnable_plan()
    {
        using var sandbox = new Sandbox();
        var source = sandbox.AddPdf("a.pdf", PdfTestFixtures.BuildDocument(1));
        var outPath = Path.Combine(sandbox.Out, "merged.pdf");

        var plan = SheafPlanner.Build([new MergeSource(source)], [new SheafOutput(outPath, [])]);

        Assert.True(plan.IsRunnable);
        Assert.Empty(plan.Problems);
    }
}
