namespace Nullprice.Batch.Core;

public sealed record BatchItem(string SourcePath, string DestinationPath, int Index);

public enum BatchOutcome
{
    Written,
    Skipped,
    Failed,
}

public sealed record BatchResult(BatchItem Item, BatchOutcome Outcome, string? Error);

public sealed record BatchProgress(int Done, int Total, string CurrentFile)
{
    public double Fraction => Total <= 0 ? 0.0 : (double)Done / Total;
}

public sealed record BatchReport(IReadOnlyList<BatchResult> Results, TimeSpan Duration, bool WasCancelled)
{
    public int Written => Results.Count(r => r.Outcome == BatchOutcome.Written);
    public int Skipped => Results.Count(r => r.Outcome == BatchOutcome.Skipped);
    public int Failed => Results.Count(r => r.Outcome == BatchOutcome.Failed);

    public bool IsClean => !WasCancelled && Failed == 0;
}

/// <summary>Why a plan could not be built. These are user mistakes, not exceptions.</summary>
public sealed record PlanProblem(string Message);

public sealed record BatchPlan(
    IReadOnlyList<BatchItem> Items,
    Pipeline Pipeline,
    string OutputDirectory,
    IReadOnlyList<PlanProblem> Problems)
{
    public bool IsRunnable => Problems.Count == 0 && Items.Count > 0;
    public int Total => Items.Count;
}

/// <summary>
/// Resolves sources plus a pipeline into an explicit list of writes.
/// <para>
/// Two invariants are enforced here rather than trusted to the UI, because both are ways
/// to destroy someone's photographs: the output directory can never be a source
/// directory, and two inputs can never resolve to the same output path.
/// </para>
/// </summary>
public static class BatchPlanner
{
    public static readonly string[] SupportedExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".gif"];

    /// <summary>
    /// Builds a plan. <paramref name="measure"/> supplies each image's dimensions, which
    /// naming templates may reference; pass a stub in tests to keep this pure.
    /// </summary>
    public static BatchPlan Build(
        IEnumerable<string> sources,
        string outputDirectory,
        Pipeline pipeline,
        string template = NamingTemplate.Default,
        Func<string, (int Width, int Height)>? measure = null)
    {
        var problems = new List<PlanProblem>();
        var files = Collect(sources, problems);

        if (files.Count == 0 && problems.Count == 0)
        {
            problems.Add(new PlanProblem("None of the selected folders contain supported images."));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            problems.Add(new PlanProblem("Choose an output folder."));
            return new BatchPlan([], pipeline, outputDirectory ?? string.Empty, problems);
        }

        var output = Path.GetFullPath(outputDirectory);

        // Never write results next to the originals. This is the single guard that keeps
        // "non-destructive by default" true instead of aspirational.
        foreach (var file in files)
        {
            var sourceDir = Path.GetDirectoryName(file);
            if (sourceDir is not null && PathsEqual(sourceDir, output))
            {
                problems.Add(new PlanProblem(
                    "The output folder is also a source folder. Pick somewhere else so the originals are left alone."));
                break;
            }
        }

        var extension = pipeline.Conversion?.Extension;
        var items = new List<BatchItem>(files.Count);
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < files.Count; i++)
        {
            var source = files[i];
            var (width, height) = measure?.Invoke(source) ?? (0, 0);

            var context = new NamingContext(
                Path.GetFileNameWithoutExtension(source),
                i + 1,
                width,
                height,
                SafeTimestamp(source));

            var stem = NamingTemplate.Apply(template, context);
            var ext = extension ?? Path.GetExtension(source);
            var destination = Path.Combine(output, stem + ext);

            if (claimed.TryGetValue(destination, out var firstOwner))
            {
                problems.Add(new PlanProblem(
                    $"\"{Path.GetFileName(destination)}\" would be written twice — from " +
                    $"{Path.GetFileName(firstOwner)} and {Path.GetFileName(source)}. " +
                    "Add {n} to the name template to keep them apart."));
                continue;
            }

            claimed[destination] = source;
            items.Add(new BatchItem(source, destination, i + 1));
        }

        return new BatchPlan(items, pipeline, output, problems);
    }

    private static List<string> Collect(IEnumerable<string> sources, List<PlanProblem> problems)
    {
        var files = new List<string>();

        foreach (var source in sources)
        {
            if (File.Exists(source))
            {
                if (IsSupported(source)) files.Add(Path.GetFullPath(source));
                else problems.Add(new PlanProblem($"{Path.GetFileName(source)} is not a supported image."));
            }
            else if (Directory.Exists(source))
            {
                files.AddRange(Directory
                    .EnumerateFiles(source, "*", SearchOption.AllDirectories)
                    .Where(IsSupported)
                    .Select(Path.GetFullPath));
            }
            else
            {
                problems.Add(new PlanProblem($"Not found: {source}"));
            }
        }

        // Stable order so {n} numbering is predictable between runs.
        return files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static DateTime SafeTimestamp(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch
        {
            return DateTime.Now;
        }
    }
}
