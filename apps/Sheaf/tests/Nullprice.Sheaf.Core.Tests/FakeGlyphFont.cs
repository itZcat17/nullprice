namespace Nullprice.Sheaf.Core.Tests;

internal sealed class FakeGlyphFont(IReadOnlySet<int> supportedCodepoints) : IGlyphFont
{
    public bool HasGlyph(int unicodeCodepoint) => supportedCodepoints.Contains(unicodeCodepoint);

    public bool TryGetGlyph(int unicodeCodepoint, out ushort glyphIndex, out double advanceWidthEm)
    {
        if (HasGlyph(unicodeCodepoint))
        {
            glyphIndex = (ushort)unicodeCodepoint;
            advanceWidthEm = 0.5;
            return true;
        }

        glyphIndex = 0;
        advanceWidthEm = 0;
        return false;
    }
}
