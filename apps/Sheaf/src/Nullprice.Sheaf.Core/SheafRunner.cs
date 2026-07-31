namespace Nullprice.Sheaf.Core;

public sealed record SheafProgress(int Done, int Total, string CurrentOutput)
{
    public double Fraction => Total <= 0 ? 0.0 : (double)Done / Total;
}

public enum SheafOutcome { Written, Failed }

public sealed record SheafResult(SheafOutput Output, SheafOutcome Outcome, string? Error);

public sealed record SheafReport(IReadOnlyList<SheafResult> Results, TimeSpan Duration, bool WasCancelled)
{
    public int Written => Results.Count(r => r.Outcome == SheafOutcome.Written);
    public int Failed => Results.Count(r => r.Outcome == SheafOutcome.Failed);
    public bool IsClean => !WasCancelled && Failed == 0;
}

/// <summary>
/// Runs a <see cref="SheafPlan"/>: opens every source once, then for each output selects,
/// reorders, rotates, and imports the pages it needs and writes a fresh PDF. Every output
/// draws its initial page list from every source in the plan (an output narrows that down to
/// what it actually wants via its own <see cref="PageOperation"/>s), so a source that fails to
/// open fails every output in the same plan — but as a reported per-output failure, not an
/// exception thrown out of <see cref="RunAsync"/>, matching Batch's "one bad file is reported
/// and skipped, not fatal to the run" behaviour at the level Sheaf's model actually supports.
/// </summary>
public sealed class SheafRunner
{
    public async Task<SheafReport> RunAsync(
        SheafPlan plan,
        IProgress<SheafProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        var results = new List<SheafResult>();
        var cancelled = false;

        var openDocs = new Dictionary<string, (PdfDocument? Doc, string? Error)>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in plan.Sources)
        {
            if (openDocs.ContainsKey(source.Path)) continue;
            try
            {
                var bytes = await File.ReadAllBytesAsync(source.Path, cancellationToken);
                var open = PdfDocument.Open(bytes, source.Password);
                openDocs[source.Path] = open.Status == PdfOpenStatus.Success
                    ? (open.Document, null)
                    : (null, open.Message ?? "Could not open this PDF.");
            }
            catch (Exception ex)
            {
                openDocs[source.Path] = (null, ex.Message);
            }
        }

        var total = plan.Outputs.Count;
        var done = 0;

        foreach (var output in plan.Outputs)
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            progress?.Report(new SheafProgress(done, total, Path.GetFileName(output.Path)));

            try
            {
                var bytes = BuildOutput(plan.Sources, openDocs, output);
                await WriteAtomicAsync(output.Path, bytes, cancellationToken);
                results.Add(new SheafResult(output, SheafOutcome.Written, null));
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                results.Add(new SheafResult(output, SheafOutcome.Failed, ex.Message));
            }

            done++;
            progress?.Report(new SheafProgress(done, total, Path.GetFileName(output.Path)));
        }

        return new SheafReport(results, DateTime.UtcNow - started, cancelled);
    }

    private static byte[] BuildOutput(
        IReadOnlyList<MergeSource> sources,
        Dictionary<string, (PdfDocument? Doc, string? Error)> openDocs,
        SheafOutput output)
    {
        var working = new List<(MergeSource Source, PdfPageRef Page, int RotationDelta)>();

        foreach (var source in sources)
        {
            var (doc, error) = openDocs[source.Path];
            if (doc is null) throw new InvalidOperationException(error ?? $"Could not open {Path.GetFileName(source.Path)}.");

            var indices = source.PageIndices ?? Enumerable.Range(0, doc.Pages.Count);
            foreach (var i in indices)
            {
                if (i < 0 || i >= doc.Pages.Count) continue;
                working.Add((source, doc.Pages[i], 0));
            }
        }

        foreach (var op in output.Operations)
        {
            working = op switch
            {
                ReorderOperation reorder => reorder.NewPageOrder
                    .Where(i => i >= 0 && i < working.Count)
                    .Select(i => working[i])
                    .ToList(),
                DeletePageOperation delete when delete.PageIndex >= 0 && delete.PageIndex < working.Count =>
                    working.Where((_, i) => i != delete.PageIndex).ToList(),
                RotateOperation rotate when rotate.PageIndex >= 0 && rotate.PageIndex < working.Count =>
                    working.Select((cell, i) => i == rotate.PageIndex
                        ? (cell.Source, cell.Page, cell.RotationDelta + rotate.Degrees)
                        : cell)
                        .ToList(),
                _ => working,
            };
        }

        if (working.Count == 0)
            throw new InvalidOperationException("This output has no pages left to write.");

        var destination = new PdfObjectTable();
        var importers = new Dictionary<PdfDocument, PdfGraphImporter>();
        var importedRefs = new List<PdfReference>();

        foreach (var (source, page, rotationDelta) in working)
        {
            var (doc, _) = openDocs[source.Path];
            if (!importers.TryGetValue(doc!, out var importer))
                importers[doc!] = importer = new PdfGraphImporter(doc!.Objects, destination);

            var pageRef = importer.ImportPage(page.Dictionary);

            if (rotationDelta != 0 && destination.TryGet(pageRef.Number, pageRef.Generation, out var imported) && imported is PdfDictionary importedDict)
            {
                var existingRotate = (destination.Resolve(importedDict.Get("Rotate")) as PdfNumber)?.AsInt ?? 0;
                var normalized = ((existingRotate + rotationDelta) % 360 + 360) % 360;
                destination.Set(pageRef.Number, pageRef.Generation, importedDict.With("Rotate", new PdfNumber(normalized)));
            }

            importedRefs.Add(pageRef);
        }

        var pagesRef = new PdfReference(destination.Allocate(), 0);
        foreach (var pageRef in importedRefs)
        {
            if (destination.TryGet(pageRef.Number, pageRef.Generation, out var pageObj) && pageObj is PdfDictionary pageDict)
                destination.Set(pageRef.Number, pageRef.Generation, pageDict.With("Parent", pagesRef));
        }

        destination.Set(pagesRef.Number, pagesRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Pages"),
            ["Kids"] = new PdfArray(importedRefs.Cast<PdfObject>().ToList()),
            ["Count"] = new PdfNumber(importedRefs.Count),
        }));

        var rootRef = new PdfReference(destination.Allocate(), 0);
        destination.Set(rootRef.Number, rootRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Catalog"),
            ["Pages"] = pagesRef,
        }));

        var trailer = new PdfDictionary(new Dictionary<string, PdfObject> { ["Root"] = rootRef });
        return PdfWriter.Write(PdfDocument.Create(destination, trailer));
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var partPath = path + ".sheafpart";
        await File.WriteAllBytesAsync(partPath, bytes, cancellationToken);
        File.Move(partPath, path, overwrite: true);
    }
}
