using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Nullprice.Carry.Core;

[SupportedOSPlatform("windows")]
public sealed class SystemComponentService
{
    public async Task<IReadOnlyList<SystemComponent>> DetectAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<SystemComponent>();
        await DetectDotNetAsync(result, cancellationToken);
        DetectClassicDotNet(result);
        DetectVisualCpp(result);
        await DetectCommandAsync(result, "node.exe", "--version", "Node.js",
            version => "OpenJS.NodeJS", cancellationToken);
        await DetectPythonAsync(result, cancellationToken);
        await DetectCommandAsync(result, "java.exe", "-version", "Java",
            _ => null, cancellationToken, versionFromError: true);
        return result
            .DistinctBy(x => x.WindowsFeature ?? x.WingetIdentifier ?? $"{x.Name}|{x.Version}",
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static async Task DetectDotNetAsync(
        ICollection<SystemComponent> result,
        CancellationToken cancellationToken)
    {
        try
        {
            var runtimes = await RunAsync("dotnet.exe", "--list-runtimes", cancellationToken);
            if (runtimes.ExitCode == 0)
            {
                foreach (var line in SplitLines(runtimes.Output))
                {
                    var match = Regex.Match(line, @"^(?<name>\S+)\s+(?<version>\d+\.\d+\.\S+)");
                    if (!match.Success) continue;
                    var framework = match.Groups["name"].Value;
                    var version = match.Groups["version"].Value;
                    var major = version.Split('.')[0];
                    var id = framework switch
                    {
                        "Microsoft.NETCore.App" => $"Microsoft.DotNet.Runtime.{major}",
                        "Microsoft.WindowsDesktop.App" => $"Microsoft.DotNet.DesktopRuntime.{major}",
                        "Microsoft.AspNetCore.App" => $"Microsoft.DotNet.AspNetCore.{major}",
                        _ => null
                    };
                    result.Add(new(framework, version, id, id is not null));
                }
            }
            var sdks = await RunAsync("dotnet.exe", "--list-sdks", cancellationToken);
            if (sdks.ExitCode == 0)
            {
                foreach (var line in SplitLines(sdks.Output))
                {
                    var version = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(version)) continue;
                    var major = version.Split('.')[0];
                    result.Add(new(".NET SDK", version, $"Microsoft.DotNet.SDK.{major}", true));
                }
            }
        }
        catch { }
    }

    private static void DetectVisualCpp(ICollection<SystemComponent> result)
    {
        var locations = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32)
        };
        foreach (var (hive, view) in locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(keyName);
                    var name = key?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name) ||
                        !name.Contains("Microsoft Visual C++", StringComparison.OrdinalIgnoreCase) ||
                        (!name.Contains("2015", StringComparison.OrdinalIgnoreCase) &&
                         !name.Contains("2017", StringComparison.OrdinalIgnoreCase) &&
                         !name.Contains("2019", StringComparison.OrdinalIgnoreCase) &&
                         !name.Contains("2022", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var architecture = name.Contains("x86", StringComparison.OrdinalIgnoreCase) ? "x86" : "x64";
                    var version = key?.GetValue("DisplayVersion") as string ?? "2015–2022";
                    result.Add(new($"Visual C++ Redistributable ({architecture})", version,
                        $"Microsoft.VCRedist.2015+.{architecture}", true));
                }
            }
            catch { }
        }
    }

    private static void DetectClassicDotNet(ICollection<SystemComponent> result)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var ndp = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP");
            if (ndp is null) return;
            WalkFrameworkKeys(ndp, "", result);
        }
        catch { }
    }

    private static void WalkFrameworkKeys(
        RegistryKey key,
        string path,
        ICollection<SystemComponent> result)
    {
        try
        {
            var version = key.GetValue("Version") as string;
            var installedValue = key.GetValue("Install");
            var installed = installedValue is null || Convert.ToInt32(installedValue) == 1;
            if (installed && !string.IsNullOrWhiteSpace(version))
            {
                var feature = version.StartsWith("2.", StringComparison.OrdinalIgnoreCase) ||
                              version.StartsWith("3.", StringComparison.OrdinalIgnoreCase)
                    ? "NetFx3"
                    : version.StartsWith("4.", StringComparison.OrdinalIgnoreCase)
                        ? "NetFx4-AdvSrvs"
                        : null;
                result.Add(new($".NET Framework ({path.TrimStart('\\')})", version,
                    null, feature is not null, feature));
            }
            foreach (var childName in key.GetSubKeyNames())
            {
                using var child = key.OpenSubKey(childName);
                if (child is not null)
                    WalkFrameworkKeys(child, path + "\\" + childName, result);
            }
        }
        catch { }
    }

    private static async Task DetectPythonAsync(
        ICollection<SystemComponent> result,
        CancellationToken cancellationToken)
    {
        try
        {
            var python = await RunAsync("py.exe", "-0", cancellationToken);
            foreach (var line in SplitLines(python.Output + Environment.NewLine + python.Error))
            {
                var match = Regex.Match(line, @"(?<!\d)(?<major>3)\.(?<minor>\d+)");
                if (!match.Success) continue;
                var version = $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}";
                result.Add(new("Python", version, $"Python.Python.{version}", true));
            }
        }
        catch { }
    }

    private static async Task DetectCommandAsync(
        ICollection<SystemComponent> result,
        string command,
        string arguments,
        string name,
        Func<string, string?> idFactory,
        CancellationToken cancellationToken,
        bool versionFromError = false)
    {
        try
        {
            var commandResult = await RunAsync(command, arguments, cancellationToken);
            var version = (versionFromError ? commandResult.Error : commandResult.Output).Trim();
            if (version.Length == 0) return;
            var id = idFactory(version);
            result.Add(new(name, version.Split(Environment.NewLine)[0], id, id is not null));
        }
        catch { }
    }

    private static IEnumerable<string> SplitLines(string value) =>
        value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, await error);
    }
}
