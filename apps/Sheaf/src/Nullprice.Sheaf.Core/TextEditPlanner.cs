namespace Nullprice.Sheaf.Core;

/// <summary>
/// Checks a proposed text edit against what the font actually has, before anything is
/// written. Most embedded fonts in real PDFs are subsets containing only the glyphs used on
/// the page — there is no fallback glyph for a character that wasn't in the original text, so
/// this is the difference between a clear, actionable refusal and a silently blank glyph in
/// the output.
/// </summary>
public static class TextEditPlanner
{
    public static PlanProblem? Validate(IGlyphFont font, string newText)
    {
        var missing = newText.Where(c => !font.HasGlyph(c)).Distinct().ToList();
        if (missing.Count == 0) return null;

        var chars = string.Join(", ", missing.Select(c => $"'{c}'"));
        return new PlanProblem(
            $"This font doesn't include {chars} — PDF fonts are usually trimmed to only what's on the page.");
    }
}
