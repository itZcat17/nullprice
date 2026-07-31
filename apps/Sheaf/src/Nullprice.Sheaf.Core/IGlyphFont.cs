namespace Nullprice.Sheaf.Core;

/// <summary>The one thing Core cannot verify itself: whether a specific embedded font program
/// actually contains a glyph for a given character. A subsetted font only has glyphs for
/// whatever was originally typeset on the page — there is no way to know that from the PDF's
/// own <c>/Widths</c> declarations alone, which can overstate coverage (some generators
/// declare widths for the full base encoding even when only a few glyphs are actually
/// embedded). Implemented in the App project against WPF's <c>GlyphTypeface</c>, the same
/// interface-in-Core, implementation-in-App boundary as <see cref="IPdfRenderer"/> and
/// <see cref="IRasterRecompressor"/>.</summary>
public interface IGlyphFont
{
    bool HasGlyph(int unicodeCodepoint);

    bool TryGetGlyph(int unicodeCodepoint, out ushort glyphIndex, out double advanceWidthEm);
}

public interface IGlyphFontLoader
{
    IGlyphFont? Load(byte[] fontProgramBytes);
}
