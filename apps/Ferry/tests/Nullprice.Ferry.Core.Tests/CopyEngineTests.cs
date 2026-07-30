using System.Globalization;
using Nullprice.Ferry.Core;

namespace Nullprice.Ferry.Core.Tests;

/// <summary>
/// Each test gets its own throwaway directory tree under the system temp folder, so the
/// suite never touches anything the developer cares about.
/// </summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(
        Path.GetTempPath(), "ferry-tests", Guid.NewGuid().ToString("n"));

    public string Source => EnsureDir(Path.Combine(Root, "src"));
    public string Destination => EnsureDir(Path.Combine(Root, "dst"));

    public string WriteFile(string relativePath, byte[] content)
    {
        var full = Path.Combine(Source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string WriteFile(string relativePath, string content) =>
        WriteFile(relativePath, System.Text.Encoding.UTF8.GetBytes(content));

    public static byte[] RandomBytes(int count)
    {
        var data = new byte[count];
        Random.Shared.NextBytes(data);
        return data;
    }

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // A leaked temp folder is not worth failing a test run over.
        }
    }
}

public class CopyEngineTests
{
    [Fact]
    public async Task Copies_a_file_and_reports_it_clean()
    {
        using var box = new Sandbox();
        var payload = Sandbox.RandomBytes(3 * 1024 * 1024);
        box.WriteFile("report.bin", payload);

        var plan = PlanBuilder.Build([Path.Combine(box.Source, "report.bin")], box.Destination);
        var report = await new CopyEngine().RunAsync(plan);

        Assert.True(report.IsClean);
        Assert.Equal(1, report.Copied);

        var landed = Path.Combine(box.Destination, "report.bin");
        Assert.True(File.Exists(landed));
        Assert.Equal(payload, File.ReadAllBytes(landed));
    }

    [Fact]
    public async Task Preserves_folder_structure()
    {
        using var box = new Sandbox();
        box.WriteFile(Path.Combine("photos", "2024", "a.jpg"), "alpha");
        box.WriteFile(Path.Combine("photos", "2025", "b.jpg"), "bravo");
        box.WriteFile(Path.Combine("photos", "top.txt"), "top");

        var plan = PlanBuilder.Build([Path.Combine(box.Source, "photos")], box.Destination);
        var report = await new CopyEngine().RunAsync(plan);

        Assert.True(report.IsClean);
        Assert.Equal(3, report.Copied);
        Assert.True(File.Exists(Path.Combine(box.Destination, "photos", "2024", "a.jpg")));
        Assert.True(File.Exists(Path.Combine(box.Destination, "photos", "2025", "b.jpg")));
        Assert.True(File.Exists(Path.Combine(box.Destination, "photos", "top.txt")));
    }

