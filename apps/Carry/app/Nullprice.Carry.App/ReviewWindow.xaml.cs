using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Nullprice.Carry.Core;

namespace Nullprice.Carry.App;

public partial class ReviewWindow : Window
{
    private readonly ReviewService _reviewService = new();
    private readonly string _documentsPath;
    private readonly int _months;
    private readonly bool _includeDocuments;
    private readonly bool _includeVsCode;
    private readonly string _vsCodePath;
    private readonly IReadOnlyList<string> _additionalFolders;
    private readonly HashSet<string> _previousFileExclusions;
    private readonly HashSet<string> _previousSelection;
    private readonly ObservableCollection<AppRow> _apps = [];
    private readonly ObservableCollection<FileRow> _files = [];
    private readonly CancellationTokenSource _reviewCancellation = new();
    private long _documentsBytes;
    private long _vsCodeBytes;
    private long _additionalBytes;

    public IReadOnlyList<AppPackage> SelectedApps =>
        _apps.Where(x => x.IsSelected).Select(x => x.Package).ToArray();
    public IReadOnlyList<string> ExcludedFiles =>
        _files.Where(x => !x.IsSelected).Select(x => x.FullPath).ToArray();

    public ReviewWindow(
        string documentsPath,
        int months,
        IReadOnlyList<AppPackage> previousSelection,
        bool includeDocuments,
        bool includeVsCode,
        string vsCodePath,
        IReadOnlyList<string> additionalFolders,
        IReadOnlyList<string> previousFileExclusions)
    {
        InitializeComponent();
        _documentsPath = documentsPath;
        _months = months;
        _includeDocuments = includeDocuments;
        _includeVsCode = includeVsCode;
        _vsCodePath = vsCodePath;
        _additionalFolders = additionalFolders.ToArray();
        _previousFileExclusions = previousFileExclusions
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _previousSelection = previousSelection.Select(x => x.Identifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        AppsGrid.ItemsSource = _apps;
        FilesGrid.ItemsSource = _files;
        Loaded += LoadReviewAsync;
        Closed += (_, _) => _reviewCancellation.Cancel();
    }

    private async void LoadReviewAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            var appsTask = _reviewService.DiscoverReinstallableAppsAsync(_reviewCancellation.Token);
            var fileProgress = new Progress<string>(path =>
                SummaryText.Text = $"Checking {Path.GetFileName(path)}…");
            var filesTask = _reviewService.FindInactiveFilesAsync(
                _documentsPath, _months, fileProgress, _reviewCancellation.Token);
            var vsCodeSizeTask = _includeVsCode
                ? _reviewService.CalculateFolderSizeAsync(_vsCodePath, _reviewCancellation.Token)
                : Task.FromResult(0L);
            var additionalSizeTasks = _additionalFolders
                .Select(path => _reviewService.CalculateFolderSizeAsync(path, _reviewCancellation.Token))
                .ToArray();
            await Task.WhenAll([appsTask, filesTask, vsCodeSizeTask, .. additionalSizeTasks]);

            foreach (var app in await appsTask)
                _apps.Add(new(app, _previousSelection.Contains(app.Identifier), _months));
            var fileReview = await filesTask;
            _documentsBytes = fileReview.TotalBytes;
            _vsCodeBytes = await vsCodeSizeTask;
            _additionalBytes = additionalSizeTasks.Sum(task => task.Result);
            foreach (var file in fileReview.InactiveFiles.Take(10_000))
                _files.Add(new(file, !_previousFileExclusions.Contains(file.FullPath)));

            SummaryText.Text = $"{_apps.Count:N0} installed apps found • " +
                $"{fileReview.InactiveFileCount:N0} files possibly inactive for {_months}+ months";
            if (fileReview.InactiveFileCount > 10_000)
                SummaryText.Text += " • showing 10,000 files";
            UpdateSizeSummary();
            DoneButton.IsEnabled = true;
            _ = CalculateSelectedSizesAsync();
            ShowCategory("Apps");
        }
        catch (OperationCanceledException)
        {
            // Closing the review window intentionally cancels its background scans.
        }
        catch (Exception ex)
        {
            SummaryText.Text = ex.Message;
            DoneButton.IsEnabled = true;
        }
    }

    private async void SelectAllClicked(object sender, RoutedEventArgs e)
    {
        foreach (var app in AppsGrid.ItemsSource.Cast<AppRow>()) app.IsSelected = true;
        UpdateSizeSummary();
        await CalculateSelectedSizesAsync();
    }

    private void ClearClicked(object sender, RoutedEventArgs e)
    {
        foreach (var app in _apps) app.IsSelected = false;
        UpdateSizeSummary();
    }

    private void CategoryClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string category })
            ShowCategory(category);
    }

    private void ShowCategory(string category)
    {
        AppsGrid.ItemsSource = category == "All"
            ? _apps
            : _apps.Where(app => app.Category == category).ToArray();
    }

    private async void BundleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string bundle }) return;
        foreach (var app in _apps.Where(app => MatchesBundle(app, bundle)))
            app.IsSelected = true;
        UpdateSizeSummary();
        await CalculateSelectedSizesAsync();
    }

    private static bool MatchesBundle(AppRow app, string bundle)
    {
        var value = (app.Package.Identifier + " " + app.DisplayName).ToLowerInvariant();
        string[] terms = bundle switch
        {
            "Browsers" => ["chrome", "firefox", "edge", "brave", "opera", "vivaldi"],
            "Coding" => ["visual studio", "vscode", "code", "git", "github", "python",
                "node", "dotnet", ".net", "java", "android studio", "unity", "gamemaker"],
            "Gaming" => ["steam", "epic", "game", "minecraft", "roblox", "gog",
                "battle.net", "ea app", "ubisoft", "xbox"],
            "Communication" => ["discord", "slack", "teams", "zoom", "telegram",
                "whatsapp", "signal", "skype"],
            _ => []
        };
        return terms.Any(value.Contains);
    }

    private void DoneClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void AppSelectionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox check || check.DataContext is not AppRow row) return;
        row.IsSelected = check.IsChecked == true;
        UpdateSizeSummary();
        if (row.IsSelected)
            await EnsureAppSizeAsync(row);
    }

    private async Task CalculateSelectedSizesAsync()
    {
        foreach (var row in _apps.Where(x => x.IsSelected).ToArray())
        {
            if (_reviewCancellation.IsCancellationRequested) return;
            await EnsureAppSizeAsync(row);
        }
    }

    private async Task EnsureAppSizeAsync(AppRow row)
    {
        if (row.SizeCalculated || row.IsCalculating) return;
        row.IsCalculating = true;
        UpdateSizeSummary();
        try
        {
            var size = await _reviewService.CalculateAppDataSizeAsync(
                row.Package, _reviewCancellation.Token);
            row.SetCalculatedSize(size);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            row.SetSizeError(ex.Message);
        }
        finally
        {
            row.IsCalculating = false;
            if (!_reviewCancellation.IsCancellationRequested)
            {
                UpdateSizeSummary();
            }
        }
    }

    private void UpdateSizeSummary()
    {
        var selectedRows = _apps.Where(x => x.IsSelected).ToArray();
        var appBytes = selectedRows.Sum(x => x.DataSizeBytes);
        var documentsBytes = _includeDocuments ? _documentsBytes : 0;
        var vsCodeBytes = _includeVsCode ? _vsCodeBytes : 0;
        SizeText.Text = $"Estimated selected size: " +
            $"{FormatBytes(documentsBytes + appBytes + vsCodeBytes + _additionalBytes)}  " +
            $"(Documents {FormatBytes(documentsBytes)} + VS Code {FormatBytes(vsCodeBytes)} + " +
            $"app data {FormatBytes(appBytes)} + extra {_additionalFolders.Count} / " +
            $"{FormatBytes(_additionalBytes)})";
        if (selectedRows.Any(x => x.IsCalculating))
            SizeText.Text += " • calculating selected app data…";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    public sealed class AppRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private long _dataSizeBytes;
        private bool _sizeCalculated;
        private bool _isCalculating;
        private string? _sizeError;

        public AppPackage Package { get; }
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }
        public long DataSizeBytes => _dataSizeBytes;
        public bool SizeCalculated => _sizeCalculated;
        public bool IsCalculating
        {
            get => _isCalculating;
            set
            {
                if (_isCalculating == value) return;
                _isCalculating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DataText));
            }
        }
        public string DisplayName => Package.DisplayName;
        public string? Version => Package.Version;
        public string DataText => IsCalculating ? "Calculating…" :
            _sizeError is not null ? "Size unavailable" :
            $"{FormatBytes(_dataSizeBytes)} / {Package.DataFolders?.Count ?? 0}";
        public string InstallText => Package.CanReinstall ? "Automatic" : "Manual";
        public string LastUsedText => Package.LastUsedEstimate?.LocalDateTime.ToString("d") ?? "Unknown";
        public string Status { get; }
        public string Category { get; }

        public AppRow(AppPackage package, bool selected, int months)
        {
            Package = package;
            _isSelected = selected;
            _dataSizeBytes = package.DataSizeBytes;
            _sizeCalculated = package.DataSizeBytes > 0 || (package.DataFolders?.Count ?? 0) == 0;
            Category = Classify(package);
            Status = package.IsLikelyInactive(months) ? "Possibly inactive" :
                package.LastUsedEstimate is null ? "Usage unknown" : "Recently used";
        }

        private static string Classify(AppPackage package)
        {
            var value = (package.Identifier + " " + package.DisplayName).ToLowerInvariant();
            string[] system = ["runtime", "redistributable", "framework", "driver", "sdk",
                "update", "installer", "service", "support", "webview", "extension"];
            if (system.Any(value.Contains)) return "System";
            string[] development = ["visual studio", "vscode", "git", "python", "node",
                "java", "android studio", "unity", "gamemaker", "developer"];
            if (development.Any(value.Contains)) return "Development";
            string[] games = ["steam", "epic", "game", "minecraft", "roblox", "gog",
                "battle.net", "ubisoft", "xbox"];
            if (games.Any(value.Contains)) return "Games";
            return "Apps";
        }

        public void SetCalculatedSize(long size)
        {
            _dataSizeBytes = size;
            _sizeCalculated = true;
            _sizeError = null;
            OnPropertyChanged(nameof(DataSizeBytes));
            OnPropertyChanged(nameof(SizeCalculated));
            OnPropertyChanged(nameof(DataText));
        }

        public void SetSizeError(string message)
        {
            _sizeError = message;
            _sizeCalculated = true;
            OnPropertyChanged(nameof(SizeCalculated));
            OnPropertyChanged(nameof(DataText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class FileRow
    {
        public string FullPath { get; }
        public bool IsSelected { get; set; }
        public string RelativePath { get; }
        public string SizeText { get; }
        public string LastActivityText { get; }

        public FileRow(InactiveFile file, bool selected)
        {
            FullPath = file.FullPath;
            IsSelected = selected;
            RelativePath = file.RelativePath;
            SizeText = FormatSize(file.Size);
            LastActivityText = file.LastActivity.LocalDateTime.ToString("g");
        }

        private static string FormatSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB"];
            var value = (double)bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return $"{value:0.#} {units[unit]}";
        }
    }
}
