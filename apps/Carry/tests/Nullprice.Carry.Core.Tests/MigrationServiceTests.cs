using Nullprice.Carry.Core;
using Xunit;

namespace Nullprice.Carry.Core.Tests;

public sealed class MigrationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CarryTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BackupAndRestorePreservesNestedDocuments()
    {
        var source = Path.Combine(_root, "source");
        var destination = Path.Combine(_root, "drive");
        var restored = Path.Combine(_root, "restored");
        Directory.CreateDirectory(Path.Combine(source, "Projects", "Demo"));
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "hello.txt"), "hello");
        await File.WriteAllBytesAsync(Path.Combine(source, "Projects", "Demo", "data.bin"), [0, 1, 2, 255]);
        var backupService = new MigrationService(source, Path.Combine(_root, "missing-vscode"),
            detectSystemComponents: false);
        var result = await backupService.BackupAsync(destination, new MigrationOptions(true, false));
        var manifest = await MigrationService.ReadManifestAsync(result.PackagePath);

        Assert.Equal(2, manifest.FileCount);
        Assert.True(File.Exists(Path.Combine(result.PackagePath, "Documents", "Projects", "Demo", "data.bin")));

        var restoreService = new MigrationService(restored, Path.Combine(_root, "restored-vscode"),
            detectSystemComponents: false);
        await restoreService.RestoreAsync(result.PackagePath, new MigrationOptions(true, false));

        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(restored, "hello.txt")));
        Assert.Equal([0, 1, 2, 255], await File.ReadAllBytesAsync(Path.Combine(restored, "Projects", "Demo", "data.bin")));
    }

    [Fact]
    public async Task RestoreKeepsExistingFilesUnlessOverwriteIsSelected()
    {
        var source = Path.Combine(_root, "source");
        var destination = Path.Combine(_root, "drive");
        var restored = Path.Combine(_root, "restored");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(restored);
        await File.WriteAllTextAsync(Path.Combine(source, "note.txt"), "from old laptop");

        var backup = await new MigrationService(source, "unused", detectSystemComponents: false).BackupAsync(
            destination, new MigrationOptions(true, false));
        await File.WriteAllTextAsync(Path.Combine(restored, "note.txt"), "on new laptop");
        var restoreService = new MigrationService(restored, "unused", detectSystemComponents: false);

        await restoreService.RestoreAsync(backup.PackagePath, new MigrationOptions(true, false, false));
        Assert.Equal("on new laptop", await File.ReadAllTextAsync(Path.Combine(restored, "note.txt")));

        await restoreService.RestoreAsync(backup.PackagePath, new MigrationOptions(true, false, true));
        Assert.Equal("from old laptop", await File.ReadAllTextAsync(Path.Combine(restored, "note.txt")));
    }

    [Fact]
    public async Task BackupRefusesDestinationInsideDocuments()
    {
        var source = Path.Combine(_root, "source");
        var destination = Path.Combine(source, "backup");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "file.txt"), "content");

        var service = new MigrationService(source, "unused", detectSystemComponents: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.BackupAsync(destination, new MigrationOptions(true, false)));
    }

    [Fact]
    public async Task BackupRecordsSelectedAppsAndInactiveFiles()
    {
        var source = Path.Combine(_root, "source");
        var destination = Path.Combine(_root, "drive");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        var oldFile = Path.Combine(source, "old-notes.txt");
        await File.WriteAllTextAsync(oldFile, "old");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddYears(-3));
        File.SetLastAccessTimeUtc(oldFile, DateTime.UtcNow.AddYears(-3));
        var roaming = Path.Combine(_root, "roaming");
        Directory.CreateDirectory(Path.Combine(roaming, "ExampleApp", "Profile"));
        await File.WriteAllTextAsync(Path.Combine(roaming, "ExampleApp", "Profile", "settings.db"), "profile-data");
        AppPackage[] selectedApps =
            [new("Example.App", "Example App", "1.0", null, "Unknown",
                [new("Roaming", "ExampleApp")])];

        var appDataRoots = new Dictionary<string, string> { ["Roaming"] = roaming };
        var result = await new MigrationService(source, "unused", appDataRoots,
            detectSystemComponents: false).BackupAsync(destination,
            new MigrationOptions(true, false, false, selectedApps, 12));
        var manifest = await MigrationService.ReadManifestAsync(result.PackagePath);

        Assert.Equal(1, manifest.SelectedAppCount);
        Assert.Equal(1, manifest.InactiveFileCount);
        Assert.True(File.Exists(Path.Combine(result.PackagePath, "selected-apps.json")));
        Assert.Equal("profile-data", await File.ReadAllTextAsync(Path.Combine(result.PackagePath,
            "AppData", "Example.App", "Roaming", "ExampleApp", "Profile", "settings.db")));
        Assert.Contains("old-notes.txt",
            await File.ReadAllTextAsync(Path.Combine(result.PackagePath, "possibly-inactive-files.csv")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
