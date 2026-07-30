using System.IO;
using System.Windows;
using Microsoft.Win32;
using Nullprice.Ferry.Core;

namespace Nullprice.Ferry.App;

/// <summary>
/// Scaffold shell. Deliberately plain code-behind rather than MVVM — this exists to drive
/// the engine and prove it works end to end, and is expected to be replaced once the
/// interaction design is settled.
/// </summary>
public partial class MainWindow : Window
{
    private readonly List<string> _sources = [];
    private string? _destination;
    private CancellationTokenSource? _cts;

    public MainWindow() => InitializeComponent();

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Add files to copy" };
        if (dialog.ShowDialog(this) != true) return;

        _sources.AddRange(dialog.FileNames);
        RefreshSources();
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add a folder to copy" };
        if (dialog.ShowDialog(this) != true) return;

        _sources.Add(dialog.FolderName);
        RefreshSources();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _sources.Clear();
        RefreshSources();
    }

    private void OnChooseDestination(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to copy to" };
        if (dialog.ShowDialog(this) != true) return;

        _destination = dialog.FolderName;
        DestText.Text = _destination;
    }

    private void RefreshSources()
    {
        SourceList.Items.Clear();
        foreach (var s in _sources) SourceList.Items.Add(s);
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_sources.Count == 0)
        {
            StatusText.Text = "Add at least one file or folder first.";
            return;
        }

        if (string.IsNullOrEmpty(_destination))
        {
            StatusText.Text = "Choose a destination folder first.";
            return;
        }

        CopyPlan plan;
        try
        {
            plan = PlanBuilder.Build(_sources, _destination, SelectedPolicy(), VerifyBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not build the transfer: {ex.Message}";
            return;
        }

        if (plan.TotalFiles == 0)
        {
            StatusText.Text = "Nothing to copy — the selected folders are empty.";
            return;
        }

        SetRunning(true);
        ResultList.Items.Clear();
        _cts = new CancellationTokenSource();

        var progress = new Progress<CopyProgress>(p =>
        {
            Bar.Value = p.Fraction;
            var remaining = p.Remaining is { } r ? $"   {r:hh\\:mm\\:ss} left" : string.Empty;
            StatusText.Text =
                $"{p.FilesDone}/{p.FilesTotal} files   " +
                $"{PlanBuilder.FormatBytes(p.BytesDone)} of {PlanBuilder.FormatBytes(p.BytesTotal)}   " +
                $"{PlanBuilder.FormatBytes((long)p.BytesPerSecond)}/s{remaining}";
        });

        try
        {
            var report = await new CopyEngine().RunAsync(plan, progress, _cts.Token);
            ShowReport(report);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Transfer failed: {ex.Message}";
        }
        finally
        {
            SetRunning(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "Cancelling…";
    }

    private void ShowReport(TransferReport report)
    {
        foreach (var r in report.Results)
        {
            var name = Path.GetFileName(r.Item.SourcePath);
            var line = r.Outcome switch
            {
                ItemOutcome.Copied => $"  ok        {name}",
                ItemOutcome.Skipped => $"  skipped   {name}",
                ItemOutcome.Failed => $"  FAILED    {name}   {r.Error}",
                ItemOutcome.VerificationFailed => $"  UNVERIFIED {name}   {r.Error}",
                _ => name,
            };
            ResultList.Items.Add(line);
        }

        Bar.Value = report.WasCancelled ? Bar.Value : 1.0;

        StatusText.Text = report.WasCancelled
            ? $"Cancelled after {report.Copied} file(s). Nothing was left half-written."
            : report.IsClean
                ? $"Done. {report.Copied} file(s) copied and verified in {report.Duration.TotalSeconds:0.0}s."
                : $"Finished with problems — {report.Failed} failed, {report.Unverified} did not verify.";
    }

    private ConflictPolicy SelectedPolicy() => PolicyBox.SelectedIndex switch
    {
        1 => ConflictPolicy.Skip,
        2 => ConflictPolicy.Rename,
        3 => ConflictPolicy.Fail,
        _ => ConflictPolicy.Overwrite,
    };

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        AddFilesButton.IsEnabled = !running;
        AddFolderButton.IsEnabled = !running;
        ClearButton.IsEnabled = !running;
        DestButton.IsEnabled = !running;
        PolicyBox.IsEnabled = !running;
        VerifyBox.IsEnabled = !running;

        if (running)
        {
            Bar.Value = 0;
            StatusText.Text = "Starting…";
        }
    }
}
