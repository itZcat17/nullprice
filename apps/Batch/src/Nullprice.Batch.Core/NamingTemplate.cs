using System.Globalization;
using System.Text;

namespace Nullprice.Batch.Core;

/// <summary>Everything a template can substitute for one image.</summary>
public sealed record NamingContext(
    string OriginalName,
    int Index,
    int Width,
    int Height,
    DateTime Taken);

/// <summary>
/// Output filename templates. Deliberately a tiny language rather than a regex or a
/// format string: people type these by hand, so an unknown token has to survive rather
/// than throw, and a typo must never silently collapse two files onto one name.
/// <para>
/// Tokens: <c>{name}</c> <c>{n}</c> <c>{n:000}</c> <c>{width}</c> <c>{height}</c>
/// <c>{date}</c> <c>{date:yyyy-MM-dd}</c>
/// </para>
/// </summary>
public static class NamingTemplate
{
    public const string Default = "{name}";

    /// <summary>Characters Windows forbids in a filename, replaced rather than rejected.</summary>
    private static readonly char[] Illegal = Path.GetInvalidFileNameChars();

    public static string Apply(string template, NamingContext context)
    {
        if (string.IsNullOrWhiteSpace(template)) template = Default;

        var result = new StringBuilder(template.Length + 16);
        var i = 0;

        while (i < template.Length)
        {
            if (template[i] != '{')
            {
                result.Append(template[i]);
                i++;
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                // Unclosed brace: treat the rest as literal text rather than failing.
                result.Append(template[i..]);
                break;
            }

            var token = template[(i + 1)..close];
            result.Append(Expand(token, context));
            i = close + 1;
        }

        return Sanitise(result.ToString());
    }

    private static string Expand(string token, NamingContext context)
    {
        var colon = token.IndexOf(':');
        var name = (colon < 0 ? token : token[..colon]).Trim().ToLowerInvariant();
        var format = colon < 0 ? null : token[(colon + 1)..];

        return name switch
        {
            "name" => context.OriginalName,

            "n" => string.IsNullOrEmpty(format)
                ? context.Index.ToString(CultureInfo.InvariantCulture)
                : SafeNumber(context.Index, format),

            "width" or "w" => context.Width.ToString(CultureInfo.InvariantCulture),
            "height" or "h" => context.Height.ToString(CultureInfo.InvariantCulture),

            "date" => context.Taken.ToString(
                string.IsNullOrEmpty(format) ? "yyyy-MM-dd" : format,
                CultureInfo.InvariantCulture),

            // Unknown tokens are left visible so the mistake is obvious in the preview
            // rather than silently producing identical names for every file.
            _ => "{" + token + "}",
        };
    }

    private static string SafeNumber(int value, string format)
    {
        try
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string Sanitise(string name)
    {
        var cleaned = new StringBuilder(name.Length);
        foreach (var c in name)
            cleaned.Append(Array.IndexOf(Illegal, c) >= 0 ? '_' : c);

        // Windows also refuses trailing dots and spaces.
        var trimmed = cleaned.ToString().TrimEnd(' ', '.');
        return trimmed.Length == 0 ? "untitled" : trimmed;
    }
}
