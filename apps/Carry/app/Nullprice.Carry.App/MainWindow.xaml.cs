using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Nullprice.Carry.Core;

namespace Nullprice.Carry.App;

public partial class MainWindow : Window
{
    private readonly MigrationService _service = new();
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<AppPackage> _selectedApps = [];
    private bool _reviewCompleted;
    private readonly List<string> _additionalFolders = [];
    private IReadOnlyList<string> _excludedDocumentFiles = [];

    public MainWindow()
    {
        InitializeComponent();
    }

    private bool IsRestore => RestoreMode.IsChecked == true;

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        PathLabel.Text = IsRestore ? "Choose the Carry transfer folder" : "Save the transfer folder to";
        PathHint.Text = IsRestore
            ? "Select the folder containing carry-manifest.json."
            : "Choose an external drive, USB disk, or shared network folder.";
        StartButton.Content = IsRestore ? "Restore this laptop →" : "Create transfer →";
        OverwriteCheck.Visibility = IsRestore ? Visibility.Visible : Visibility.Collapsed;
        AppsRow.Visibility = IsRestore ? Visibility.Collapsed : Visibility.Visible;
        InactivityRow.Visibility = IsRestore ? Visibility.Collapsed : Visibility.Visible;
        ComponentsNote.Visibility = IsRestore ? Visibility.Collapsed : Visibility.Visible;
        AdditionalFoldersRow.Visibility = IsRestore ? Visibility.Collapsed : Visibility.Visible;
        PathBox.Clear();
        ResetStatus();
    }

    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = IsRestore ? "Choose a Carry transfer folder" : "Choose where Carry should save the transfer",
            Multiselect = false
        };
        if (picker.ShowDialog(this) == true)
            PathBox.Text = picker.FolderName;
    }

    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PathBox.Text) || !Directory.Exists(PathBox.Text))
        {
            System.Windows.MessageBox.Show("Choose an existing folder first.", "Carry",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!IsRestore && DocumentsCheck.IsChecked != true && VsCodeCheck.IsChecked != true &&
            AppsCheck.IsChecked != true && _additionalFolders.Count == 0)
        {
            System.Windows.MessageBox.Show("Select at least one thing to carry.", "Carry",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!IsRestore && AppsCheck.IsChecked == true && !_reviewCompleted)
        {
            if (!ShowReview()) return;
            if (DocumentsCheck.IsChecked != true && VsCodeCheck.IsChecked != true &&
                _selectedApps.Count == 0 && _additionalFolders.Count == 0)
            {
                System.Windows.MessageBox.Show("Select at least one thing to carry.", "Carry",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        if (!IsRestore && _selectedApps.Count > 0)
        {
            var answer = System.Windows.MessageBox.Show(
                "Before continuing, close the selected apps (especially browsers, email, chat, and password tools). " +
                "This lets Carry copy their profile databases consistently.\n\nContinue when they are closed.",
                "Close selected apps", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK) return;
        }

        SetBusy(true);
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<MigrationProgress>(UpdateProgress);
        var options = new MigrationOptions(
            DocumentsCheck.IsChecked == true,
            VsCodeCheck.IsChecked == true,
            OverwriteCheck.IsChecked == true,
            _selectedApps,
            SelectedInactivityMonths,
            _additionalFolders,
            _excludedDocumentFiles);

        try
        {
            var result = IsRestore
                ? await _service.RestoreAsync(PathBox.Text, options, progress, _cancellation.Token)
                : await _service.BackupAsync(PathBox.Text, options, progress, _cancellation.Token);

            StatusText.Text = IsRestore ? "Restore complete" : "Transfer ready";
            ItemText.Text = $"{result.FilesCopied:N0} files • {FormatSize(result.BytesCopied)} • {result.Duration:mm\\:ss}";
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 100;
            ProgressText.Text = result.Warnings.Count == 0
                ? "Completed without warnings"
                : $"Completed with {result.Warnings.Count} warning(s)";

            var warningText = result.Warnings.Count == 0 ? "" :
                "\n\nWarnings:\n" + string.Join("\n", result.Warnings.Take(8));
            var action = IsRestore ? "Your files and VS Code setup are now on this laptop."
                : $"Your transfer is ready at:\n{result.PackagePath}";
            System.Windows.MessageBox.Show(action + warningText, "Carry complete",
                MessageBoxButton.OK, result.Warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (!IsRestore)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{result.PackagePath}\"") { UseShellExecute = true });
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled";
            ItemText.Text = "Any unfinished .carrypart file was removed. You can start again safely.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not finish";
            ItemText.Text = ex.Message;
            System.Windows.MessageBox.Show(ex.Message, "Carry could not finish",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            SetBusy(false);
        }
    }

    private void CancelClicked(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private void ReviewClicked(object sender, RoutedEventArgs e) => ShowReview();

    private void CableModeClicked(object sender, RoutedEventArgs e) =>
        new CableWindow { Owner = this }.ShowDialog();

    private void AddFolderClicked(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Choose an additional folder to carry",
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;
        var path = Path.GetFullPath(picker.FolderName);
        if (!_additionalFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
            _additionalFolders.Add(path);
        _reviewCompleted = false;
        UpdateAdditionalFoldersText();
    }

    private void ClearFoldersClicked(object sender, RoutedEventArgs e)
    {
        _additionalFolders.Clear();
        _reviewCompleted = false;
        UpdateAdditionalFoldersText();
    }

    private void UpdateAdditionalFoldersText()
    {
        AdditionalFoldersText.Text = _additionalFolders.Count == 0
            ? "None selected"
            : $"{_additionalFolders.Count:N0} selected: " +
              string.Join(", ", _additionalFolders.Select(Path.GetFileName));
    }

    private bool ShowReview()
    {
        var review = new ReviewWindow(_service.DocumentsPath, SelectedInactivityMonths, _selectedApps,
            DocumentsCheck.IsChecked == true, VsCodeCheck.IsChecked == true, _service.VsCodeUserPath,
            _additionalFolders, _excludedDocumentFiles)
        {
            Owner = this
        };
        if (review.ShowDialog() != true) return false;
        _selectedApps = review.SelectedApps;
        _excludedDocumentFiles = review.ExcludedFiles;
        _reviewCompleted = true;
        AppsCheck.IsChecked = _selectedApps.Count > 0;
        AppsCheck.Content = _selectedApps.Count == 0
            ? "Selected apps (none selected)"
            : $"Selected apps ({_selectedApps.Count:N0} chosen for reinstall)";
        return true;
    }

    private int SelectedInactivityMonths =>
        int.TryParse((InactivityMonths.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString(),
            out var months) ? months : 12;

    private void UpdateProgress(MigrationProgress value)
    {
        StatusText.Text = value.Stage;
        ItemText.Text = value.CurrentItem;
        if (value.TotalBytes > 0)
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = value.BytesCompleted * 100d / value.TotalBytes;
            ProgressText.Text = $"{value.FilesCompleted:N0} / {value.TotalFiles:N0} files • " +
                                $"{FormatSize(value.BytesCompleted)} / {FormatSize(value.TotalBytes)}";
        }
        else if (value.TotalFiles > 0)
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = value.FilesCompleted * 100d / value.TotalFiles;
            ProgressText.Text = $"{value.FilesCompleted:N0} / {value.TotalFiles:N0}";
        }
        else
        {
            ProgressBar.IsIndeterminate = true;
            ProgressText.Text = value.FilesCompleted > 0
                ? $"{value.FilesCompleted:N0} files found • {FormatSize(value.BytesCompleted)}"
                : "";
        }
    }

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy;
        BackupMode.IsEnabled = !busy;
        RestoreMode.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
        DocumentsCheck.IsEnabled = !busy;
        VsCodeCheck.IsEnabled = !busy;
        OverwriteCheck.IsEnabled = !busy;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResetStatus()
    {
        StatusText.Text = "Ready";
        ItemText.Text = "";
        ProgressText.Text = "";
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 0;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
