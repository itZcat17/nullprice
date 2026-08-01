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

/// <summary>New typed text in a font of the user's choosing — <see cref="FontBytes"/> is the
/// chosen system font's own raw sfnt bytes (resolved by the App layer, which is the only place
/// with access to installed-font APIs; Core has no platform dependency and never enumerates
/// system fonts itself). <see cref="X"/>/<see cref="Y"/> is the text baseline's start point, in
/// PDF user-space, matching how <see cref="ContentStreamTextEditor"/> already treats existing
/// text positions. Building the actual embedded font is <see cref="TrueTypeSubsetter"/>'s job,
/// invoked from <see cref="AnnotationWriter"/> — this record only carries what that needs.</summary>
public sealed record FreeTextEdit(int PageIndex, double X, double Y, double FontSize, string Text, string ColorHex, byte[] FontBytes, string FontFamilyName) : AnnotationEdit(PageIndex);

/// <summary>Places an image on the page. <see cref="JpegBytes"/> is already-decoded-and-JPEG-encoded
/// image data — the App layer owns the actual image codec (WIC), the same interface-free
/// "resolve platform-specific bytes before building the edit record" shape
/// <see cref="FreeTextEdit.FontBytes"/> already uses, rather than a new Core interface for a
/// one-shot, UI-driven operation with no equivalent to <see cref="SheafRunner"/> discovering
/// and deciding about images on its own (that's what <see cref="IRasterRecompressor"/> is for).
/// <see cref="PixelWidth"/>/<see cref="PixelHeight"/> are the source image's natural pixel
/// dimensions, needed for the PDF <c>/Width</c>/<c>/Height</c> keys.</summary>
public sealed record ImageStampEdit(int PageIndex, double X, double Y, double W, double H, byte[] JpegBytes, int PixelWidth, int PixelHeight) : AnnotationEdit(PageIndex);
