namespace Nullprice.Sheaf.Core;

/// <summary>
/// Removes content-stream operators whose drawn output overlaps a redaction region, so the
/// underlying text or image data is gone from the output — not merely covered. Operates at
/// whole-operator granularity: a text-showing operator or an image <c>Do</c> either survives
/// untouched or is dropped entirely. Explicit v1 boundaries: no partial-glyph or
/// partial-image redaction, and this only scans the visible content stream — metadata,
/// annotations, and structure-tree text are not touched.
/// <para>
/// Text bounding boxes are an approximation (font size for height, an average-glyph-width
/// heuristic for width) rather than exact glyph metrics, since real per-glyph widths need the
/// embedded font work built for text editing (a later milestone). The heuristic is
/// deliberately generous, so it is far more likely to over-redact a borderline case than to
/// leave targeted text behind.
/// </para>
/// </summary>
public static class ContentStreamRedactor
{
    public static byte[] Redact(byte[] contentBytes, int pageIndex, IReadOnlyList<RedactionRegion> regions)
    {
        var relevant = regions.Where(r => r.PageIndex == pageIndex).ToList();
        if (relevant.Count == 0) return contentBytes;

        var ops = ContentStreamReader.Read(contentBytes);
        var kept = new List<ContentOp>();

        ContentStreamWalker.Walk(ops, PdfMatrix.Identity, (op, state) =>
        {
            if (TextRunGeometry.IsTextShow(op.Operator))
            {
                var (x0, y0, x1, y1) = TextRunGeometry.Bounds(op, state);
                if (!relevant.Any(r => r.Intersects(x0, y0, x1, y1))) kept.Add(op);
                return;
            }

            if (op.Operator == "Do")
            {
                var (x0, y0, x1, y1) = ImageBounds(state.Ctm);
                if (!relevant.Any(r => r.Intersects(x0, y0, x1, y1))) kept.Add(op);
                return;
            }

            kept.Add(op);
        });

        return ContentStreamWriter.Write(kept);
    }

    private static (double, double, double, double) ImageBounds(PdfMatrix ctm)
    {
        var (x0, y0) = ctm.Apply(0, 0);
        var (x1, y1) = ctm.Apply(1, 1);
        return (Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }
}
