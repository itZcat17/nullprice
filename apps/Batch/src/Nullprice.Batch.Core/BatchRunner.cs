using System.Diagnostics;

namespace Nullprice.Batch.Core;

/// <summary>
/// The one thing Core cannot do itself: turn pixels into other pixels.
/// Implemented in the app layer against Windows Imaging Component, and stubbed in tests
/// so every decision above this line stays verifiable without touching an image codec.
/// </summary>
public interface IImageProcessor
{
    /// <summary>Applies the pipeline to one file and writes the result. Must not touch the source.</summary>
    Task ApplyAsync(BatchItem item, Pipeline pipeline, CancellationToken cancellationToken);

    /// <summary>Reads dimensions without decoding the whole image, where the codec allows.</summary>
    (int Width, int Height) Measure(string path);
}

public sealed class BatchRunner(IImageProcessor processor)
{
    public async Task<BatchReport> RunAsync(
        BatchPlan plan,
        IProgress<BatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BatchResult>(plan.Total);
        var clock = Stopwatch.StartNew();
        var cancelled = false;
        var done = 0;

        Directory.CreateDirectory(plan.OutputDirectory);

        foreach (var item in plan.Items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            progress?.Report(new BatchProgress(done, plan.Total, item.SourcePath));

            try
            {
                var directory = Path.GetDirectoryName(item.DestinationPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                await processor.ApplyAsync(item, plan.Pipeline, cancellationToken).ConfigureAwait(false);
                results.Add(new BatchResult(item, BatchOutcome.Written, null));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                // One unreadable file must not abandon the other nine hundred.
                results.Add(new BatchResult(item, BatchOutcome.Failed, ex.Message));
            }

            done++;
            progress?.Report(new BatchProgress(done, plan.Total, item.SourcePath));
        }

        return new BatchReport(results, clock.Elapsed, cancelled);
    }
}
