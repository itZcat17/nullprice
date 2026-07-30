namespace Nullprice.Ferry.Core;

/// <summary>
/// Turns "these files and folders, into that folder" into an explicit list of files.
/// <para>
/// Resolving the whole plan up front means the total is known before a single byte moves,
/// so progress is honest rather than a bar that keeps discovering more work.
/// </para>
/// </summary>
public static class PlanBuilder
{
    public static CopyPlan Build(
        IEnumerable<string> sources,
        string destinationRoot,
        ConflictPolicy policy = ConflictPolicy.Overwrite,
        bool verify = true)
    {
        var items = new List<CopyItem>();

        foreach (var source in sources)
        {
            if (File.Exists(source))
            {
                var info = new FileInfo(source);
                items.Add(new CopyItem(
                    info.FullName,
                    Path.Combine(destinationRoot, info.Name),
                    info.Length));
            }
            else if (Directory.Exists(source))
            {
                AddDirectory(new DirectoryInfo(source), destinationRoot, items);
            }
            else
            {
                throw new FileNotFoundException($"No such file or folder: {source}", source);
            }
        }

        return new CopyPlan(items, policy, verify);
    }

    /// <summary>Recurses a folder, preserving its structure under the destination.</summary>
    private static void AddDirectory(DirectoryInfo directory, string destinationRoot, List<CopyItem> items)
    {
        // The folder itself becomes a folder at the destination, so its name is part of the path.
        var target = Path.Combine(destinationRoot, directory.Name);

        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory.FullName, file.FullName);
            items.Add(new CopyItem(
                file.FullName,
                Path.Combine(target, relative),
                file.Length));
        }
    }

    /// <summary>
    /// Human-readable byte count, for progress text and the final report.
    /// <para>
    /// Formats in the caller's culture by default, so a reader who writes decimals with a
    /// comma sees "1,5 KB". Pass <see cref="System.Globalization.CultureInfo.InvariantCulture"/>
    /// for log files and anything else that has to be stable across machines.
    /// </para>
    /// </summary>
    public static string FormatBytes(long bytes, IFormatProvider? culture = null)
    {
        culture ??= System.Globalization.CultureInfo.CurrentCulture;

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Format(culture, "{0} B", bytes)
            : string.Format(culture, "{0:0.##} {1}", value, units[unit]);
    }
}
