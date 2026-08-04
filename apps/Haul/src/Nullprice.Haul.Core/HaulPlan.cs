namespace Nullprice.Haul.Core;

/// <summary>One resolved, directly-downloadable stream a prober found for a URL.</summary>
public sealed record HaulFormat(
    string Itag,
    string Container,
    string CodecLabel,
    int? Height,
    int? BitrateKbps,
    string DirectUrl);

/// <summary>
/// What probing a URL turned up. <see cref="SkippedReasons"/> carries formats that existed but
/// were refused (signature-ciphered, PO-Token gated, etc.) so the App can say why the list is
/// shorter than the source actually offers, rather than looking incomplete for no reason.
/// </summary>
public sealed record ProbeResult(
    string Title,
    TimeSpan? Duration,
    IReadOnlyList<HaulFormat> Formats,
    IReadOnlyList<string> SkippedReasons);

public sealed record TrimRange(TimeSpan Start, TimeSpan End);

/// <summary>
/// One resolved unit of work: a format the user picked, where it lands, and the edits to apply.
/// Unlike <c>BatchItem</c>, this is not derived by the planner — probing is an interactive,
/// async, per-URL step the App drives, so the App hands the planner already-decided jobs.
/// </summary>
public sealed record HaulJob(
    string SourceUrl,
    HaulFormat Format,
    string OutputPath,
    TrimRange? Trim,
    double? SpeedFactor);

public enum HaulOutcome
{
    Downloaded,
    Failed,
}

public sealed record HaulResult(HaulJob Job, HaulOutcome Outcome, string? Error);

public sealed record HaulProgress(int Done, int Total, long BytesDone, long? BytesTotal, string CurrentUrl)
{
    public double Fraction => Total <= 0 ? 0.0 : (double)Done / Total;
}

public sealed record HaulReport(IReadOnlyList<HaulResult> Results, TimeSpan Duration, bool WasCancelled)
{
    public int Downloaded => Results.Count(r => r.Outcome == HaulOutcome.Downloaded);
    public int Failed => Results.Count(r => r.Outcome == HaulOutcome.Failed);

    public bool IsClean => !WasCancelled && Failed == 0;
}

/// <summary>Why a plan could not be built. These are user mistakes, not exceptions.</summary>
public sealed record PlanProblem(string Message);

public sealed record HaulPlan(IReadOnlyList<HaulJob> Jobs, IReadOnlyList<PlanProblem> Problems)
{
    public bool IsRunnable => Problems.Count == 0 && Jobs.Count > 0;
    public int Total => Jobs.Count;
}

/// <summary>
/// Turns already-resolved jobs into a runnable plan. The one invariant enforced here — two
/// jobs can never resolve to the same output path — is the same class of mistake Batch guards
/// against, just without a naming template to derive it from.
/// </summary>
public static class HaulPlanner
{
    public static HaulPlan Build(IReadOnlyList<HaulJob> jobs)
    {
        var problems = new List<PlanProblem>();

        if (jobs.Count == 0)
        {
            problems.Add(new PlanProblem("Nothing queued to download."));
        }

        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var job in jobs)
        {
            var destination = Path.GetFullPath(job.OutputPath);

            if (claimed.TryGetValue(destination, out var firstOwner))
            {
                problems.Add(new PlanProblem(
                    $"\"{Path.GetFileName(destination)}\" would be written twice — from " +
                    $"{firstOwner} and {job.SourceUrl}. Pick a different filename for one of them."));
                continue;
            }

            claimed[destination] = job.SourceUrl;
        }

        return new HaulPlan(jobs, problems);
    }
}
