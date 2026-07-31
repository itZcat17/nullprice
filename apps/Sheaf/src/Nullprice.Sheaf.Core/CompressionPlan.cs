namespace Nullprice.Sheaf.Core;

public sealed record ImageCompressionItem(int PageIndex, string XObjectName, long OriginalSize, long EstimatedSize);

public sealed record CompressionPlan(IReadOnlyList<ImageCompressionItem> Items)
{
    public long TotalOriginal => Items.Sum(i => i.OriginalSize);
    public long TotalEstimated => Items.Sum(i => i.EstimatedSize);
}

/// <summary>
/// Previews what compressing a document's images would do before anything is written —
/// mirrors Batch's "show the plan before running it" UX. Only JPEG (DCTDecode-filtered)
/// images are recompressed in v1; raw or losslessly-filtered bitmap samples are left
/// untouched (an explicit v1 boundary, not a silent gap — decoding arbitrary PDF sample
/// layouts into a bitmap is real added scope for a case that's less common in practice than
/// an already-JPEG photo).
/// </summary>
public static class CompressionPlanner
{
    public static CompressionPlan Build(PdfDocument document, IReadOnlyList<int> pageIndices, int quality, IRasterRecompressor recompressor)
    {
        var items = new List<ImageCompressionItem>();

        foreach (var pageIndex in pageIndices)
        {
            if (pageIndex < 0 || pageIndex >= document.Pages.Count) continue;

            foreach (var (name, stream) in ImageXObjects.Find(document.Objects, document.Pages[pageIndex].Dictionary))
            {
                if (!ImageXObjects.IsJpeg(stream)) continue;

                var estimated = recompressor.EstimateSize(stream.RawBytes, "DCTDecode", quality);
                items.Add(new ImageCompressionItem(pageIndex, name, stream.RawBytes.Length, estimated));
            }
        }

        return new CompressionPlan(items);
    }
}

/// <summary>Shared lookup for a page's <c>/Resources/XObject</c> images — used by both the
/// preview (<see cref="CompressionPlanner"/>, against a source document) and the actual apply
/// step (<see cref="SheafRunner"/>, against the document being built).</summary>
internal static class ImageXObjects
{
    public static IEnumerable<(string Name, PdfStream Stream)> Find(PdfObjectTable objects, PdfDictionary pageDict)
    {
        var resources = objects.Resolve(pageDict.Get("Resources")) as PdfDictionary;
        var xobjects = resources is null ? null : objects.Resolve(resources.Get("XObject")) as PdfDictionary;
        if (xobjects is null) yield break;

        foreach (var (name, value) in xobjects.Entries)
        {
            if (objects.Resolve(value) is PdfStream stream && (stream.Dictionary.Get("Subtype") as PdfName)?.Value == "Image")
                yield return (name, stream);
        }
    }

    public static bool IsJpeg(PdfStream stream)
    {
        if ((stream.Dictionary.Get("Subtype") as PdfName)?.Value != "Image") return false;
        var filters = FilterNamesOf(stream.Dictionary.Get("Filter"));
        return filters.Count > 0 && filters[^1] == "DCTDecode";
    }

    private static List<string> FilterNamesOf(PdfObject? filterObj) => filterObj switch
    {
        PdfName n => [n.Value],
        PdfArray a => a.Items.OfType<PdfName>().Select(n => n.Value).ToList(),
        _ => [],
    };
}
