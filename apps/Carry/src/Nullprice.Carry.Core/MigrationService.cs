using System.Diagnostics;
using System.Text.Json;

namespace Nullprice.Carry.Core;

public sealed class MigrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string AppsFileName = "selected-apps.json";
    private const string ComponentsFileName = "system-components.json";
    private const string InactiveFilesName = "possibly-inactive-files.csv";

    public string DocumentsPath { get; }
    public string VsCodeUserPath { get; }
    private readonly IReadOnlyDictionary<string, string> _appDataRoots;
    private readonly bool _detectSystemComponents;

    public MigrationService(
        string? documentsPath = null,
        string? vsCodeUserPath = null,
        IReadOnlyDictionary<string, string>? appDataRoots = null,
        bool detectSystemComponents = true)
    {
        DocumentsPath = documentsPath ??
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        VsCodeUserPath = vsCodeUserPath ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User");
        _appDataRoots = appDataRoots ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Roaming"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["Local"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["LocalLow"] = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
            ["UserProfile"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        _detectSystemComponents = detectSystemComponents;
    }

    public async Task<MigrationResult> BackupAsync(
        string destinationRoot,
        MigrationOptions options,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.IncludeDocuments && !options.IncludeVsCode && (options.SelectedApps?.Count ?? 0) == 0)
            throw new ArgumentException("Select at least one item to back up.", nameof(options));
        if (string.IsNullOrWhiteSpace(destinationRoot))
            throw new ArgumentException("Choose a destination folder.", nameof(destinationRoot));

        var started = Stopwatch.StartNew();
        var warnings = new List<string>();
        var selectedApps = options.SelectedApps ?? [];
        var systemComponents = _detectSystemComponents && OperatingSystem.IsWindows()
            ? await new SystemComponentService().DetectAsync(cancellationToken)
            : [];
        var packageName = $"Carry-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd-HHmm}";
        var packagePath = GetUniqueDirectory(Path.Combine(Path.GetFullPath(destinationRoot), packageName));

        var sources = new List<CopyRoot>();
        if (options.IncludeDocuments)
        {
            if (Directory.Exists(DocumentsPath))
                sources.Add(new(DocumentsPath, Path.Combine(packagePath, "Documents")));
            else
                warnings.Add($"Documents was not found: {DocumentsPath}");
        }

        if (options.IncludeVsCode)
        {
            if (Directory.Exists(VsCodeUserPath))
                sources.Add(new(VsCodeUserPath, Path.Combine(packagePath, "VSCode", "User")));
            else
                warnings.Add($"VS Code settings were not found: {VsCodeUserPath}");
        }
        foreach (var app in selectedApps)
        foreach (var folder in app.DataFolders ?? [])
        {
            var source = ResolveAppDataPath(folder);
            if (Directory.Exists(source))
                sources.Add(new(source, Path.Combine(packagePath, "AppData",
                    SafeName(app.Identifier), folder.Scope, folder.RelativePath)));
            else
                warnings.Add($"App data was not found for {app.DisplayName}: {source}");
        }

        Directory.CreateDirectory(packagePath);
        var files = Scan(sources, packagePath, warnings, cancellationToken);
        var totalBytes = files.Sum(f => f.Length);
        var cutoff = DateTimeOffset.Now.AddMonths(-options.InactivityMonths);
        var inactiveFiles = files
            .Where(file => IsInside(DocumentsPath, file.Source) && IsInactive(file.Source, cutoff))
            .ToArray();
        progress?.Report(new("Preparing", "Transfer plan ready", 0, files.Count, 0, totalBytes));

        var copied = await CopyFilesAsync(files, overwrite: true, progress, cancellationToken);
        if (options.IncludeVsCode)
            await ExportExtensionsAsync(Path.Combine(packagePath, "VSCode", "extensions.txt"), warnings, cancellationToken);
        if (selectedApps.Count > 0)
            await File.WriteAllTextAsync(Path.Combine(packagePath, AppsFileName),
                JsonSerializer.Serialize(selectedApps, JsonOptions), cancellationToken);
        if (systemComponents.Count > 0)
            await File.WriteAllTextAsync(Path.Combine(packagePath, ComponentsFileName),
                JsonSerializer.Serialize(systemComponents, JsonOptions), cancellationToken);

        if (inactiveFiles.Length > 0)
            await WriteInactiveReportAsync(Path.Combine(packagePath, InactiveFilesName),
                inactiveFiles, DocumentsPath, cancellationToken);

        var manifest = new MigrationManifest
        {
            FileCount = copied.Files,
            TotalBytes = copied.Bytes,
            IncludesDocuments = options.IncludeDocuments,
            IncludesVsCode = options.IncludeVsCode,
            SelectedAppCount = selectedApps.Count,
            SystemComponentCount = systemComponents.Count,
            InactiveFileCount = inactiveFiles.Length
        };
        await File.WriteAllTextAsync(
            Path.Combine(packagePath, MigrationManifest.FileName),
            JsonSerializer.Serialize(manifest, JsonOptions),
            cancellationToken);

        progress?.Report(new("Complete", packagePath, copied.Files, files.Count, copied.Bytes, totalBytes));
        return new(true, packagePath, copied.Files, copied.Bytes, warnings, started.Elapsed);
    }

    public async Task<MigrationResult> RestoreAsync(
        string packagePath,
        MigrationOptions options,
        IProgress<MigrationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var warnings = new List<string>();
        packagePath = Path.GetFullPath(packagePath);
        var manifestPath = Path.Combine(packagePath, MigrationManifest.FileName);
        if (!File.Exists(manifestPath))
            throw new InvalidDataException("This folder is not a Carry backup (manifest is missing).");

        var manifest = JsonSerializer.Deserialize<MigrationManifest>(
            await File.ReadAllTextAsync(manifestPath, cancellationToken), JsonOptions)
            ?? throw new InvalidDataException("The Carry manifest could not be read.");
        if (manifest.FormatVersion != 1)
            throw new InvalidDataException($"Backup format {manifest.FormatVersion} is not supported.");

        var roots = new List<CopyRoot>();
        var appsPath = Path.Combine(packagePath, AppsFileName);
        var selectedApps = File.Exists(appsPath)
            ? JsonSerializer.Deserialize<AppPackage[]>(
                await File.ReadAllTextAsync(appsPath, cancellationToken), JsonOptions) ?? []
            : [];
        var documentsSource = Path.Combine(packagePath, "Documents");
        if (options.IncludeDocuments && Directory.Exists(documentsSource))
            roots.Add(new(documentsSource, DocumentsPath));

        var vsCodeSource = Path.Combine(packagePath, "VSCode", "User");
        if (options.IncludeVsCode && Directory.Exists(vsCodeSource))
            roots.Add(new(vsCodeSource, VsCodeUserPath));

        var files = Scan(roots, packagePath: null, warnings, cancellationToken);
        var totalBytes = files.Sum(f => f.Length);
        progress?.Report(new("Preparing", "Restore plan ready", 0, files.Count, 0, totalBytes));
        var copied = await CopyFilesAsync(files, options.OverwriteExisting, progress, cancellationToken);

        await ImportSystemComponentsAsync(
            Path.Combine(packagePath, ComponentsFileName), warnings, progress, cancellationToken);
        await ImportAppsAsync(appsPath, warnings, progress, cancellationToken);
        var appRoots = new List<CopyRoot>();
        foreach (var app in selectedApps)
        foreach (var folder in app.DataFolders ?? [])
        {
            var source = Path.Combine(packagePath, "AppData", SafeName(app.Identifier),
                folder.Scope, folder.RelativePath);
            if (Directory.Exists(source))
                appRoots.Add(new(source, ResolveAppDataPath(folder)));
        }
        var appFiles = Scan(appRoots, packagePath: null, warnings, cancellationToken);
        if (appFiles.Count > 0)
        {
            progress?.Report(new("Restoring app profiles", "Preparing app data",
                0, appFiles.Count, 0, appFiles.Sum(x => x.Length)));
            var appCopied = await CopyFilesAsync(appFiles, overwrite: true, progress, cancellationToken);
            copied = (copied.Files + appCopied.Files, copied.Bytes + appCopied.Bytes);
        }
        if (options.IncludeVsCode)
            await ImportExtensionsAsync(Path.Combine(packagePath, "VSCode", "extensions.txt"), warnings, progress, cancellationToken);

        progress?.Report(new("Complete", packagePath, copied.Files, copied.Files, copied.Bytes, copied.Bytes));
        return new(true, packagePath, copied.Files, copied.Bytes, warnings, started.Elapsed);
    }

    public static async Task<MigrationManifest> ReadManifestAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(packagePath, MigrationManifest.FileName);
        if (!File.Exists(path))
            throw new InvalidDataException("This folder is not a Carry backup.");
        return JsonSerializer.Deserialize<MigrationManifest>(
            await File.ReadAllTextAsync(path, cancellationToken), JsonOptions)
            ?? throw new InvalidDataException("The Carry manifest could not be read.");
    }

    private static List<CopyFile> Scan(
        IEnumerable<CopyRoot> roots,
        string? packagePath,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var result = new List<CopyFile>();
        foreach (var root in roots)
        {
            if (packagePath is not null && IsInside(root.Source, packagePath))
                throw new InvalidOperationException("The backup destination cannot be inside Documents.");

            try
            {
                foreach (var path in EnumerateFilesSafely(root.Source, warnings, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var info = new FileInfo(path);
                        result.Add(new(path, Path.Combine(root.Destination, Path.GetRelativePath(root.Source, path)), info.Length));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"Skipped {path}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not fully scan {root.Source}: {ex.Message}");
            }
        }
        return result;
    }

    private static IEnumerable<string> EnumerateFilesSafely(
        string root,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Skipped folder {directory}: {ex.Message}");
                continue;
            }

            foreach (var file in files)
                yield return file;
            foreach (var child in directories)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    {
                        warnings.Add($"Skipped linked folder to avoid copying outside Documents: {child}");
                        continue;
                    }
                    pending.Push(child);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Skipped folder {child}: {ex.Message}");
                }
            }
        }
    }

    private static async Task<(long Files, long Bytes)> CopyFilesAsync(
        IReadOnlyList<CopyFile> files,
        bool overwrite,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        long filesDone = 0;
        long bytesDone = 0;
        var totalBytes = files.Sum(f => f.Length);
        var buffer = new byte[1024 * 1024];

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(file.Destination) && !overwrite)
            {
                filesDone++;
                bytesDone += file.Length;
                progress?.Report(new("Copying", $"Kept existing: {Path.GetFileName(file.Destination)}",
                    filesDone, files.Count, bytesDone, totalBytes));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
            var partial = file.Destination + ".carrypart";
            try
            {
                {
                    await using var input = new FileStream(file.Source, FileMode.Open, FileAccess.Read, FileShare.Read,
                        buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None,
                        buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        bytesDone += read;
                        progress?.Report(new("Copying", file.Source, filesDone, files.Count, bytesDone, totalBytes));
                    }
                    await output.FlushAsync(cancellationToken);
                }
                File.Move(partial, file.Destination, true);
                File.SetLastWriteTimeUtc(file.Destination, File.GetLastWriteTimeUtc(file.Source));
                filesDone++;
            }
            catch
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                throw;
            }
        }

        return (filesDone, bytesDone);
    }

    private static async Task ExportExtensionsAsync(string outputPath, List<string> warnings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        try
        {
            var result = await RunCodeAsync("--list-extensions", cancellationToken);
            if (result.ExitCode == 0)
                await File.WriteAllTextAsync(outputPath, result.Output.Trim(), cancellationToken);
            else
                warnings.Add("VS Code extension list could not be exported. " + result.Error.Trim());
        }
        catch (Exception ex)
        {
            warnings.Add("VS Code extension list could not be exported: " + ex.Message);
        }
    }

    private static async Task ImportExtensionsAsync(
        string listPath,
        List<string> warnings,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(listPath))
        {
            warnings.Add("No VS Code extension list was found in this backup.");
            return;
        }

        var extensions = (await File.ReadAllLinesAsync(listPath, cancellationToken))
            .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        for (var i = 0; i < extensions.Length; i++)
        {
            progress?.Report(new("Installing VS Code extensions", extensions[i], i, extensions.Length, 0, 0));
            var result = await RunCodeAsync($"--install-extension \"{extensions[i]}\" --force", cancellationToken);
            if (result.ExitCode != 0)
                warnings.Add($"Could not install {extensions[i]}: {result.Error.Trim()}");
        }
    }

    private static async Task ImportAppsAsync(
        string listPath,
        List<string> warnings,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(listPath)) return;
        var apps = JsonSerializer.Deserialize<AppPackage[]>(
            await File.ReadAllTextAsync(listPath, cancellationToken), JsonOptions) ?? [];
        for (var i = 0; i < apps.Length; i++)
        {
            progress?.Report(new("Installing selected apps", apps[i].DisplayName, i, apps.Length, 0, 0));
            if (!apps[i].CanReinstall)
            {
                warnings.Add($"{apps[i].DisplayName} is not available through Windows Package Manager. " +
                    "Its saved data was carried, but install the app manually.");
                continue;
            }
            try
            {
                var result = await RunProcessAsync("winget.exe",
                    $"install --id \"{apps[i].Identifier}\" --exact --accept-package-agreements " +
                    "--accept-source-agreements --disable-interactivity", cancellationToken);
                if (result.ExitCode != 0)
                    warnings.Add($"Could not install {apps[i].DisplayName}: {result.Error.Trim()}");
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not install {apps[i].DisplayName}: {ex.Message}");
            }
        }
    }

    private static async Task ImportSystemComponentsAsync(
        string listPath,
        List<string> warnings,
        IProgress<MigrationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(listPath)) return;
        var components = JsonSerializer.Deserialize<SystemComponent[]>(
            await File.ReadAllTextAsync(listPath, cancellationToken), JsonOptions) ?? [];
        for (var i = 0; i < components.Length; i++)
        {
            var component = components[i];
            progress?.Report(new("Installing system components", component.Name,
                i, components.Length, 0, 0));
            if (!string.IsNullOrWhiteSpace(component.WindowsFeature))
            {
                try
                {
                    var exitCode = await RunElevatedAsync("dism.exe",
                        $"/Online /Enable-Feature /FeatureName:{component.WindowsFeature} /All /NoRestart",
                        cancellationToken);
                    if (exitCode != 0)
                        warnings.Add($"Windows could not enable {component.Name} (exit code {exitCode}).");
                }
                catch (Exception ex)
                {
                    warnings.Add($"Windows could not enable {component.Name}: {ex.Message}");
                }
                continue;
            }
            if (!component.CanReinstall || string.IsNullOrWhiteSpace(component.WingetIdentifier))
            {
                warnings.Add($"{component.Name} {component.Version} was detected but needs manual installation.");
                continue;
            }
            try
            {
                var result = await RunProcessAsync("winget.exe",
                    $"install --id \"{component.WingetIdentifier}\" --exact " +
                    "--accept-package-agreements --accept-source-agreements --disable-interactivity",
                    cancellationToken);
                if (result.ExitCode != 0)
                    warnings.Add($"Could not install {component.Name}: {result.Error.Trim()}");
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not install {component.Name}: {ex.Message}");
            }
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCodeAsync(string arguments, CancellationToken cancellationToken)
        => await RunProcessAsync("code.cmd", arguments, cancellationToken);

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string fileName, string arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("VS Code could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static async Task<int> RunElevatedAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using var process = Process.Start(start) ??
            throw new InvalidOperationException($"{fileName} could not start.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private static bool IsInactive(string path, DateTimeOffset cutoff)
    {
        try
        {
            var info = new FileInfo(path);
            var activity = info.LastAccessTimeUtc > info.LastWriteTimeUtc
                ? info.LastAccessTimeUtc : info.LastWriteTimeUtc;
            return activity < cutoff.UtcDateTime;
        }
        catch { return false; }
    }

    private static async Task WriteInactiveReportAsync(
        string path,
        IReadOnlyList<CopyFile> files,
        string documentsRoot,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        await writer.WriteLineAsync("Relative path,Size (bytes),Last accessed,Last modified");
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file.Source);
            var relative = Path.GetRelativePath(documentsRoot, file.Source).Replace("\"", "\"\"");
            await writer.WriteLineAsync(
                $"\"{relative}\",{info.Length},\"{info.LastAccessTime:O}\",\"{info.LastWriteTime:O}\"");
        }
    }

    private static string GetUniqueDirectory(string preferred)
    {
        if (!Directory.Exists(preferred) && !File.Exists(preferred))
            return preferred;
        for (var i = 2; ; i++)
        {
            var candidate = $"{preferred}-{i}";
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }
    }

    private static bool IsInside(string parent, string candidate)
    {
        var parentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        var candidatePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)) + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveAppDataPath(AppDataFolder folder)
    {
        if (!_appDataRoots.TryGetValue(folder.Scope, out var root))
            throw new InvalidDataException($"Unknown app data scope: {folder.Scope}");
        var resolved = Path.GetFullPath(Path.Combine(root, folder.RelativePath));
        if (!IsInside(root, resolved))
            throw new InvalidDataException("An app data path points outside its allowed folder.");
        return resolved;
    }

    private static string SafeName(string value) =>
        string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    private sealed record CopyRoot(string Source, string Destination);
    private sealed record CopyFile(string Source, string Destination, long Length);
}
