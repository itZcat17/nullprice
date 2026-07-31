using System.IO;
using System.Windows.Media;
using Nullprice.Sheaf.Core;

namespace Nullprice.Sheaf.App;

/// <summary>Checks whether a specific embedded font program actually has a glyph for a given
/// character, using WPF's <see cref="GlyphTypeface"/> — the OS text-shaping stack, the same
/// "let the platform do the specialist part" precedent as <see cref="WindowsDataPdfRenderer"/>
/// (rendering) and <see cref="WicRasterRecompressor"/> (image codecs).</summary>
public sealed class WpfGlyphFontLoader : IGlyphFontLoader
{
    public IGlyphFont? Load(byte[] fontProgramBytes)
    {
        // GlyphTypeface has no in-memory constructor — it only loads from a Uri — so the font
        // bytes have to touch disk. Written to a short-lived temp file and deleted immediately
        // after load, the same write-then-discard shape as the .part/.ferrypart atomic-write
        // pattern used elsewhere, just for a font instead of an output file.
        var dir = Path.Combine(Path.GetTempPath(), "sheaf-fonts");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Guid.NewGuid():n}.ttf");

        try
        {
            File.WriteAllBytes(path, fontProgramBytes);
            var glyphTypeface = new GlyphTypeface(new Uri(path));

            // GlyphTypeface's dictionary properties can be lazily populated from the backing
            // file on first access — copying them into plain dictionaries now, before the
            // temp file is deleted below, avoids depending on exactly when WPF decides to
            // actually read them.
            var characterToGlyph = new Dictionary<int, ushort>(glyphTypeface.CharacterToGlyphMap);
            var advanceWidths = new Dictionary<ushort, double>(glyphTypeface.AdvanceWidths);

            return new WpfGlyphFont(characterToGlyph, advanceWidths);
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private sealed class WpfGlyphFont(IReadOnlyDictionary<int, ushort> characterToGlyph, IReadOnlyDictionary<ushort, double> advanceWidths) : IGlyphFont
    {
        public bool HasGlyph(int unicodeCodepoint) => characterToGlyph.ContainsKey(unicodeCodepoint);

        public bool TryGetGlyph(int unicodeCodepoint, out ushort glyphIndex, out double advanceWidthEm)
        {
            if (characterToGlyph.TryGetValue(unicodeCodepoint, out var index))
            {
                glyphIndex = index;
                advanceWidthEm = advanceWidths.TryGetValue(index, out var width) ? width : 0;
                return true;
            }

            glyphIndex = 0;
            advanceWidthEm = 0;
            return false;
        }
    }
}
