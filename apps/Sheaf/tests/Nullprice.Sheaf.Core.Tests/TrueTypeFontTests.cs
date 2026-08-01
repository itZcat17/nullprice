namespace Nullprice.Sheaf.Core.Tests;

/// <summary>Parses real, installed Windows fonts rather than only synthetic fixtures — the
/// scope boundary (classic TrueType only) and the cmap fallback chain (Unicode BMP/full,
/// falling back to a symbol font's PUA-offset codepoints) are exactly the kind of thing that
/// looks right against a hand-built fixture but breaks on the first real-world font, so this
/// file exercises actual system fonts the same way M1/M2's rendering checks used real PDFs.</summary>
public class TrueTypeFontTests
{
    private static byte[] ReadSystemFont(string fileName) =>
        File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", fileName));

    [Fact]
    public void Arial_parses_as_a_classic_TrueType_font_with_a_working_cmap()
    {
        var result = TrueTypeFont.Parse(ReadSystemFont("arial.ttf"));

        Assert.Null(result.Problem);
        Assert.NotNull(result.Font);
        Assert.True(result.Font!.NumGlyphs > 0);
        Assert.True(result.Font.TryGetGlyphId('A', out var gidUpperA));
        Assert.True(gidUpperA > 0);
        Assert.True(result.Font.TryGetGlyphId('a', out var gidLowerA));
        Assert.NotEqual(gidUpperA, gidLowerA);
    }

    [Fact]
    public void Bahnschrift_is_refused_as_a_variable_font()
    {
        var result = TrueTypeFont.Parse(ReadSystemFont("bahnschrift.ttf"));

        Assert.Null(result.Font);
        Assert.Equal(TrueTypeParseProblem.VariableFont, result.Problem);
    }

    [Fact]
    public void Webdings_symbol_glyphs_are_reachable_by_both_the_PUA_codepoint_and_the_bare_byte()
    {
        var result = TrueTypeFont.Parse(ReadSystemFont("webdings.ttf"));

        Assert.Null(result.Problem);
        Assert.True(result.Font!.TryGetGlyphId(0xF041, out var gidPua));
        Assert.True(result.Font.TryGetGlyphId(0x41, out var gidBare));
        Assert.Equal(gidPua, gidBare);
    }

    [Fact]
    public void GetGlyphData_returns_empty_for_a_glyph_with_no_outline()
    {
        var font = TrueTypeFont.Parse(ReadSystemFont("arial.ttf")).Font!;
        Assert.True(font.TryGetGlyphId(' ', out var spaceGid));
        Assert.Empty(font.GetGlyphData(spaceGid));
    }

    [Fact]
    public void Ascent_and_descent_are_populated_from_hhea()
    {
        var font = TrueTypeFont.Parse(ReadSystemFont("arial.ttf")).Font!;
        Assert.True(font.Ascent > 0);
        Assert.True(font.Descent < 0);
    }
}
