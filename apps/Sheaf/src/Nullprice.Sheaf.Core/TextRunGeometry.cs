namespace Nullprice.Sheaf.Core;

/// <summary>
/// Shared text-run geometry helpers used by both redaction and in-place text editing: which
/// operators show text, roughly where they land in page space, and the raw string they draw.
/// <para>
/// Bounding boxes are an approximation (font size for height, an average-glyph-width heuristic
/// for width) rather than exact glyph metrics — real per-glyph widths need a loaded font
/// (<see cref="IGlyphFont"/>), which redaction never needs and text editing only needs for the
/// font whose text is actually being clicked, not for locating it in the first place. The
/// heuristic is deliberately generous, so a hit-test is more likely to catch a borderline case
/// than to miss it.
/// </para>
/// </summary>
public static class TextRunGeometry
{
    private const double TextWidthHeuristic = 0.6; // average glyph advance as a fraction of font size

    public static bool IsTextShow(string op) => op is "Tj" or "TJ" or "'" or "\"";

    public static (double X0, double Y0, double X1, double Y1) Bounds(ContentOp op, GraphicsState state)
    {
        var combined = PdfMatrix.Multiply(state.TextMatrix, state.Ctm);
        var text = TextOf(op);
        var width = Math.Max(1, text.Length) * state.FontSize * TextWidthHeuristic;
        var height = state.FontSize <= 0 ? 1 : state.FontSize;

        var (x0, y0) = combined.Apply(0, 0);
        var (x1, y1) = combined.Apply(width, height);
        return (x0, y0, x1, y1);
    }

    public static string TextOf(ContentOp op)
    {
        if (op.Operands.Count == 0) return "";
        return op.Operands[0] switch
        {
            PdfString s => System.Text.Encoding.Latin1.GetString(s.Bytes),
            PdfArray a => string.Concat(a.Items.OfType<PdfString>().Select(s => System.Text.Encoding.Latin1.GetString(s.Bytes))),
            _ => "",
        };
    }
}
