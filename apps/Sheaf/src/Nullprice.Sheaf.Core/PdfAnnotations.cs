namespace Nullprice.Sheaf.Core;

/// <summary>
/// A markup edit to add to a page, expressed in PDF user-space geometry with no PDF object
/// types leaked into the App layer. Everything here is purely additive — see
/// <see cref="AnnotationWriter"/> for why these become PDF annotations (<c>/Annots</c>)
/// rather than edits to the page's existing content stream.
/// </summary>
public abstract record AnnotationEdit(int PageIndex);

public sealed record HighlightEdit(int PageIndex, double X, double Y, double W, double H, string ColorHex) : AnnotationEdit(PageIndex);

public sealed record UnderlineEdit(int PageIndex, double X, double Y, double W, double H, string ColorHex) : AnnotationEdit(PageIndex);

public sealed record StrikeOutEdit(int PageIndex, double X, double Y, double W, double H, string ColorHex) : AnnotationEdit(PageIndex);

public sealed record StickyNoteEdit(int PageIndex, double X, double Y, string Text) : AnnotationEdit(PageIndex);

public sealed record LineShapeEdit(int PageIndex, double X1, double Y1, double X2, double Y2, string ColorHex, double LineWidth, bool Arrow) : AnnotationEdit(PageIndex);

public sealed record RectShapeEdit(int PageIndex, double X, double Y, double W, double H, string ColorHex, double LineWidth, string? FillHex) : AnnotationEdit(PageIndex);

public sealed record EllipseShapeEdit(int PageIndex, double X, double Y, double W, double H, string ColorHex, double LineWidth, string? FillHex) : AnnotationEdit(PageIndex);

public sealed record InkEdit(int PageIndex, IReadOnlyList<IReadOnlyList<(double X, double Y)>> Strokes, string ColorHex, double LineWidth) : AnnotationEdit(PageIndex);
