namespace Nullprice.Sheaf.Core.Tests;

/// <summary>Covers every component-record flag combination the composite-glyph spec defines,
/// since a single byte-count mistake in any one of them silently misreads every following
/// component as garbage rather than throwing — see <see cref="CompositeGlyphWalker"/>'s own
/// doc comment for why this file exists on its own rather than being folded into a
/// font-level test.</summary>
public class CompositeGlyphWalkerTests
{
    private static byte[] Header() => new byte[10]; // numberOfContours + bbox — contents irrelevant to the walker

    private static void WriteUInt16BE(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static byte[] BuildComponent(ushort flags, ushort glyphIndex, bool argsAreWords, int scaleBytes)
    {
        var bytes = new List<byte>(Header());
        WriteUInt16BE(bytes, flags);
        WriteUInt16BE(bytes, glyphIndex);
        bytes.AddRange(new byte[argsAreWords ? 4 : 2]);
        bytes.AddRange(new byte[scaleBytes]);
        return bytes.ToArray();
    }

    [Fact]
    public void Minimal_component_with_byte_args_and_no_scale()
    {
        var data = BuildComponent(flags: 0x0000, glyphIndex: 5, argsAreWords: false, scaleBytes: 0);
        var c = Assert.Single(CompositeGlyphWalker.EnumerateComponents(data));

        Assert.Equal(5, c.GlyphIndex);
        Assert.Equal(12, c.GlyphIndexByteOffset); // 10 header + 2 flags
        Assert.Equal(16, c.NextOffset); // 12 + 2 glyphIndex + 2 args
        Assert.False(c.MoreComponents);
        Assert.False(c.HasInstructionsFlag);
    }

    [Fact]
    public void Args_are_words_uses_four_bytes_instead_of_two()
    {
        var data = BuildComponent(flags: 0x0001, glyphIndex: 9, argsAreWords: true, scaleBytes: 0);
        var c = Assert.Single(CompositeGlyphWalker.EnumerateComponents(data));
        Assert.Equal(18, c.NextOffset); // 12 + 2 glyphIndex + 4 args
    }

    [Fact]
    public void We_have_a_scale_adds_two_bytes()
    {
        var data = BuildComponent(flags: 0x0008, glyphIndex: 3, argsAreWords: false, scaleBytes: 2);
        var c = Assert.Single(CompositeGlyphWalker.EnumerateComponents(data));
        Assert.Equal(18, c.NextOffset); // 12 + 2 args + 2 scale
    }

    [Fact]
    public void We_have_an_x_and_y_scale_adds_four_bytes()
    {
        var data = BuildComponent(flags: 0x0040, glyphIndex: 3, argsAreWords: false, scaleBytes: 4);
        var c = Assert.Single(CompositeGlyphWalker.EnumerateComponents(data));
        Assert.Equal(20, c.NextOffset); // 12 + 2 args + 4 scale
    }

    [Fact]
    public void We_have_a_two_by_two_adds_eight_bytes()
    {
        var data = BuildComponent(flags: 0x0080, glyphIndex: 3, argsAreWords: false, scaleBytes: 8);
        var c = Assert.Single(CompositeGlyphWalker.EnumerateComponents(data));
        Assert.Equal(24, c.NextOffset); // 12 + 2 args + 8 matrix
    }

    [Fact]
    public void Word_args_combined_with_a_two_by_two_matrix()
    {
        var data = BuildComponent(flags: (ushort)(0x0001 | 0x0080), glyphIndex: 3, argsAreWords: true, scaleBytes: 8);
        var c = Assert.Single(CompositeGlyphWalker.EnumerateComponents(data));
        Assert.Equal(26, c.NextOffset); // 12 + 4 args + 8 matrix
    }

    [Fact]
    public void More_components_flag_continues_the_walk_to_a_second_record()
    {
        var first = BuildComponent(flags: 0x0020, glyphIndex: 1, argsAreWords: false, scaleBytes: 0); // MORE_COMPONENTS
        var second = BuildComponent(flags: 0x0000, glyphIndex: 2, argsAreWords: false, scaleBytes: 0);
        var data = first.Concat(second.Skip(10)).ToArray(); // second's own synthetic 10-byte header is discarded

        var components = CompositeGlyphWalker.EnumerateComponents(data);

        Assert.Equal(2, components.Count);
        Assert.Equal(1, components[0].GlyphIndex);
        Assert.True(components[0].MoreComponents);
        Assert.Equal(2, components[1].GlyphIndex);
        Assert.False(components[1].MoreComponents);
        Assert.Equal(components[0].NextOffset + 6, components[1].NextOffset);
    }

    [Fact]
    public void Last_components_instructions_flag_is_reported()
    {
        var data = BuildComponent(flags: 0x0100, glyphIndex: 7, argsAreWords: false, scaleBytes: 0); // WE_HAVE_INSTRUCTIONS
        var c = Assert.Single(CompositeGlyphWalker.EnumerateComponents(data));
        Assert.True(c.HasInstructionsFlag);
        Assert.False(c.MoreComponents);
    }

    [Fact]
    public void Each_component_reports_its_own_instructions_flag_independently()
    {
        // Per spec, only the *last* component's flag actually means anything (a trailing
        // instruction block can only follow the final component) — that interpretation is the
        // subsetter's job (see TrueTypeSubsetter), not the walker's; the walker just reports
        // exactly what each record's flags say.
        var first = BuildComponent(flags: (ushort)(0x0020 | 0x0100), glyphIndex: 1, argsAreWords: false, scaleBytes: 0);
        var second = BuildComponent(flags: 0x0000, glyphIndex: 2, argsAreWords: false, scaleBytes: 0);
        var data = first.Concat(second.Skip(10)).ToArray();

        var components = CompositeGlyphWalker.EnumerateComponents(data);
        Assert.True(components[0].HasInstructionsFlag);
        Assert.False(components[1].HasInstructionsFlag);
    }
}
