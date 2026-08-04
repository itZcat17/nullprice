using System.Diagnostics;
using System.Net.Http;

namespace Nullprice.Haul.Core;

/// <summary>
/// Downloads each job's format to disk. Unlike Batch/Ferry, there is no App-side platform
/// interface here — streaming bytes from an HTTP response to a file needs nothing OS-specific,
/// so Core does the network I/O directly. The <see cref="HttpClient"/> is injected so tests can
/// hand in one backed by a fake <see cref="HttpMessageHandler"/> instead of touching the network.
/// </summary>
public sealed class HaulRunner(HttpClient httpClient)
{
    private const int BufferSize = 81920;

    public async Task<HaulReport> RunAsync(
        HaulPlan plan,
        IProgress<HaulProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<HaulResult>(plan.Total);
        var clock = Stopwatch.StartNew();
        var cancelled = false;
        var done = 0;

        foreach (var job in plan.Jobs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            progress?.Report(new HaulProgress(done, plan.Total, 0, null, job.SourceUrl));

            try
            {
                var directory = Path.GetDirectoryName(job.OutputPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                await DownloadOneAsync(job, done, plan.Total, progress, cancellationToken).ConfigureAwait(false);
                results.Add(new HaulResult(job, HaulOutcome.Downloaded, null));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                // One bad link must not abandon the rest of the queue.
                results.Add(new HaulResult(job, HaulOutcome.Failed, ex.Message));
            }

            done++;
            progress?.Report(new HaulProgress(done, plan.Total, 0, null, job.SourceUrl));
        }

        return new HaulReport(results, clock.Elapsed, cancelled);
    }

    /// <summary>
    /// Streams to a temporary neighbour and renames on success, so a failed or cancelled
    /// download never leaves a partial file sitting at the real destination looking complete.
    /// No HTTP range-resume in v1 — a retry always restarts from zero.
    /// </summary>
    private async Task DownloadOneAsync(
        HaulJob job,
        int done,
        int total,
        IProgress<HaulProgress>? progress,
        CancellationToken cancellationToken)
    {
        var temp = job.OutputPath + ".haulpart";

        try
        {
            using var response = await httpClient
                .GetAsync(job.Format.DirectUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous);

            var buffer = new byte[BufferSize];
            long written = 0;

            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;

                progress?.Report(new HaulProgress(done, total, written, totalBytes, job.SourceUrl));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        SafeMove(temp, job.OutputPath);
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
}
