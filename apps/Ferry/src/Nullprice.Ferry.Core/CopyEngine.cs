using System.Diagnostics;
using System.Security.Cryptography;

namespace Nullprice.Ferry.Core;

/// <summary>
/// Copies files and proves they arrived intact.
/// <para>
/// The verification is deliberately done by re-reading the destination from disk rather
/// than by hashing the buffer on its way out. Hashing what you wrote only tells you what
/// you intended to write; hashing what came back tells you what actually landed, which is
/// the failure this application exists to catch.
/// </para>
/// </summary>
public sealed class CopyEngine
{
    /// <summary>Large enough to keep spinning disks streaming, small enough to stay responsive.</summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>How often to push a progress update, so the UI is not flooded.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    public async Task<TransferReport> RunAsync(
        CopyPlan plan,
        IProgress<CopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ItemResult>(plan.TotalFiles);
        var overall = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        long bytesDone = 0;
        var filesDone = 0;
        var cancelled = false;

        foreach (var item in plan.Items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var itemClock = Stopwatch.StartNew();
            ItemResult result;

            try
            {
                result = await CopyOneAsync(
                    item,
                    plan,
                    bytesAlreadyDone: bytesDone,
                    onBytes: written =>
                    {
                        // Only report on an interval; a per-buffer callback would swamp the UI.
                        if (progress is null) return;
                        if (overall.Elapsed - lastReport < ProgressInterval) return;

                        lastReport = overall.Elapsed;
                        progress.Report(new CopyProgress(
                            filesDone,
                            plan.TotalFiles,
                            bytesDone + written,
                            plan.TotalBytes,
                            item.SourcePath,
                            Rate(bytesDone + written, overall.Elapsed)));
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                result = new ItemResult(item, ItemOutcome.Failed, null, ex.Message, itemClock.Elapsed);
            }

            results.Add(result with { Duration = itemClock.Elapsed });

            filesDone++;
            if (result.Outcome != ItemOutcome.Skipped) bytesDone += item.Length;

            progress?.Report(new CopyProgress(
                filesDone,
                plan.TotalFiles,
                bytesDone,
                plan.TotalBytes,
                item.SourcePath,
                Rate(bytesDone, overall.Elapsed)));
        }

        return new TransferReport(results, overall.Elapsed, cancelled);
    }

    private static async Task<ItemResult> CopyOneAsync(
        CopyItem item,
        CopyPlan plan,
        long bytesAlreadyDone,
        Action<long> onBytes,
        CancellationToken cancellationToken)
    {
        var destination = item.DestinationPath;

        if (File.Exists(destination))
        {
            switch (plan.Policy)
            {
                case ConflictPolicy.Skip:
                    return new ItemResult(item, ItemOutcome.Skipped, destination, null, TimeSpan.Zero);

                case ConflictPolicy.Fail:
                    return new ItemResult(item, ItemOutcome.Failed, null,
                        "Destination already exists.", TimeSpan.Zero);

                case ConflictPolicy.Rename:
                    destination = NextFreeName(destination);
                    break;

                case ConflictPolicy.Overwrite:
                    break;
            }
        }

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Write to a temporary neighbour first, so an interrupted copy never leaves a
        // half-written file sitting at the real destination looking complete.
        var temp = destination + ".ferrypart";

        try
        {
            var sourceHash = await CopyBytesAsync(item.SourcePath, temp, onBytes, cancellationToken)
                .ConfigureAwait(false);

            if (plan.Verify)
            {
                var writtenHash = await HashFileAsync(temp, cancellationToken).ConfigureAwait(false);

                if (!sourceHash.AsSpan().SequenceEqual(writtenHash))
                {
                    // Keep the evidence. Deleting it would destroy the only copy of whatever
                    // went wrong, and the user may still want the partial data.
                    var quarantine = destination + ".unverified";
                    SafeMove(temp, quarantine);

                    return new ItemResult(item, ItemOutcome.VerificationFailed, quarantine,
                        "Destination hash did not match source after writing.", TimeSpan.Zero);
                }
            }

            SafeMove(temp, destination);
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(item.SourcePath));

            return new ItemResult(item, ItemOutcome.Copied, destination, null, TimeSpan.Zero);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Streams source to destination, hashing the source as it reads.</summary>
    private static async Task<byte[]> CopyBytesAsync(
        string source,
        string destination,
        Action<long> onBytes,
        CancellationToken cancellationToken)
    {
        using var hasher = SHA256.Create();
        var buffer = new byte[BufferSize];
        long written = 0;

        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        await using var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous);

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            hasher.TransformBlock(buffer, 0, read, null, 0);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

            written += read;
            onBytes(written);
        }

        hasher.TransformFinalBlock([], 0, 0);

        // Push the data past the OS cache before anyone claims the file is safely written.
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        output.Flush(flushToDisk: true);

        return hasher.Hash ?? [];
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>"report.pdf" becomes "report (2).pdf", then "report (3).pdf", and so on.</summary>
    internal static string NextFreeName(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var n = 2; n < int.MaxValue; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        throw new IOException($"No free filename available for {path}.");
    }

    private static void SafeMove(string from, string to)
    {
        if (File.Exists(to)) File.Delete(to);
        File.Move(from, to);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup is best-effort; the original failure is the one worth surfacing.
        }
    }

    private static double Rate(long bytes, TimeSpan elapsed) =>
        elapsed.TotalSeconds <= 0.001 ? 0.0 : bytes / elapsed.TotalSeconds;
}