    [Fact]
    public async Task Skip_policy_leaves_the_existing_file_untouched()
    {
        using var box = new Sandbox();
        box.WriteFile("notes.txt", "new content");
        File.WriteAllText(Path.Combine(box.Destination, "notes.txt"), "original content");

        var plan = PlanBuilder.Build(
            [Path.Combine(box.Source, "notes.txt")], box.Destination, ConflictPolicy.Skip);
        var report = await new CopyEngine().RunAsync(plan);

        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.Copied);
        Assert.Equal("original content", File.ReadAllText(Path.Combine(box.Destination, "notes.txt")));
    }

    [Fact]
    public async Task Rename_policy_writes_alongside_rather_than_over()
    {
        using var box = new Sandbox();
        box.WriteFile("notes.txt", "new content");
        File.WriteAllText(Path.Combine(box.Destination, "notes.txt"), "original content");

        var plan = PlanBuilder.Build(
            [Path.Combine(box.Source, "notes.txt")], box.Destination, ConflictPolicy.Rename);
        var report = await new CopyEngine().RunAsync(plan);

        Assert.True(report.IsClean);
        Assert.Equal("original content", File.ReadAllText(Path.Combine(box.Destination, "notes.txt")));
        Assert.Equal("new content", File.ReadAllText(Path.Combine(box.Destination, "notes (2).txt")));
    }

    [Fact]
    public async Task Fail_policy_records_a_failure_and_does_not_overwrite()
    {
        using var box = new Sandbox();
        box.WriteFile("notes.txt", "new content");
        File.WriteAllText(Path.Combine(box.Destination, "notes.txt"), "original content");

        var plan = PlanBuilder.Build(
            [Path.Combine(box.Source, "notes.txt")], box.Destination, ConflictPolicy.Fail);
        var report = await new CopyEngine().RunAsync(plan);

        Assert.False(report.IsClean);
        Assert.Equal(1, report.Failed);
        Assert.Equal("original content", File.ReadAllText(Path.Combine(box.Destination, "notes.txt")));
    }

    [Fact]
    public async Task Overwrite_replaces_the_existing_file()
    {
        using var box = new Sandbox();
        box.WriteFile("notes.txt", "new content");
        File.WriteAllText(Path.Combine(box.Destination, "notes.txt"), "original content");

        var plan = PlanBuilder.Build(
            [Path.Combine(box.Source, "notes.txt")], box.Destination, ConflictPolicy.Overwrite);
        var report = await new CopyEngine().RunAsync(plan);

        Assert.True(report.IsClean);
        Assert.Equal("new content", File.ReadAllText(Path.Combine(box.Destination, "notes.txt")));
    }

    [Fact]
    public async Task Leaves_no_partial_file_at_the_destination_when_cancelled()
    {
        using var box = new Sandbox();
        for (var i = 0; i < 40; i++)
            box.WriteFile($"file{i:D2}.bin", Sandbox.RandomBytes(256 * 1024));

        var plan = PlanBuilder.Build([box.Source], box.Destination);

        using var cts = new CancellationTokenSource();
        var seen = 0;
        var progress = new Progress<CopyProgress>(_ =>
        {
            if (Interlocked.Increment(ref seen) > 3) cts.Cancel();
        });

        var report = await new CopyEngine().RunAsync(plan, progress, cts.Token);

        Assert.True(report.WasCancelled);

        // The working extension must never survive a cancellation.
        var leftovers = Directory
            .EnumerateFiles(box.Destination, "*.ferrypart", SearchOption.AllDirectories)
            .ToList();
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task Reports_progress_that_reaches_the_full_total()
    {
        using var box = new Sandbox();
        for (var i = 0; i < 5; i++)
            box.WriteFile($"file{i}.bin", Sandbox.RandomBytes(512 * 1024));

        var plan = PlanBuilder.Build([box.Source], box.Destination);

        CopyProgress? last = null;
        var progress = new Progress<CopyProgress>(p => last = p);

        var report = await new CopyEngine().RunAsync(plan, progress);

        Assert.True(report.IsClean);
        Assert.NotNull(last);
        Assert.Equal(plan.TotalFiles, last!.FilesDone);
        Assert.Equal(plan.TotalBytes, last.BytesDone);
        Assert.Equal(1.0, last.Fraction, precision: 6);
    }

    [Fact]
    public async Task Copies_an_empty_file_without_complaint()
    {
        using var box = new Sandbox();
        box.WriteFile("empty.txt", Array.Empty<byte>());

        var plan = PlanBuilder.Build([Path.Combine(box.Source, "empty.txt")], box.Destination);
        var report = await new CopyEngine().RunAsync(plan);

        Assert.True(report.IsClean);
        Assert.Equal(0, new FileInfo(Path.Combine(box.Destination, "empty.txt")).Length);
    }

    [Fact]
    public async Task Missing_source_is_rejected_while_building_the_plan()
    {
        using var box = new Sandbox();
        var missing = Path.Combine(box.Source, "nope.txt");

        Assert.Throws<FileNotFoundException>(() => PlanBuilder.Build([missing], box.Destination));
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    public void Formats_byte_counts_readably(long bytes, string expected)
    {
        Assert.Equal(expected, PlanBuilder.FormatBytes(bytes, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Formats_byte_counts_in_the_readers_own_culture()
    {
        // A reader whose locale writes decimals with a comma should see one.
        var dutch = new CultureInfo("nl-NL");
        Assert.Equal("1,5 KB", PlanBuilder.FormatBytes(1536, dutch));
    }
}
