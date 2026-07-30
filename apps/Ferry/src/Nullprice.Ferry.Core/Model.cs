namespace Nullprice.Ferry.Core;

/// <summary>What to do when the destination file already exists.</summary>
public enum ConflictPolicy
{
    /// <summary>Replace the existing file.</summary>
    Overwrite,

    /// <summary>Leave the existing file alone and report the item as skipped.</summary>
    Skip,

    /// <summary>Write alongside it as "name (2).ext".</summary>
    Rename,

    /// <summary>Treat it as an error and record a failure for that item.</summary>
    Fail,
}

/// <summary>One file to move from A to B.</summary>
public sealed record CopyItem(string SourcePath, string DestinationPath, long Length);

/// <summary>A complete transfer, resolved to individual files before anything is written.</summary>
public sealed record CopyPlan(
    IReadOnlyList<CopyItem> Items,
    ConflictPolicy Policy = ConflictPolicy.Overwrite,
    bool Verify = true)
{
    public long TotalBytes => Items.Sum(i => i.Length);
    public int TotalFiles => Items.Count;
}

public enum ItemOutcome
{
    Copied,
    Skipped,
    Failed,

    /// <summary>
    /// The bytes were written but reading them back produced a different hash. The
    /// destination is left in place so the user can inspect it, and it is reported loudly.
    /// </summary>
    VerificationFailed,
}

public sealed record ItemResult(
    CopyItem Item,
    ItemOutcome Outcome,
    string? FinalPath,
    string? Error,
    TimeSpan Duration)
{
    public bool IsProblem => Outcome is ItemOutcome.Failed or ItemOutcome.VerificationFailed;
}

/// <summary>Progress snapshot, pushed to the UI as the transfer runs.</summary>
public sealed record CopyProgress(
    int FilesDone,
    int FilesTotal,
    long BytesDone,
    long BytesTotal,
    string CurrentFile,
    double BytesPerSecond)
{
    public double Fraction => BytesTotal <= 0 ? 0.0 : (double)BytesDone / BytesTotal;

    public TimeSpan? Remaining =>
        BytesPerSecond <= 1.0
            ? null
            : TimeSpan.FromSeconds((BytesTotal - BytesDone) / BytesPerSecond);
}

/// <summary>The outcome of a whole transfer. This is what gets written to the log.</summary>
public sealed record TransferReport(
    IReadOnlyList<ItemResult> Results,
    TimeSpan Duration,
    bool WasCancelled)
{
    public int Copied => Results.Count(r => r.Outcome == ItemOutcome.Copied);
    public int Skipped => Results.Count(r => r.Outcome == ItemOutcome.Skipped);
    public int Failed => Results.Count(r => r.Outcome == ItemOutcome.Failed);
    public int Unverified => Results.Count(r => r.Outcome == ItemOutcome.VerificationFailed);

    /// <summary>
    /// The whole point of the application: a transfer is only clean if every file arrived
    /// and every file read back the same as it went in.
    /// </summary>
    public bool IsClean => !WasCancelled && Results.All(r => !r.IsProblem);
}
