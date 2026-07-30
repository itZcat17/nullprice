using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Nullprice.Carry.Core;

[SupportedOSPlatform("windows")]
public sealed class ReviewService
{
    public Task<long> CalculateFolderSizeAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => DirectorySize(path, cancellationToken), cancellationToken);

    public Task<long> CalculateAppDataSizeAsync(
        AppPackage package,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => CalculateDataSize(
            package.DataFolders ?? [], new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
            cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<AppPackage>> DiscoverReinstallableAppsAsync(
        CancellationToken cancellationToken = default)
    {
        var exportPath = Path.Combine(Path.GetTempPath(), $"carry-apps-{Guid.NewGuid():N}.json");
        try
        {
            var result = await RunAsync("winget.exe",
                $"export --output \"{exportPath}\" --include-versions --accept-source-agreements --disable-interactivity",
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(exportPath))
                throw new InvalidOperationException(
                    "Windows Package Manager could not list reinstallable apps. " + result.Error.Trim());

            using var document = JsonDocument.Parse(
                await File.ReadAllTextAsync(exportPath, cancellationToken).ConfigureAwait(false));
            var registryApps = ReadInstalledApps();
            var packages = new List<AppPackage>();
            foreach (var source in document.RootElement.GetProperty("Sources").EnumerateArray())
            foreach (var package in source.GetProperty("Packages").EnumerateArray())
            {
                var id = package.GetProperty("PackageIdentifier").GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                var version = package.TryGetProperty("Version", out var v) ? v.GetString() : null;
                var match = FindRegistryMatch(id, registryApps);
                var displayName = match?.DisplayName ?? PrettifyIdentifier(id);
                var lastUsed = match is null ? null : FindLastLaunch(match);
                var dataFolders = FindAppDataFolders(id, displayName);
                packages.Add(new(id, displayName, version, lastUsed,
                    lastUsed is null ? "No reliable launch history found" : "Estimated from Windows launch history",
                    dataFolders));
            }
            var matchedNames = packages.Select(x => Normalize(x.DisplayName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var registryApp in registryApps
                         .Where(x => !matchedNames.Contains(Normalize(x.DisplayName)))
                         .DistinctBy(x => Normalize(x.DisplayName)))
            {
                var id = "unlisted:" + registryApp.DisplayName;
                var lastUsed = FindLastLaunch(registryApp);
                var dataFolders = FindAppDataFolders(id, registryApp.DisplayName);
                packages.Add(new(id, registryApp.DisplayName, null, lastUsed,
                    lastUsed is null ? "No reliable launch history found" : "Estimated from Windows launch history",
                    dataFolders, CanReinstall: false));
            }

            return packages
                .DistinctBy(x => x.Identifier, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            try { if (File.Exists(exportPath)) File.Delete(exportPath); } catch { }
        }
    }

    public Task<FileReviewResult> FindInactiveFilesAsync(
        string root,
        int months,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var cutoff = DateTimeOffset.Now.AddMonths(-months);
            var found = new List<InactiveFile>();
            long inactiveFileCount = 0;
            long totalFiles = 0;
            long totalBytes = 0;
            var progressTimer = Stopwatch.StartNew();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                if (progressTimer.ElapsedMilliseconds >= 250)
                {
                    progress?.Report(directory);
                    progressTimer.Restart();
                }
                try
                {
                    foreach (var path in Directory.GetFiles(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var info = new FileInfo(path);
                            totalFiles++;
                            totalBytes += info.Length;
                            var activity = new DateTimeOffset(
                                info.LastAccessTimeUtc > info.LastWriteTimeUtc
                                    ? info.LastAccessTimeUtc : info.LastWriteTimeUtc, TimeSpan.Zero);
                            if (activity < cutoff)
                            {
                                inactiveFileCount++;
                                if (found.Count < 10_000)
                                    found.Add(new(path, Path.GetRelativePath(root, path), info.Length, activity));
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    foreach (var child in Directory.GetDirectories(directory))
                    {
                        try
                        {
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                                pending.Push(child);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return new FileReviewResult(
                found.OrderBy(x => x.LastActivity).ToArray(),
                inactiveFileCount, totalFiles, totalBytes);
        }, cancellationToken);
    }

    private static IReadOnlyList<RegistryApp> ReadInstalledApps()
    {
        var result = new List<RegistryApp>();
        var locations = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64)
        };
        foreach (var (hive, view) in locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    var displayName = key?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;
                    result.Add(new(displayName,
                        key?.GetValue("DisplayIcon") as string,
                        key?.GetValue("InstallLocation") as string));
                }
            }
            catch { }
        }
        return result;
    }

    private static RegistryApp? FindRegistryMatch(string id, IReadOnlyList<RegistryApp> apps)
    {
        var normalizedId = Normalize(id);
        return apps
            .Select(app => (App: app, Name: Normalize(app.DisplayName)))
            .Where(x => x.Name.Length >= 4 &&
                (normalizedId.Contains(x.Name, StringComparison.OrdinalIgnoreCase) ||
                 x.Name.Contains(normalizedId, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(x => x.Name.Length)
            .Select(x => x.App)
            .FirstOrDefault();
    }

    private static DateTimeOffset? FindLastLaunch(RegistryApp app)
    {
        var executable = ResolveExecutable(app);
        if (executable is null) return null;
        try
        {
            var prefetch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
            var stem = Path.GetFileNameWithoutExtension(executable);
            var latest = Directory.EnumerateFiles(prefetch, $"{stem}-*.pf")
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty()
                .Max();
            return latest == default ? null : new DateTimeOffset(latest, TimeSpan.Zero);
        }
        catch { return null; }
    }

    private static string? ResolveExecutable(RegistryApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.DisplayIcon))
        {
            var candidate = app.DisplayIcon.Trim().Trim('"').Split(',')[0].Trim('"');
            if (File.Exists(candidate) && candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        if (!string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation))
        {
            try
            {
                return Directory.EnumerateFiles(app.InstallLocation, "*.exe", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(x => !Path.GetFileName(x).Contains("unins", StringComparison.OrdinalIgnoreCase));
            }
            catch { }
        }
        return null;
    }

    private static string PrettifyIdentifier(string id)
    {
        var name = id.Split('.').LastOrDefault() ?? id;
        return Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IReadOnlyList<AppDataFolder> FindAppDataFolders(string id, string displayName)
    {
        var found = new Dictionary<string, AppDataFolder>(StringComparer.OrdinalIgnoreCase);
        var roots = AppDataRoots();
        foreach (var known in KnownDataFolders(id))
            AddIfPresent(known, roots, found);

        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft", "google", "windows", "desktop", "application", "app", "software",
            "corporation", "inc", "llc", "official", "client", "installer", "package"
        };
        var tokens = Regex.Split(id + " " + displayName, @"[^A-Za-z0-9]+")
            .Select(Normalize)
            .Where(x => x.Length >= 4 && !ignored.Contains(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Length)
            .ToArray();

        foreach (var (scope, root) in roots.Where(x => x.Scope != "UserProfile"))
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var first in Directory.GetDirectories(root))
                {
                    var firstName = Normalize(Path.GetFileName(first));
                    if (tokens.Any(t => firstName == t || (t.Length >= 6 && firstName.Contains(t))))
                        AddFolder(scope, root, first, found);
                    try
                    {
                        foreach (var second in Directory.GetDirectories(first))
                        {
                            var combined = Normalize(Path.GetRelativePath(root, second));
                            if (tokens.Any(t => combined.EndsWith(t, StringComparison.OrdinalIgnoreCase) ||
                                                (t.Length >= 6 && combined.Contains(t))))
                                AddFolder(scope, root, second, found);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return found.Values.OrderBy(x => x.Scope).ThenBy(x => x.RelativePath).ToArray();
    }

    private static IReadOnlyList<(string Scope, string Root)> AppDataRoots() =>
    [
        ("Roaming", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
        ("Local", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        ("LocalLow", Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow")),
        ("UserProfile", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    ];

    private static IEnumerable<AppDataFolder> KnownDataFolders(string id)
    {
        var map = new Dictionary<string, AppDataFolder[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Google.Chrome"] = [new("Local", @"Google\Chrome")],
            ["Mozilla.Firefox"] = [new("Roaming", @"Mozilla\Firefox"), new("Local", @"Mozilla\Firefox")],
            ["Microsoft.Edge"] = [new("Local", @"Microsoft\Edge")],
            ["Microsoft.VisualStudioCode"] = [new("Roaming", "Code"), new("UserProfile", ".vscode")],
            ["Discord.Discord"] = [new("Roaming", "discord"), new("Local", "Discord")],
            ["SlackTechnologies.Slack"] = [new("Roaming", "Slack"), new("Local", "slack")],
            ["Notion.Notion"] = [new("Roaming", "Notion"), new("Local", "Notion")],
            ["Spotify.Spotify"] = [new("Roaming", "Spotify"), new("Local", "Spotify")],
            ["Brave.Brave"] = [new("Local", @"BraveSoftware\Brave-Browser")],
            ["Opera.Opera"] = [new("Roaming", "Opera Software"), new("Local", "Opera Software")],
            ["Valve.Steam"] = [new("Local", "Steam")],
            ["Mojang.MinecraftLauncher"] = [new("UserProfile", ".minecraft")]
        };
        return map.TryGetValue(id, out var folders) ? folders : [];
    }

    private static void AddIfPresent(
        AppDataFolder folder,
        IReadOnlyList<(string Scope, string Root)> roots,
        IDictionary<string, AppDataFolder> found)
    {
        var root = roots.FirstOrDefault(x => x.Scope == folder.Scope).Root;
        if (!string.IsNullOrEmpty(root) && Directory.Exists(Path.Combine(root, folder.RelativePath)))
            found[$"{folder.Scope}|{folder.RelativePath}"] = folder;
    }

    private static void AddFolder(
        string scope,
        string root,
        string path,
        IDictionary<string, AppDataFolder> found)
    {
        var relative = Path.GetRelativePath(root, path);
        var candidate = new AppDataFolder(scope, relative);
        if (found.Values.Any(existing =>
            existing.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase) &&
            (relative.Equals(existing.RelativePath, StringComparison.OrdinalIgnoreCase) ||
             relative.StartsWith(existing.RelativePath + Path.DirectorySeparatorChar,
                 StringComparison.OrdinalIgnoreCase))))
            return;
        foreach (var child in found.Where(pair =>
                     pair.Value.Scope.Equals(scope, StringComparison.OrdinalIgnoreCase) &&
                     pair.Value.RelativePath.StartsWith(relative + Path.DirectorySeparatorChar,
                         StringComparison.OrdinalIgnoreCase))
                 .Select(pair => pair.Key).ToArray())
            found.Remove(child);
        found[$"{scope}|{relative}"] = candidate;
    }

    private static long CalculateDataSize(
        IReadOnlyList<AppDataFolder> folders,
        IDictionary<string, long> cache,
        CancellationToken cancellationToken)
    {
        long total = 0;
        var roots = AppDataRoots();
        foreach (var folder in folders)
        {
            var root = roots.FirstOrDefault(x =>
                x.Scope.Equals(folder.Scope, StringComparison.OrdinalIgnoreCase)).Root;
            if (string.IsNullOrEmpty(root)) continue;
            var path = Path.Combine(root, folder.RelativePath);
            if (!cache.TryGetValue(path, out var size))
            {
                size = DirectorySize(path, cancellationToken);
                cache[path] = size;
            }
            total += size;
        }
        return total;
    }

    private static long DirectorySize(string root, CancellationToken cancellationToken)
    {
        long total = 0;
        var pending = new Stack<string>();
        if (Directory.Exists(root)) pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.GetFiles(directory))
                {
                    try { total += new FileInfo(file).Length; } catch { }
                }
                foreach (var child in Directory.GetDirectories(directory))
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            pending.Push(child);
                    }
                    catch { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string fileName, string arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} could not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }

    private sealed record RegistryApp(string DisplayName, string? DisplayIcon, string? InstallLocation);
}
