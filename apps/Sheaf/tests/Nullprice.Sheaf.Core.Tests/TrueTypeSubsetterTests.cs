namespace Nullprice.Sheaf.Core.Tests;

public class TrueTypeSubsetterTests
{
    private static TrueTypeFont ParseFixture()
    {
        var result = TrueTypeFont.Parse(TrueTypeTestFixtures.Build());
        Assert.Null(result.Problem);
        return result.Font!;
    }

    [Fact]
    public void Subsetting_a_simple_glyphs_codepoint_keeps_only_notdef_and_that_glyph()
    {
        var font = ParseFixture();
        var subset = TrueTypeSubsetter.Subset(font, ['A'], "TestFont");

        var reparsed = TrueTypeFont.Parse(subset.FontBytes);
        Assert.Null(reparsed.Problem);
        Assert.Equal(2, reparsed.Font!.NumGlyphs); // .notdef + A
    }

    [Fact]
    public void Subsetting_a_composite_glyphs_codepoint_pulls_in_its_components()
    {
        var font = ParseFixture();
        var subset = TrueTypeSubsetter.Subset(font, ['C'], "TestFont");

        var reparsed = TrueTypeFont.Parse(subset.FontBytes);
        Assert.Null(reparsed.Problem);
        Assert.Equal(4, reparsed.Font!.NumGlyphs); // .notdef + C + its two components (A, B)
    }

    [Fact]
    public void Subsetting_a_cyclic_composite_reference_terminates_and_keeps_both_glyphs_in_the_cycle()
    {
        var font = ParseFixture();

        // The real assertion here is that this call returns at all: GidCycle1 references
        // GidCycle2 which references back to GidCycle1, so a closure walk without a
        // visited-set guard would recurse or loop forever.
        var subset = TrueTypeSubsetter.Subset(font, ['D'], "TestFont");

        var reparsed = TrueTypeFont.Parse(subset.FontBytes);
        Assert.Null(reparsed.Problem);
        Assert.Equal(3, reparsed.Font!.NumGlyphs); // .notdef + D + its cycle partner
    }

    [Fact]
    public void Hint_instructions_are_stripped_from_the_subsetted_glyph()
    {
        var font = ParseFixture();
        var subset = TrueTypeSubsetter.Subset(font, ['A'], "TestFont");

        var reparsed = TrueTypeFont.Parse(subset.FontBytes).Font!;
        var data = reparsed.GetGlyphData(subset.CodepointToCid['A']);

        // Original was 21 bytes (10 header + 2 endPts + 2 instrLen + 4 instructions + 3
        // trailing). Stripped content is 17 bytes, which is odd, so the glyf table pads it to
        // 18 for loca's even-offset requirement — GetGlyphData legitimately includes that
        // trailing pad byte, the same way any real TrueType parser tolerates it (a glyph's own
        // point/contour counts say exactly how much of the buffer is meaningful).
        Assert.True(data.Length is 17 or 18);
        Assert.True(data.Length < 21);

        var instructionLength = (data[12] << 8) | data[13];
        Assert.Equal(0, instructionLength);
    }

    [Fact]
    public void Codepoints_with_no_glyph_in_the_source_font_are_silently_skipped()
    {
        var font = ParseFixture();
        var subset = TrueTypeSubsetter.Subset(font, ['A', '香'], "TestFont");

        Assert.True(subset.CodepointToCid.ContainsKey('A'));
        Assert.False(subset.CodepointToCid.ContainsKey('香'));
    }

    [Fact]
    public void Advance_widths_are_carried_through_when_the_source_font_is_already_1000_units_per_em()
    {
        var font = ParseFixture(); // fixture uses unitsPerEm = 1000 and advanceWidth = 500 for every glyph
        var subset = TrueTypeSubsetter.Subset(font, ['A'], "TestFont");

        Assert.Equal(500, subset.CidToWidthPdfUnits[subset.CodepointToCid['A']]);
    }

    [Fact]
    public void ToPdfGlyphSpace_converts_a_non_1000_unitsPerEm_font_correctly()
    {
        // A glyph advancing 1024 font-units in a 2048-units/em font is exactly half an em wide,
        // which in PDF's fixed 1000-unit glyph space is 500 — independent of the fixture, this
        // is the exact bug the M8 design review caught before any code was written: skipping
        // this conversion would leave every glyph rendering roughly twice too wide.
        Assert.Equal(500, TrueTypeSubsetter.ToPdfGlyphSpace(1024, unitsPerEm: 2048));
    }

    [Fact]
    public void BaseFontName_has_a_six_letter_uppercase_subset_tag_prefix()
    {
        var font = ParseFixture();
        var subset = TrueTypeSubsetter.Subset(font, ['A'], "My Test Font");

        Assert.Matches("^[A-Z]{6}\\+MyTestFont$", subset.BaseFontName);
    }

    [Fact]
    public void Real_system_font_arial_subsets_and_reparses_cleanly()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf"));
        var font = TrueTypeFont.Parse(bytes).Font!;

        var subset = TrueTypeSubsetter.Subset(font, "Hello, Sheaf!".Select(c => (int)c).Distinct().ToList(), "Arial");

        var reparsed = TrueTypeFont.Parse(subset.FontBytes);
        Assert.Null(reparsed.Problem);
        Assert.True(reparsed.Font!.NumGlyphs >= 1);
        Assert.True(reparsed.Font.NumGlyphs < font.NumGlyphs); // an actual subset, not a full copy
    }
}
