namespace Nullprice.Sheaf.Core;

/// <summary>
/// WinAnsiEncoding (ISO 32000-1 Appendix D) — the default encoding for non-symbolic
/// TrueType fonts, and Windows-1252 in every range that matters here: codes 0x20-0x7E and
/// 0xA0-0xFF map directly to the identical Unicode codepoint, and 0x80-0x9F carry the
/// smart-punctuation/currency substitutions hand-listed below. Undefined codes (0x00-0x1F,
/// 0x7F, and the unlisted gaps in 0x80-0x9F) resolve to <see langword="null"/> rather than a
/// guess.
/// </summary>
internal static class WinAnsiEncoding
{
    private static readonly Dictionary<int, int> HighRangeOverrides = new()
    {
        [0x80] = 0x20AC, [0x82] = 0x201A, [0x83] = 0x0192, [0x84] = 0x201E,
        [0x85] = 0x2026, [0x86] = 0x2020, [0x87] = 0x2021, [0x88] = 0x02C6,
        [0x89] = 0x2030, [0x8A] = 0x0160, [0x8B] = 0x2039, [0x8C] = 0x0152,
        [0x8E] = 0x017D,
        [0x91] = 0x2018, [0x92] = 0x2019, [0x93] = 0x201C, [0x94] = 0x201D,
        [0x95] = 0x2022, [0x96] = 0x2013, [0x97] = 0x2014, [0x98] = 0x02DC,
        [0x99] = 0x2122, [0x9A] = 0x0161, [0x9B] = 0x203A, [0x9C] = 0x0153,
        [0x9E] = 0x017E, [0x9F] = 0x0178,
    };

    public static int? ToUnicode(int code)
    {
        if (code is >= 0x20 and <= 0x7E) return code;
        if (code is >= 0xA0 and <= 0xFF) return code;
        return HighRangeOverrides.TryGetValue(code, out var u) ? u : null;
    }
}
