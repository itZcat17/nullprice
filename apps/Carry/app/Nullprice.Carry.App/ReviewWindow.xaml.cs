using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
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
    private readonly HashSet<string> _previousSelection;
    private readonly ObservableCollection<AppRow> _apps = [];
    private readonly ObservableCollection<FileRow> _files = [];
    private long _documentsBytes;
    private long _vsCodeBytes;

    public IReadOnlyList<AppPackage> SelectedApps =>
        _apps.Where(x => x.IsSelected).Select(x => x.Package).ToArray();

    public ReviewWindow(
        string documentsPath,
        int months,
        IReadOnlyList<AppPackage> previousSelection,
        bool includeDocuments,
        bool includeVsCode,
        string vsCodePath)
    {
        InitializeComponent();
        _documentsPath = documentsPath;
        _months = months;
        _includeDocuments = includeDocuments;
        _includeVsCode = includeVsCode;
        _vsCodePath = vsCodePath;
        _previousSelection = previousSelection.Select(x => x.Identifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        AppsGrid.ItemsSource = _apps;
        FilesGrid.ItemsSource = _files;
        Loaded += LoadReviewAsync;
    }

    private async void LoadReviewAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            var appsTask = _reviewService.DiscoverReinstallableAppsAsync();
            var fileProgress = new Progress<string>(path =>
                SummaryText.Text = $"Checking {Path.GetFileName(path)}…");
            var filesTask = _reviewService.FindInactiveFilesAsync(
                _documentsPath, _months, fileProgress);
            var vsCodeSizeTask = _includeVsCode
                ? _reviewService.CalculateFolderSizeAsync(_vsCodePath)
                : Task.FromResult(0L);
            await Task.WhenAll(appsTask, filesTask, vsCodeSizeTask);

            foreach (var app in await appsTask)
                _apps.Add(new(app, _previousSelection.Contains(app.Identifier), _months));
            var fileReview = await filesTask;
            _documentsBytes = fileReview.TotalBytes;
            _vsCodeBytes = await vsCodeSizeTask;
            foreach (var file in fileReview.InactiveFiles.Take(10_000))
                _files.Add(new(file));

            SummaryText.Text = $"{_apps.Count:N0} installed apps found • " +
                $"{fileReview.InactiveFiles.Count:N0} files possibly inactive for {_months}+ months";
            if (fileReview.InactiveFiles.Count > 10_000)
                SummaryText.Text += " • showing the oldest 10,000 files";
            UpdateSizeSummary();
            DoneButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SummaryText.Text = ex.Message;
            DoneButton.IsEnabled = true;
        }
    }

    private void SelectAllClicked(object sender, RoutedEventArgs e)
    {
        foreach (var app in _apps) app.IsSelected = true;
        AppsGrid.Items.Refresh();
        UpdateSizeSummary();
    }

    private void ClearClicked(object sender, RoutedEventArgs e)
    {
        foreach (var app in _apps) app.IsSelected = false;
        AppsGrid.Items.Refresh();
        UpdateSizeSummary();
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

    private void AppSelectionClicked(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateSizeSummary);

    private void UpdateSizeSummary()
    {
        var appBytes = _apps.Where(x => x.IsSelected).Sum(x => x.Package.DataSizeBytes);
        var documentsBytes = _includeDocuments ? _documentsBytes : 0;
        var vsCodeBytes = _includeVsCode ? _vsCodeBytes : 0;
        SizeText.Text = $"Estimated selected size: {FormatBytes(documentsBytes + appBytes + vsCodeBytes)}  " +
            $"(Documents {FormatBytes(documentsBytes)} + VS Code {FormatBytes(vsCodeBytes)} + " +
            $"app data {FormatBytes(appBytes)})";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    public sealed class AppRow
    {
        public AppPackage Package { get; }
        public bool IsSelected { get; set; }
        public string DisplayName => Package.DisplayName;
        public string? Version => Package.Version;
        public string DataText => $"{FormatBytes(Package.DataSizeBytes)} / {Package.DataFolders?.Count ?? 0}";
        public string InstallText => Package.CanReinstall ? "Automatic" : "Manual";
        public string LastUsedText => Package.LastUsedEstimate?.LocalDateTime.ToString("d") ?? "Unknown";
        public string Status { get; }

        public AppRow(AppPackage package, bool selected, int months)
        {
            Package = package;
            IsSelected = selected;
            Status = package.IsLikelyInactive(months) ? "Possibly inactive" :
                package.LastUsedEstimate is null ? "Usage unknown" : "Recently used";
        }
    }

    public sealed class FileRow
    {
        public string RelativePath { get; }
        public string SizeText { get; }
        public string LastActivityText { get; }

        public FileRow(InactiveFile file)
        {
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
