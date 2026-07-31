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
public sealed class SheafRunner(IRasterRecompressor? recompressor = null)
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

    private byte[] BuildOutput(
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

        // Text edits before redaction, and redaction before compression: text edits rely on
        // operator indices captured against the pristine imported content (see TextEdit's own
        // doc comment for why), and a page's content should be fully settled — including which
        // images are even still referenced — before anything about the surviving images is
        // recompressed.
        if (output.TextEdits is { Count: > 0 } textEdits)
            ApplyTextEdits(destination, importedRefs, textEdits);

        if (output.Redactions is { Count: > 0 } redactions)
            ApplyRedactions(destination, importedRefs, redactions);

        if (output.Compression is { } compression)
        {
            if (recompressor is null)
                throw new InvalidOperationException("Compression was requested but no image recompressor is configured.");
            ApplyCompression(destination, compression, recompressor);
        }

        // Pruned unconditionally, not just when redacting: an unreachable object's bytes are
        // still physically present in the file otherwise, which would make "redaction" hollow,
        // and it also keeps a plain merge/split from carrying over anything a page didn't
        // actually end up referencing.
        var pruned = PdfGarbageCollector.Prune(destination, trailer);
        return PdfWriter.Write(PdfDocument.Create(pruned, trailer));
    }

    private static void ApplyTextEdits(PdfObjectTable destination, IReadOnlyList<PdfReference> importedPageRefs, IReadOnlyList<TextEdit> edits)
    {
        foreach (var group in edits.GroupBy(e => e.PageIndex))
        {
            var i = group.Key;
            if (i < 0 || i >= importedPageRefs.Count) continue;

            var pageRef = importedPageRefs[i];
            if (!destination.TryGet(pageRef.Number, pageRef.Generation, out var pageObj) || pageObj is not PdfDictionary pageDict) continue;

            var current = ContentStreamCombiner.Combine(destination, pageDict.Get("Contents"));
            if (current is null) continue;

            foreach (var edit in group)
            {
                var fontDict = EmbeddedFontExtractor.ResolveFontResource(destination, pageDict, edit.FontResourceName)
                    ?? throw new InvalidOperationException($"Could not find font resource '{edit.FontResourceName}' for a text edit on page {i + 1}.");

                var font = EmbeddedFontExtractor.Extract(destination, fontDict)
                    ?? throw new InvalidOperationException($"This page's font can't be used for in-place text editing (page {i + 1}).");

                current = ContentStreamTextEditor.Rewrite(current, edit.OperatorIndex, edit.NewText, font)
                    ?? throw new InvalidOperationException($"Couldn't apply a text edit on page {i + 1} — the font may not include every character typed.");
            }

            var encoded = FilterCodec.Encode(current);
            var streamDict = new PdfDictionary(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") });
            var newStreamNum = destination.Allocate();
            destination.Set(newStreamNum, 0, new PdfStream(streamDict, encoded));
            destination.Set(pageRef.Number, pageRef.Generation, pageDict.With("Contents", new PdfReference(newStreamNum, 0)));
        }
    }

    private static void ApplyRedactions(PdfObjectTable destination, IReadOnlyList<PdfReference> importedPageRefs, IReadOnlyList<RedactionRegion> regions)
    {
        for (var i = 0; i < importedPageRefs.Count; i++)
        {
            if (!regions.Any(r => r.PageIndex == i)) continue;

            var pageRef = importedPageRefs[i];
            if (!destination.TryGet(pageRef.Number, pageRef.Generation, out var pageObj) || pageObj is not PdfDictionary pageDict) continue;

            var combined = ContentStreamCombiner.Combine(destination, pageDict.Get("Contents"));
            if (combined is null) continue;

            var redacted = ContentStreamRedactor.Redact(combined, i, regions);
            var encoded = FilterCodec.Encode(redacted);

            var streamDict = new PdfDictionary(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") });
            var newStreamNum = destination.Allocate();
            destination.Set(newStreamNum, 0, new PdfStream(streamDict, encoded));

            destination.Set(pageRef.Number, pageRef.Generation, pageDict.With("Contents", new PdfReference(newStreamNum, 0)));
        }
    }

    private static void ApplyCompression(PdfObjectTable destination, CompressionSettings settings, IRasterRecompressor recompressor)
    {
        foreach (var (key, value) in destination.All.ToList())
        {
            if (value is not PdfStream stream || !ImageXObjects.IsJpeg(stream)) continue;

            var recompressed = recompressor.Recompress(stream.RawBytes, "DCTDecode", settings.Quality, out var resultFilter);
            if (recompressed.Length >= stream.RawBytes.Length) continue; // not worth the quality loss

            destination.Set(key.Number, key.Generation, new PdfStream(stream.Dictionary.With("Filter", new PdfName(resultFilter)), recompressed));
        }
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
