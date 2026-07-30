using Nullprice.Batch.Core;

namespace Nullprice.Batch.Core.Tests;

public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "batch-tests", Guid.NewGuid().ToString("n"));

    public string In => Ensure(Path.Combine(Root, "in"));
    public string Out => Ensure(Path.Combine(Root, "out"));

    public string AddImage(string relativePath)
    {
        var full = Path.Combine(In, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Contents are irrelevant — the planner never decodes, and the processor is stubbed.
        File.WriteAllBytes(full, [0x00, 0x01, 0x02]);
        return full;
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
        catch { }
    }
}

/// <summary>Records what it was asked to do and writes a placeholder so the file exists.</summary>
public sealed class FakeProcessor : IImageProcessor
{
    public List<BatchItem> Applied { get; } = [];
    public HashSet<string> FailOn { get; } = new(StringComparer.OrdinalIgnoreCase);
    public (int Width, int Height) Size { get; set; } = (1920, 1080);

    public Task ApplyAsync(BatchItem item, Pipeline pipeline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (FailOn.Contains(Path.GetFileName(item.SourcePath)))
            throw new InvalidDataException("Not a readable image.");

        Applied.Add(item);
        File.WriteAllText(item.DestinationPath, "processed");
        return Task.CompletedTask;
    }

    public (int Width, int Height) Measure(string path) => Size;
}

public class NamingTemplateTests
{
    private static NamingContext Ctx(string name = "DSC_0001", int index = 1) =>
        new(name, index, 1920, 1080, new DateTime(2026, 7, 30, 14, 5, 0));

    [Fact]
    public void Default_template_keeps_the_original_name()
    {
        Assert.Equal("DSC_0001", NamingTemplate.Apply("{name}", Ctx()));
    }

    [Theory]
    [InlineData("{name}-{n}", "DSC_0001-1")]
    [InlineData("{n:000}_{name}", "001_DSC_0001")]
    [InlineData("{name}_{width}x{height}", "DSC_0001_1920x1080")]
    [InlineData("{date}_{name}", "2026-07-30_DSC_0001")]
    [InlineData("{date:yyyy}/{name}", "2026_DSC_0001")]
    [InlineData("web_{name}_thumb", "web_DSC_0001_thumb")]
    public void Substitutes_tokens(string template, string expected)
    {
        Assert.Equal(expected, NamingTemplate.Apply(template, Ctx()));
    }

    [Fact]
    public void Unknown_tokens_stay_visible_rather_than_vanishing()
    {
        // Silently dropping an unknown token would collapse every file onto one name.
        var result = NamingTemplate.Apply("{name}-{nope}", Ctx());
        Assert.Equal("DSC_0001-{nope}", result);
    }

    [Fact]
    public void Unclosed_brace_is_treated_as_literal_text()
    {
        Assert.Equal("DSC_0001-{oops", NamingTemplate.Apply("{name}-{oops", Ctx()));
    }

    [Fact]
    public void Strips_characters_windows_forbids()
    {
        var result = NamingTemplate.Apply("a:b*c?{name}", Ctx());
        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('*', result);
        Assert.DoesNotContain('?', result);
    }

    [Fact]
    public void Empty_template_falls_back_to_the_original_name()
    {
        Assert.Equal("DSC_0001", NamingTemplate.Apply("   ", Ctx()));
    }
}

public class ResizeTests
{
    [Theory]
    [InlineData(ResizeStrategy.Fit, 800, 800, 1920, 1080, 800, 450)]
    [InlineData(ResizeStrategy.Fill, 800, 800, 1920, 1080, 1422, 800)]
    [InlineData(ResizeStrategy.Exact, 800, 800, 1920, 1080, 800, 800)]
    public void Resolves_dimensions_per_mode(
        ResizeStrategy mode, int w, int h, int srcW, int srcH, int expectedW, int expectedH)
    {
        var op = new ResizeOperation(mode, w, h);
        Assert.Equal((expectedW, expectedH), op.ResolveFor(srcW, srcH));
    }

    [Fact]
    public void Percent_scales_both_axes()
    {
        var op = new ResizeOperation(ResizeStrategy.Percent, 0, 0, 50);
        Assert.Equal((960, 540), op.ResolveFor(1920, 1080));
    }

