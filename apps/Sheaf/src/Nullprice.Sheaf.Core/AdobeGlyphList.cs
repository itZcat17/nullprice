namespace Nullprice.Sheaf.Core;

/// <summary>
/// Maps a PDF glyph name (as used in <c>/Differences</c> arrays) to Unicode. A deliberately
/// practical subset of the full Adobe Glyph List (~4300 names) rather than an exhaustive
/// transcription: single-letter names resolve directly (<c>"A"</c> → U+0041), the standard
/// <c>uniXXXX</c> pattern is handled generically, and the table below covers what real-world
/// <c>/Differences</c> arrays overwhelmingly actually contain — smart punctuation, currency,
/// and accented Latin. A name outside this set resolves to <see langword="null"/> rather than
/// a guess; the character it names is then treated as unavailable for editing, same as a
/// genuinely un-embedded glyph.
/// </summary>
internal static class AdobeGlyphList
{
    private static readonly Dictionary<string, int> Names = new()
    {
        ["space"] = 0x0020, ["exclam"] = 0x0021, ["quotedbl"] = 0x0022, ["numbersign"] = 0x0023,
        ["dollar"] = 0x0024, ["percent"] = 0x0025, ["ampersand"] = 0x0026, ["quotesingle"] = 0x0027,
        ["parenleft"] = 0x0028, ["parenright"] = 0x0029, ["asterisk"] = 0x002A, ["plus"] = 0x002B,
        ["comma"] = 0x002C, ["hyphen"] = 0x002D, ["period"] = 0x002E, ["slash"] = 0x002F,
        ["zero"] = 0x0030, ["one"] = 0x0031, ["two"] = 0x0032, ["three"] = 0x0033, ["four"] = 0x0034,
        ["five"] = 0x0035, ["six"] = 0x0036, ["seven"] = 0x0037, ["eight"] = 0x0038, ["nine"] = 0x0039,
        ["colon"] = 0x003A, ["semicolon"] = 0x003B, ["less"] = 0x003C, ["equal"] = 0x003D,
        ["greater"] = 0x003E, ["question"] = 0x003F, ["at"] = 0x0040,
        ["bracketleft"] = 0x005B, ["backslash"] = 0x005C, ["bracketright"] = 0x005D,
        ["asciicircum"] = 0x005E, ["underscore"] = 0x005F, ["grave"] = 0x0060,
        ["braceleft"] = 0x007B, ["bar"] = 0x007C, ["braceright"] = 0x007D, ["asciitilde"] = 0x007E,

        // Smart punctuation and currency — the overwhelming majority of real /Differences use.
        ["quoteleft"] = 0x2018, ["quoteright"] = 0x2019, ["quotesinglbase"] = 0x201A,
        ["quotedblleft"] = 0x201C, ["quotedblright"] = 0x201D, ["quotedblbase"] = 0x201E,
        ["endash"] = 0x2013, ["emdash"] = 0x2014, ["ellipsis"] = 0x2026, ["bullet"] = 0x2022,
        ["dagger"] = 0x2020, ["daggerdbl"] = 0x2021, ["perthousand"] = 0x2030,
        ["trademark"] = 0x2122, ["Euro"] = 0x20AC, ["florin"] = 0x0192,
        ["guilsinglleft"] = 0x2039, ["guilsinglright"] = 0x203A,
        ["circumflex"] = 0x02C6, ["tilde"] = 0x02DC,
        ["OE"] = 0x0152, ["oe"] = 0x0153, ["Scaron"] = 0x0160, ["scaron"] = 0x0161,
        ["Zcaron"] = 0x017D, ["zcaron"] = 0x017E, ["Ydieresis"] = 0x0178,

        // Accented Latin.
        ["Agrave"] = 0x00C0, ["Aacute"] = 0x00C1, ["Acircumflex"] = 0x00C2, ["Atilde"] = 0x00C3,
        ["Adieresis"] = 0x00C4, ["Aring"] = 0x00C5, ["AE"] = 0x00C6, ["Ccedilla"] = 0x00C7,
        ["Egrave"] = 0x00C8, ["Eacute"] = 0x00C9, ["Ecircumflex"] = 0x00CA, ["Edieresis"] = 0x00CB,
        ["Igrave"] = 0x00CC, ["Iacute"] = 0x00CD, ["Icircumflex"] = 0x00CE, ["Idieresis"] = 0x00CF,
        ["Eth"] = 0x00D0, ["Ntilde"] = 0x00D1, ["Ograve"] = 0x00D2, ["Oacute"] = 0x00D3,
        ["Ocircumflex"] = 0x00D4, ["Otilde"] = 0x00D5, ["Odieresis"] = 0x00D6, ["Oslash"] = 0x00D8,
        ["Ugrave"] = 0x00D9, ["Uacute"] = 0x00DA, ["Ucircumflex"] = 0x00DB, ["Udieresis"] = 0x00DC,
        ["Yacute"] = 0x00DD, ["Thorn"] = 0x00DE, ["germandbls"] = 0x00DF,
        ["agrave"] = 0x00E0, ["aacute"] = 0x00E1, ["acircumflex"] = 0x00E2, ["atilde"] = 0x00E3,
        ["adieresis"] = 0x00E4, ["aring"] = 0x00E5, ["ae"] = 0x00E6, ["ccedilla"] = 0x00E7,
        ["egrave"] = 0x00E8, ["eacute"] = 0x00E9, ["ecircumflex"] = 0x00EA, ["edieresis"] = 0x00EB,
        ["igrave"] = 0x00EC, ["iacute"] = 0x00ED, ["icircumflex"] = 0x00EE, ["idieresis"] = 0x00EF,
        ["eth"] = 0x00F0, ["ntilde"] = 0x00F1, ["ograve"] = 0x00F2, ["oacute"] = 0x00F3,
        ["ocircumflex"] = 0x00F4, ["otilde"] = 0x00F5, ["odieresis"] = 0x00F6, ["oslash"] = 0x00F8,
        ["ugrave"] = 0x00F9, ["uacute"] = 0x00FA, ["ucircumflex"] = 0x00FB, ["udieresis"] = 0x00FC,
        ["yacute"] = 0x00FD, ["thorn"] = 0x00FE, ["ydieresis"] = 0x00FF,

        // Latin-1 punctuation/symbols.
        ["exclamdown"] = 0x00A1, ["cent"] = 0x00A2, ["sterling"] = 0x00A3, ["currency"] = 0x00A4,
        ["yen"] = 0x00A5, ["brokenbar"] = 0x00A6, ["section"] = 0x00A7, ["dieresis"] = 0x00A8,
        ["copyright"] = 0x00A9, ["ordfeminine"] = 0x00AA, ["guillemotleft"] = 0x00AB,
        ["logicalnot"] = 0x00AC, ["registered"] = 0x00AE, ["macron"] = 0x00AF, ["degree"] = 0x00B0,
        ["plusminus"] = 0x00B1, ["acute"] = 0x00B4, ["mu"] = 0x00B5, ["paragraph"] = 0x00B6,
        ["periodcentered"] = 0x00B7, ["cedilla"] = 0x00B8, ["ordmasculine"] = 0x00BA,
        ["guillemotright"] = 0x00BB, ["questiondown"] = 0x00BF, ["multiply"] = 0x00D7, ["divide"] = 0x00F7,
    };

    public static int? ToUnicode(string glyphName)
    {
        if (Names.TryGetValue(glyphName, out var mapped)) return mapped;

        if (glyphName.Length == 1 && char.IsAsciiLetterOrDigit(glyphName[0])) return glyphName[0];

        if (glyphName.StartsWith("uni", StringComparison.Ordinal) && glyphName.Length == 7 &&
            int.TryParse(glyphName.AsSpan(3, 4), System.Globalization.NumberStyles.HexNumber, null, out var hex))
        {
            return hex;
        }

        return null;
    }
}
