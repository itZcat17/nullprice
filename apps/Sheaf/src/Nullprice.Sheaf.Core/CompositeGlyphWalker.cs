namespace Nullprice.Sheaf.Core;

/// <summary>One component record within a composite glyph (ISO/IEC 14496-22, or equivalently
/// the OpenType <c>glyf</c> table spec's composite glyph description).
/// <see cref="GlyphIndexByteOffset"/> is the byte offset (within the glyph's own data, i.e.
/// relative to the start of the 10-byte glyph header) of this component's 2-byte glyph-index
/// field — needed both to resolve which glyph this component references (for the subsetting
/// closure) and, later, to overwrite that field in place with the glyph's new compacted ID.
/// <see cref="NextOffset"/> is the byte offset immediately after this component's own fields
/// (arguments + optional scale) — for the last component, this is where a trailing hint
/// instruction block would begin if <see cref="HasInstructionsFlag"/> is set.</summary>
public readonly record struct CompositeGlyphComponent(
    int GlyphIndex,
    int GlyphIndexByteOffset,
    int NextOffset,
    bool MoreComponents,
    bool HasInstructionsFlag);

/// <summary>Walks a composite glyph's component records without interpreting their actual
/// transform values — only enough of each record's flag bits to know how many bytes it
/// occupies and where its glyph-index field lives. This is the single highest-risk piece of
/// the font subsetting engine: getting the flag-driven size combinatorics wrong produces a
/// walk that silently reads garbage as if it were the next component, so every flag
/// combination is covered by its own test (see CompositeGlyphWalkerTests) rather than trusting
/// one or two "typical" fonts to exercise every path.</summary>
public static class CompositeGlyphWalker
{
    private const ushort ArgsAreWords = 0x0001;
    private const ushort MoreComponentsFlag = 0x0020;
    private const ushort WeHaveAScale = 0x0008;
    private const ushort WeHaveAnXAndYScale = 0x0040;
    private const ushort WeHaveATwoByTwo = 0x0080;
    private const ushort WeHaveInstructions = 0x0100;

    /// <summary><paramref name="glyphData"/> is a full glyph's raw bytes, starting at the
    /// 10-byte header (numberOfContours + bounding box) — callers must have already checked
    /// numberOfContours == -1 (composite) before calling this.</summary>
    public static List<CompositeGlyphComponent> EnumerateComponents(ReadOnlySpan<byte> glyphData)
    {
        var result = new List<CompositeGlyphComponent>();
        var offset = 10;

        while (true)
        {
            var flags = ReadUInt16BESpan(glyphData, offset);
            var glyphIndexOffset = offset + 2;
            var glyphIndex = ReadUInt16BESpan(glyphData, glyphIndexOffset);
            offset += 4;

            // Two arguments (dx/dy or point-matching indices — ARGS_ARE_XY_VALUES changes their
            // meaning, not their size, so it doesn't affect the byte walk).
            offset += (flags & ArgsAreWords) != 0 ? 4 : 2;

            // At most one of these three scale forms is present, in this priority order.
            if ((flags & WeHaveAScale) != 0) offset += 2;
            else if ((flags & WeHaveAnXAndYScale) != 0) offset += 4;
            else if ((flags & WeHaveATwoByTwo) != 0) offset += 8;

            var moreComponents = (flags & MoreComponentsFlag) != 0;
            var hasInstructions = (flags & WeHaveInstructions) != 0;

            result.Add(new CompositeGlyphComponent(glyphIndex, glyphIndexOffset, offset, moreComponents, hasInstructions));

            if (!moreComponents) return result;
        }
    }

    private static ushort ReadUInt16BESpan(ReadOnlySpan<byte> bytes, int offset) => (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
}
