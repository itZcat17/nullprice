namespace Nullprice.Sheaf.Core;

/// <summary>A PDF-style 2D affine transform: x' = a·x + c·y + e, y' = b·x + d·y + f
/// (ISO 32000-1 §8.3.4). Kept independent of any UI framework's matrix type since Core has no
/// UI dependency.</summary>
public readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static readonly PdfMatrix Identity = new(1, 0, 0, 1, 0, 0);

    public static PdfMatrix Multiply(PdfMatrix m1, PdfMatrix m2) => new(
        m1.A * m2.A + m1.B * m2.C,
        m1.A * m2.B + m1.B * m2.D,
        m1.C * m2.A + m1.D * m2.C,
        m1.C * m2.B + m1.D * m2.D,
        m1.E * m2.A + m1.F * m2.C + m2.E,
        m1.E * m2.B + m1.F * m2.D + m2.F);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
}

/// <summary>The graphics state visible to a single content-stream operator: the current
/// transform, text matrix, and font — everything needed to answer "where does this operator's
/// mark actually land on the page". Only the state redaction (M3) and text editing (M5)
/// actually need is tracked; text leading (<c>TL</c>/<c>T*</c>) and stroke/fill color are not.</summary>
public sealed record GraphicsState(PdfMatrix Ctm, PdfMatrix TextMatrix, PdfMatrix TextLineMatrix, string? FontName, double FontSize);

/// <summary>Walks a content stream's operators while tracking graphics/text state
/// (ISO 32000-1 §8.4, §9.4) — the shared piece redaction and text editing both need, since
/// both have to answer where a given operator's content lands in page space.</summary>
public static class ContentStreamWalker
{
    public static void Walk(IReadOnlyList<ContentOp> ops, PdfMatrix initialCtm, Action<ContentOp, GraphicsState> onOperator)
    {
        var stack = new Stack<PdfMatrix>();
        var ctm = initialCtm;
        var textMatrix = PdfMatrix.Identity;
        var textLineMatrix = PdfMatrix.Identity;
        string? fontName = null;
        double fontSize = 0;

        foreach (var op in ops)
        {
            switch (op.Operator)
            {
                case "q":
                    stack.Push(ctm);
                    break;
                case "Q":
                    if (stack.Count > 0) ctm = stack.Pop();
                    break;
                case "cm":
                    ctm = PdfMatrix.Multiply(ReadMatrix(op.Operands), ctm);
                    break;
                case "BT":
                    textMatrix = PdfMatrix.Identity;
                    textLineMatrix = PdfMatrix.Identity;
                    break;
                case "Tm":
                    textMatrix = ReadMatrix(op.Operands);
                    textLineMatrix = textMatrix;
                    break;
                case "Td":
                case "TD":
                    textLineMatrix = PdfMatrix.Multiply(new PdfMatrix(1, 0, 0, 1, Num(op.Operands, 0), Num(op.Operands, 1)), textLineMatrix);
                    textMatrix = textLineMatrix;
                    break;
                case "Tf":
                    fontName = op.Operands.Count > 0 ? (op.Operands[0] as PdfName)?.Value : null;
                    fontSize = Num(op.Operands, 1);
                    break;
            }

            onOperator(op, new GraphicsState(ctm, textMatrix, textLineMatrix, fontName, fontSize));
        }
    }

    private static PdfMatrix ReadMatrix(IReadOnlyList<PdfObject> operands) =>
        new(Num(operands, 0), Num(operands, 1), Num(operands, 2), Num(operands, 3), Num(operands, 4), Num(operands, 5));

    private static double Num(IReadOnlyList<PdfObject> operands, int index) =>
        index < operands.Count && operands[index] is PdfNumber n ? n.Value : 0;
}
