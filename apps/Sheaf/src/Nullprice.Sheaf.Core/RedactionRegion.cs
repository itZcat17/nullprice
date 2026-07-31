namespace Nullprice.Sheaf.Core;

/// <summary>A rectangle to redact, in PDF user-space units on a specific output page (the
/// page index is into the output's final, post-reorder page list — the same numbering the
/// person drawing the box on screen would see).</summary>
public sealed record RedactionRegion(int PageIndex, double X, double Y, double Width, double Height)
{
    public bool Intersects(double x0, double y0, double x1, double y1)
    {
        var left = Math.Min(x0, x1);
        var right = Math.Max(x0, x1);
        var bottom = Math.Min(y0, y1);
        var top = Math.Max(y0, y1);
        return left < X + Width && right > X && bottom < Y + Height && top > Y;
    }
}
