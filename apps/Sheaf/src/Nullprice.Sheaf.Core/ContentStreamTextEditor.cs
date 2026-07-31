namespace Nullprice.Sheaf.Core;

public sealed record TextRunLocation(int OperatorIndex, string Text, double X, double Y, double FontSize, string? FontResourceName);

/// <summary>
/// Locates and rewrites a single text-showing operator in a page's content stream — the
/// in-place editing counterpart to <see cref="ContentStreamRedactor"/>, sharing the same
/// approximate hit-testing (<see cref="TextRunGeometry"/>) but changing content instead of
/// removing it. Single-line edits only: this replaces one operator's shown text using the
/// font's own character codes, along the operator's existing baseline — it never touches
/// surrounding <c>Td</c>/<c>Tm</c> positioning, so it neither needs nor attempts to reflow
/// anything that follows on the same line.
/// </summary>
public static class ContentStreamTextEditor
{
    /// <summary>Finds the text-showing operator whose approximate bounding box contains the
    /// given point, for a click-to-edit UI. Returns null if nothing is under the point.</summary>
    public static TextRunLocation? FindTextAt(byte[] contentBytes, double x, double y)
    {
        var ops = ContentStreamReader.Read(contentBytes);
        TextRunLocation? found = null;
        var index = -1;

        ContentStreamWalker.Walk(ops, PdfMatrix.Identity, (op, state) =>
        {
            index++;
            if (found is not null || !TextRunGeometry.IsTextShow(op.Operator)) return;

            var (x0, y0, x1, y1) = TextRunGeometry.Bounds(op, state);
            var left = Math.Min(x0, x1);
            var right = Math.Max(x0, x1);
            var bottom = Math.Min(y0, y1);
            var top = Math.Max(y0, y1);
            if (x < left || x > right || y < bottom || y > top) return;

            found = new TextRunLocation(index, TextRunGeometry.TextOf(op), x0, y0, state.FontSize, state.FontName);
        });

        return found;
    }

    /// <summary>Rewrites the operator at <paramref name="operatorIndex"/> to show
    /// <paramref name="newText"/>, re-encoding each character through <paramref name="font"/>'s
    /// code mapping. Always emits a plain <c>Tj</c> regardless of the original operator's
    /// exact form (<c>TJ</c>'s per-glyph kerning adjustments don't carry meaning for
    /// arbitrary edited text) — the starting position is untouched, since that comes from
    /// whatever <c>Td</c>/<c>Tm</c> preceded it, not from the text-showing operator itself.
    /// Returns null if the index is out of range, isn't a text-showing operator, or the text
    /// contains a character the font has no code for — callers should validate with
    /// <see cref="TextEditPlanner"/> first so this failure path is unreachable in practice.</summary>
    public static byte[]? Rewrite(byte[] contentBytes, int operatorIndex, string newText, ExtractedFont font)
    {
        var ops = ContentStreamReader.Read(contentBytes);
        if (operatorIndex < 0 || operatorIndex >= ops.Count) return null;
        if (!TextRunGeometry.IsTextShow(ops[operatorIndex].Operator)) return null;

        var codeBytes = new byte[newText.Length];
        for (var i = 0; i < newText.Length; i++)
        {
            if (!font.UnicodeToCode.TryGetValue(newText[i], out var code)) return null;
            codeBytes[i] = (byte)code;
        }

        var newOps = ops.ToList();
        newOps[operatorIndex] = new ContentOp("Tj", [new PdfString(codeBytes)]);

        return ContentStreamWriter.Write(newOps);
    }
}