    [Fact]
    public void Never_resolves_to_a_zero_dimension()
    {
        var op = new ResizeOperation(ResizeStrategy.Percent, 0, 0, 0.01);
        var (w, h) = op.ResolveFor(100, 100);
        Assert.True(w >= 1 && h >= 1);
    }
}

public class PipelineTests
{
    [Fact]
    public void The_last_conversion_decides_the_extension()
    {
        var pipeline = Pipeline.Empty
            .With(new ConvertOperation(ImageFormat.Png))
            .With(new ConvertOperation(ImageFormat.Jpeg, 70));

        Assert.Equal(".jpg", pipeline.Conversion!.Extension);
    }

    [Fact]
    public void Quality_only_applies_to_lossy_formats()
    {
        Assert.True(new ConvertOperation(ImageFormat.Jpeg).SupportsQuality);
        Assert.False(new ConvertOperation(ImageFormat.Png).SupportsQuality);
    }

    [Fact]
    public void Move_reorders_because_order_changes_the_result()
    {
        var resize = new ResizeOperation(ResizeStrategy.Fit, 100, 100);
        var mark = new WatermarkOperation("x");
        var pipeline = Pipeline.Empty.With(resize).With(mark);

        var moved = pipeline.Move(0, 1);

        Assert.Same(mark, moved.Operations[0]);
        Assert.Same(resize, moved.Operations[1]);
    }

    [Fact]
    public void Move_out_of_range_is_a_no_op()
    {
        var pipeline = Pipeline.Empty.With(new WatermarkOperation("x"));
        Assert.Equal(pipeline, pipeline.Move(0, -1));
        Assert.Equal(pipeline, pipeline.Move(0, 5));
    }
}

public class BatchPlannerTests
{
    [Fact]
    public void Collects_supported_images_recursively_and_ignores_others()
    {
        using var box = new Sandbox();
        box.AddImage("a.jpg");
        box.AddImage(Path.Combine("nested", "b.png"));
        box.AddImage("notes.txt");

        var plan = BatchPlanner.Build([box.In], box.Out, Pipeline.Empty);

        Assert.Equal(2, plan.Total);
        Assert.All(plan.Items, i => Assert.True(BatchPlanner.IsSupported(i.SourcePath)));
    }

    [Fact]
    public void Refuses_to_write_into_a_source_folder()
    {
        using var box = new Sandbox();
        box.AddImage("a.jpg");

        var plan = BatchPlanner.Build([box.In], box.In, Pipeline.Empty);

        Assert.False(plan.IsRunnable);
        Assert.Contains(plan.Problems, p => p.Message.Contains("also a source folder"));
    }

    [Fact]
    public void Detects_two_inputs_colliding_on_one_output_name()
    {
        using var box = new Sandbox();
        box.AddImage("a.jpg");
        box.AddImage("b.jpg");

        // A constant template makes every file resolve to the same destination.
        var plan = BatchPlanner.Build([box.In], box.Out, Pipeline.Empty, "same-name");

        Assert.False(plan.IsRunnable);
        Assert.Contains(plan.Problems, p => p.Message.Contains("written twice"));
    }

    [Fact]
    public void Adding_the_index_token_resolves_a_collision()
    {
        using var box = new Sandbox();
        box.AddImage("a.jpg");
        box.AddImage("b.jpg");

        var plan = BatchPlanner.Build([box.In], box.Out, Pipeline.Empty, "shot-{n}");

        Assert.True(plan.IsRunnable);
        Assert.Equal(2, plan.Total);
        Assert.Equal(2, plan.Items.Select(i => i.DestinationPath).Distinct().Count());
    }

    [Fact]
    public void Conversion_changes_the_output_extension()
    {
        using var box = new Sandbox();
        box.AddImage("a.png");

        var pipeline = Pipeline.Empty.With(new ConvertOperation(ImageFormat.Jpeg));
        var plan = BatchPlanner.Build([box.In], box.Out, pipeline);

        Assert.EndsWith(".jpg", plan.Items[0].DestinationPath);
    }

    [Fact]
    public void Without_conversion_the_original_extension_is_kept()
    {
        using var box = new Sandbox();
        box.AddImage("a.png");

        var plan = BatchPlanner.Build([box.In], box.Out, Pipeline.Empty);
        Assert.EndsWith(".png", plan.Items[0].DestinationPath);
    }

