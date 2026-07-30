using System.Text.Json.Serialization;

namespace Nullprice.Carry.Core;

public enum MigrationMode
{
    Backup,
    Restore
}

public sealed record MigrationProgress(
    string Stage,
    string CurrentItem,
    long FilesCompleted,
    long TotalFiles,
    long BytesCompleted,
    long TotalBytes);

public sealed record MigrationResult(
    bool Succeeded,
    string PackagePath,
    long FilesCopied,
    long BytesCopied,
    IReadOnlyList<string> Warnings,
    TimeSpan Duration);

public sealed class MigrationManifest
{
    public int FormatVersion { get; init; } = 1;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public string SourceComputer { get; init; } = Environment.MachineName;
    public string SourceUser { get; init; } = Environment.UserName;
    public long FileCount { get; init; }
    public long TotalBytes { get; init; }
    public bool IncludesDocuments { get; init; }
    public bool IncludesVsCode { get; init; }
    public int SelectedAppCount { get; init; }
    public int SystemComponentCount { get; init; }
    public int InactiveFileCount { get; init; }

    [JsonIgnore]
    public const string FileName = "carry-manifest.json";
}

public sealed record MigrationOptions(
    bool IncludeDocuments = true,
    bool IncludeVsCode = true,
    bool OverwriteExisting = false,
    IReadOnlyList<AppPackage>? SelectedApps = null,
    int InactivityMonths = 12);

public sealed record AppPackage(
    string Identifier,
    string DisplayName,
    string? Version,
    DateTimeOffset? LastUsedEstimate,
    string UsageEvidence,
    IReadOnlyList<AppDataFolder>? DataFolders = null,
    bool CanReinstall = true,
    long DataSizeBytes = 0)
{
    public bool IsLikelyInactive(int months) =>
        LastUsedEstimate is { } used && used < DateTimeOffset.Now.AddMonths(-months);
}

public sealed record AppDataFolder(string Scope, string RelativePath);

public sealed record SystemComponent(
    string Name,
    string Version,
    string? WingetIdentifier,
    bool CanReinstall,
    string? WindowsFeature = null);

public sealed record InactiveFile(
    string FullPath,
    string RelativePath,
    long Size,
    DateTimeOffset LastActivity);

public sealed record FileReviewResult(
    IReadOnlyList<InactiveFile> InactiveFiles,
    long TotalFiles,
    long TotalBytes);
