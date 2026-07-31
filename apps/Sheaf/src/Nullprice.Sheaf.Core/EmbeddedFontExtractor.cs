namespace Nullprice.Sheaf.Core;

/// <summary>The embedded TrueType program bytes for a simple font, plus a bidirectional
/// character-code &lt;-&gt; Unicode mapping (built from <c>/Encoding</c> +
/// <c>/Differences</c>) so new text can be re-encoded into codes the font actually
/// understands, and each code's declared advance width (already 1000-units/em per spec).</summary>
public sealed record ExtractedFont(
    byte[] FontFileBytes,
    IReadOnlyDictionary<int, int> CodeToUnicode,
    IReadOnlyDictionary<int, int> UnicodeToCode,
    IReadOnlyDictionary<int, double> CodeToWidthEm);

/// <summary>
/// Reads what's needed to edit text drawn with a PDF simple font. Scope boundary, refused
/// (returns null) rather than mishandled: composite (Type0/CID) fonts, fonts without an
/// embedded <c>/FontFile2</c> (Type1/CFF and non-embedded standard-14 fonts — Windows'
/// substitutes for Helvetica/Times aren't metrically identical, so editing them without the
/// real font program risks visibly shifting text), and any character code whose glyph name
/// isn't in <see cref="AdobeGlyphList"/>'s practical subset.
/// </summary>
public static class EmbeddedFontExtractor
{
    /// <summary>Looks up a page's own <c>/Resources/Font</c> entry by name (e.g. "F1") — the
    /// same lookup both <see cref="SheafRunner"/> (applying a saved <see cref="TextEdit"/>)
    /// and the interactive App-side editor (resolving what font a click landed on) need.</summary>
    public static PdfDictionary? ResolveFontResource(PdfObjectTable objects, PdfDictionary pageDict, string fontResourceName)
    {
        var resources = objects.Resolve(pageDict.Get("Resources")) as PdfDictionary;
        var fonts = objects.Resolve(resources?.Get("Font")) as PdfDictionary;
        return objects.Resolve(fonts?.Get(fontResourceName)) as PdfDictionary;
    }

    public static ExtractedFont? Extract(PdfObjectTable objects, PdfDictionary fontDict)
    {
        if ((fontDict.Get("Subtype") as PdfName)?.Value != "TrueType") return null;

        var descriptor = objects.Resolve(fontDict.Get("FontDescriptor")) as PdfDictionary;
        if (objects.Resolve(descriptor?.Get("FontFile2")) is not PdfStream fontFileStream) return null;

        var firstChar = (fontDict.Get("FirstChar") as PdfNumber)?.AsInt;
        var widthsArray = objects.Resolve(fontDict.Get("Widths")) as PdfArray;
        if (firstChar is null || widthsArray is null) return null;

        var fontBytes = FilterCodec.Decode(fontFileStream.Dictionary, fontFileStream.RawBytes, objects);
        var differencesNames = ReadDifferences(objects, fontDict);

        var codeToUnicode = new Dictionary<int, int>();
        var unicodeToCode = new Dictionary<int, int>();
        var codeToWidthEm = new Dictionary<int, double>();

        for (var i = 0; i < widthsArray.Items.Count; i++)
        {
            var code = firstChar.Value + i;
            var width = (objects.Resolve(widthsArray.Items[i]) as PdfNumber)?.Value ?? 0;
            codeToWidthEm[code] = width / 1000.0;

            var unicode = differencesNames.TryGetValue(code, out var name)
                ? AdobeGlyphList.ToUnicode(name)
                : WinAnsiEncoding.ToUnicode(code);

            if (unicode is null) continue; // no known character for this code — skip rather than guess

            codeToUnicode[code] = unicode.Value;
            unicodeToCode.TryAdd(unicode.Value, code); // first code wins if more than one maps to the same character
        }

        if (codeToUnicode.Count == 0) return null;

        return new ExtractedFont(fontBytes, codeToUnicode, unicodeToCode, codeToWidthEm);
    }

    private static Dictionary<int, string> ReadDifferences(PdfObjectTable objects, PdfDictionary fontDict)
    {
        var result = new Dictionary<int, string>();
        if (objects.Resolve(fontDict.Get("Encoding")) is not PdfDictionary encodingDict) return result;
        if (objects.Resolve(encodingDict.Get("Differences")) is not PdfArray differences) return result;

        var currentCode = 0;
        foreach (var item in differences.Items)
        {
            switch (objects.Resolve(item))
            {
                case PdfNumber n: currentCode = n.AsInt; break;
                case PdfName name: result[currentCode] = name.Value; currentCode++; break;
            }
        }

        return result;
    }
}
