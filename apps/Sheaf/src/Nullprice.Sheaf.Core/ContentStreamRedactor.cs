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
    private const double TextWidthHeuristic = 0.6; // average glyph advance as a fraction of font size

    public static byte[] Redact(byte[] contentBytes, int pageIndex, IReadOnlyList<RedactionRegion> regions)
    {
        var relevant = regions.Where(r => r.PageIndex == pageIndex).ToList();
        if (relevant.Count == 0) return contentBytes;

        var ops = ContentStreamReader.Read(contentBytes);
        var kept = new List<ContentOp>();

        ContentStreamWalker.Walk(ops, PdfMatrix.Identity, (op, state) =>
        {
            if (IsTextShow(op.Operator))
            {
                var (x0, y0, x1, y1) = TextBounds(op, state);
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

    private static bool IsTextShow(string op) => op is "Tj" or "TJ" or "'" or "\"";

    private static (double, double, double, double) TextBounds(ContentOp op, GraphicsState state)
    {
        var combined = PdfMatrix.Multiply(state.TextMatrix, state.Ctm);
        var text = TextOf(op);
        var width = Math.Max(1, text.Length) * state.FontSize * TextWidthHeuristic;
        var height = state.FontSize <= 0 ? 1 : state.FontSize;

        var (x0, y0) = combined.Apply(0, 0);
        var (x1, y1) = combined.Apply(width, height);
        return (x0, y0, x1, y1);
    }

    private static string TextOf(ContentOp op)
    {
        if (op.Operands.Count == 0) return "";
        return op.Operands[0] switch
        {
            PdfString s => System.Text.Encoding.Latin1.GetString(s.Bytes),
            PdfArray a => string.Concat(a.Items.OfType<PdfString>().Select(s => System.Text.Encoding.Latin1.GetString(s.Bytes))),
            _ => "",
        };
    }

    private static (double, double, double, double) ImageBounds(PdfMatrix ctm)
    {
        var (x0, y0) = ctm.Apply(0, 0);
        var (x1, y1) = ctm.Apply(1, 1);
        return (Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
    }
}