    [Fact]
    public void Reports_a_missing_source_rather_than_throwing()
    {
        using var box = new Sandbox();
        var plan = BatchPlanner.Build([Path.Combine(box.In, "ghost.jpg")], box.Out, Pipeline.Empty);

        Assert.False(plan.IsRunnable);
        Assert.Contains(plan.Problems, p => p.Message.StartsWith("Not found"));
    }

    [Fact]
    public void Empty_output_folder_is_a_problem_not_a_crash()
    {
        using var box = new Sandbox();
        box.AddImage("a.jpg");

        var plan = BatchPlanner.Build([box.In], "", Pipeline.Empty);

        Assert.False(plan.IsRunnable);
        Assert.Contains(plan.Problems, p => p.Message.Contains("output folder"));
    }

    [Fact]
    public void Numbering_is_stable_between_runs()
    {
        using var box = new Sandbox();
        box.AddImage("c.jpg");
        box.AddImage("a.jpg");
        box.AddImage("b.jpg");

        var first = BatchPlanner.Build([box.In], box.Out, Pipeline.Empty, "{n}-{name}");
        var second = BatchPlanner.Build([box.In], box.Out, Pipeline.Empty, "{n}-{name}");

        Assert.Equal(
            first.Items.Select(i => i.DestinationPath),
            second.Items.Select(i => i.DestinationPath));
    }
}

public class BatchRunnerTests
{
    [Fact]
    public async Task Writes_every_planned_output_and_leaves_sources_alone()
    {
        using var box = new Sandbox();
        var a = box.AddImage("a.jpg");
        var b = box.AddImage("b.jpg");
        var before = new[] { File.ReadAllBytes(a), File.ReadAllBytes(b) };

        var plan = BatchPlanner.Build([box.In], box.Out, Pipeline.Empty, "{n}-{name}");
        var report = await new BatchRunner(new FakeProcessor()).RunAsync(plan);

        Assert.True(report.IsClean);
        Assert.Equal(2, report.Written);
        Assert.All(plan.Items, i => Assert.True(File.Exists(i.DestinationPath)));

        // Non-destructive is the headline promise, so assert it rather than trust it.
        Assert.Equal(before[0], File.ReadAllBytes(a));
        Assert.Equal(before[1], File.ReadAllBytes(b));
    }

    [Fact]
    public async Task One_bad_file_does_not_abandon_the_rest()
    {
        using var box = new Sandbox();
        box.AddImage("good1.jpg");
        box.AddImage("broken.jpg");
        box.AddImage("good2.jpg");

        var processor = new FakeProcessor();
        processor.FailOn.Add("broken.jpg");

        var plan = BatchPlanner.Build([box.In], box.Out, Pipeline.Empty, "{n}-{name}");
        var report = await new BatchRunner(processor).RunAsync(plan);

        Assert.False(report.IsClean);
        Assert.Equal(2, report.Written);
        Assert.Equal(1, report.Failed);
        Assert.Contains(report.Results, r => r.Outcome == BatchOutcome.Failed && r.Error is not null);
    }

    [Fact]
    public async Task Cancellation_stops_early_and_says_so()
    {
        using var box = new Sandbox();
        for (var i = 0; i < 20; i++) box.AddImage($"f{i:D2}.jpg");

        using var cts = new CancellationTokenSource();
        var seen = 0;
        var progress = new Progress<BatchProgress>(_ =>
        {
            if (Interlocked.Increment(ref seen) > 3) cts.Cancel();
        });

        var plan = BatchPlanner.Build([box.In], box.Out, Pipeline.Empty, "{n}-{name}");
        var report = await new BatchRunner(new FakeProcessor()).RunAsync(plan, progress, cts.Token);

        Assert.True(report.WasCancelled);
        Assert.False(report.IsClean);
        Assert.True(report.Written < plan.Total);
    }

    [Fact]
    public async Task Progress_finishes_at_the_full_total()
    {
        using var box = new Sandbox();
        for (var i = 0; i < 5; i++) box.AddImage($"f{i}.jpg");

        BatchProgress? last = null;
        var plan = BatchPlanner.Build([box.In], box.Out, Pipeline.Empty, "{n}-{name}");
        await new BatchRunner(new FakeProcessor()).RunAsync(plan, new Progress<BatchProgress>(p => last = p));

        Assert.NotNull(last);
        Assert.Equal(plan.Total, last!.Done);
        Assert.Equal(1.0, last.Fraction, precision: 6);
    }
}
