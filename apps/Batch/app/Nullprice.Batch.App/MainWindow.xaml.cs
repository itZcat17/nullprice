using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Nullprice.Batch.Core;

namespace Nullprice.Batch.App;

/// <summary>
/// Scaffold shell: plain code-behind, no MVVM. Drives the Core pipeline and shows a live
/// preview of the resolved output names, so a name collision or a bad output folder
/// surfaces before anything is written.
/// </summary>
public partial class MainWindow : Window
{
    private readonly List<string> _sources = [];
    private readonly WicImageProcessor _processor = new();
    private Pipeline _pipeline = Pipeline.Empty;
    private string? _output;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        RefreshPipeline();
    }

    // ---- sources ----------------------------------------------------------

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var filter = "Images|" + string.Join(";", BatchPlanner.SupportedExtensions.Select(x => "*" + x));
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Add images", Filter = filter };
        if (dialog.ShowDialog(this) != true) return;

        _sources.AddRange(dialog.FileNames);
        RefreshSources();
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add a folder of images" };
        if (dialog.ShowDialog(this) != true) return;

        _sources.Add(dialog.FolderName);
        RefreshSources();
    }

    private void OnClearSources(object sender, RoutedEventArgs e)
    {
        _sources.Clear();
        RefreshSources();
    }

    private void OnChooseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where the results go" };
        if (dialog.ShowDialog(this) != true) return;

        _output = dialog.FolderName;
        OutputText.Text = _output;
        RefreshPreview();
    }

    private void RefreshSources()
    {
        SourceList.Items.Clear();
        foreach (var s in _sources) SourceList.Items.Add(s);
        RefreshPreview();
    }

    // ---- pipeline ---------------------------------------------------------

    private void OnAddResize(object sender, RoutedEventArgs e)
    {
        var dialog = new ResizeDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            _pipeline = _pipeline.With(dialog.Result);
            RefreshPipeline();
        }
    }

    private void OnAddConvert(object sender, RoutedEventArgs e)
    {
        var dialog = new ConvertDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            _pipeline = _pipeline.With(dialog.Result);
            RefreshPipeline();
        }
    }

    private void OnAddWatermark(object sender, RoutedEventArgs e)
    {
        var dialog = new WatermarkDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            _pipeline = _pipeline.With(dialog.Result);
            RefreshPipeline();
        }
    }

    private void OnAddStrip(object sender, RoutedEventArgs e)
    {
        _pipeline = _pipeline.With(new StripMetadataOperation());
        RefreshPipeline();
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int offset)
    {
        var index = PipelineList.SelectedIndex;
        if (index < 0 || _pipeline.IsEmpty) return;

        _pipeline = _pipeline.Move(index, offset);
        RefreshPipeline();
        PipelineList.SelectedIndex = Math.Clamp(index + offset, 0, Math.Max(0, _pipeline.Operations.Count - 1));
    }

    private void OnRemoveStep(object sender, RoutedEventArgs e)
    {
        var index = PipelineList.SelectedIndex;
        if (index < 0 || _pipeline.IsEmpty) return;

        _pipeline = _pipeline.Without(index);
        RefreshPipeline();
    }

    private void OnPipelineChanged(object sender, RoutedEventArgs e) => RefreshPreview();

    private void RefreshPipeline()
    {
        PipelineList.Items.Clear();

        if (_pipeline.IsEmpty)
        {
            PipelineList.Items.Add("(no steps — images would be re-encoded unchanged)");
        }
        else
        {
            var n = 1;
            foreach (var description in _pipeline.Describe())
                PipelineList.Items.Add($"{n++}.  {description}");
        }

        RefreshPreview();
    }

    // ---- preview ----------------------------------------------------------

    private void RefreshPreview()
    {
        // Called from the constructor's RefreshPipeline before the visual tree is ready.
        if (PreviewList is null || StatusText is null) return;

        PreviewList.Items.Clear();

        if (_sources.Count == 0 || string.IsNullOrEmpty(_output))
        {
            StatusText.Text = "Add images and choose an output folder.";
            return;
        }

        BatchPlan plan;
        try
        {
            plan = BuildPlan();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        foreach (var item in plan.Items.Take(5))
        {
            PreviewList.Items.Add(
                Path.GetFileName(item.SourcePath) + "   ->   " + Path.GetFileName(item.DestinationPath));
        }

        if (plan.Items.Count > 5)
            PreviewList.Items.Add($"… and {plan.Items.Count - 5} more");

        StatusText.Text = plan.Problems.Count > 0
            ? string.Join("   ", plan.Problems.Select(p => p.Message))
            : $"{plan.Total} image(s) ready. Nothing is written until you run it.";
    }

    private BatchPlan BuildPlan() => BatchPlanner.Build(
        _sources,
        _output!,
        _pipeline,
        TemplateBox.Text,
        _processor.Measure);

    // ---- run --------------------------------------------------------------

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (_sources.Count == 0 || string.IsNullOrEmpty(_output))
        {
            StatusText.Text = "Add images and choose an output folder first.";
            return;
        }

        BatchPlan plan;
        try
        {
            plan = BuildPlan();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        if (!plan.IsRunnable)
        {
            StatusText.Text = plan.Problems.Count > 0
                ? string.Join("   ", plan.Problems.Select(p => p.Message))
                : "Nothing to do.";
            return;
        }

        SetRunning(true);
        _cts = new CancellationTokenSource();

        var progress = new Progress<BatchProgress>(p =>
        {
            Bar.Value = p.Fraction;
            StatusText.Text = $"{p.Done}/{p.Total}   {Path.GetFileName(p.CurrentFile)}";
        });

        try
        {
            var report = await new BatchRunner(_processor).RunAsync(plan, progress, _cts.Token);
            ShowReport(report);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"The batch stopped: {ex.Message}";
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

    private void ShowReport(BatchReport report)
    {
        PreviewList.Items.Clear();

        foreach (var result in report.Results.Where(r => r.Outcome == BatchOutcome.Failed))
            PreviewList.Items.Add($"FAILED  {Path.GetFileName(result.Item.SourcePath)}   {result.Error}");

        if (report.Failed == 0)
            PreviewList.Items.Add($"All {report.Written} image(s) written.");

        Bar.Value = report.WasCancelled ? Bar.Value : 1.0;

        StatusText.Text = report.WasCancelled
            ? $"Cancelled after {report.Written} image(s). Originals untouched."
            : report.IsClean
                ? $"Done. {report.Written} image(s) in {report.Duration.TotalSeconds:0.0}s. Originals untouched."
                : $"Finished with {report.Failed} failure(s). {report.Written} written.";
    }

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_output) || !Directory.Exists(_output))
        {
            StatusText.Text = "No output folder to open yet.";
            return;
        }

        Process.Start(new ProcessStartInfo(_output) { UseShellExecute = true });
    }

    private void SetRunning(bool running)
    {
        RunButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        AddFilesButton.IsEnabled = !running;
        AddFolderButton.IsEnabled = !running;
        ClearButton.IsEnabled = !running;
        OutputButton.IsEnabled = !running;
        TemplateBox.IsEnabled = !running;
        PipelineList.IsEnabled = !running;

        if (running)
        {
            Bar.Value = 0;
            StatusText.Text = "Starting…";
        }
    }
}
