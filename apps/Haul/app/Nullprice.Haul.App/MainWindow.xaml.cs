using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
using Nullprice.Haul.Core;

namespace Nullprice.Haul.App;

/// <summary>
/// M1 shell: probe a URL for a directly-downloadable format, then download it. Format choice,
/// trim, speed, and YouTube support land in later milestones — this exercises the plan/runner
/// shape end to end on the generic direct-media-URL path first.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly HttpClient HttpClient = new();
    private readonly GenericMediaProber _prober = new(HttpClient);

    private ProbeResult? _probed;
    private string? _probedUrl;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void ProbeButton_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url.Length == 0) return;

        ProbeButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        _probed = null;
        _probedUrl = null;
        StatusText.Text = "Probing...";
        ResultText.Text = string.Empty;

        try
        {
            var result = await _prober.ProbeAsync(url, CancellationToken.None);

            if (result.Formats.Count == 0)
            {
                ResultText.Text = result.SkippedReasons.Count > 0
                    ? result.SkippedReasons[0]
                    : "Nothing downloadable was found at that link.";
                StatusText.Text = "Ready.";
                return;
            }

            _probed = result;
            _probedUrl = url;

            var format = result.Formats[0];
            var title = string.IsNullOrWhiteSpace(result.Title) ? url : result.Title;
            ResultText.Text = $"\"{title}\" — {format.Container.ToUpperInvariant()} ({format.CodecLabel}).";
            DownloadButton.IsEnabled = true;
            StatusText.Text = "Ready to download.";
        }
        catch (Exception ex)
        {
            ResultText.Text = "Could not read that link: " + ex.Message;
            StatusText.Text = "Ready.";
        }
        finally
        {
            ProbeButton.IsEnabled = true;
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunDownloadAsync();
        }
        catch (Exception ex)
        {
            // async void handlers propagate uncaught exceptions to the Dispatcher, which
            // otherwise crashes the whole app rather than just this one action.
            StatusText.Text = "Something went wrong: " + ex.Message;
            ProbeButton.IsEnabled = true;
            DownloadButton.IsEnabled = true;
        }
    }

    private async Task RunDownloadAsync()
    {
        if (_probed is null || _probedUrl is null) return;

        var format = _probed.Formats[0];
        var title = string.IsNullOrWhiteSpace(_probed.Title) ? "haul-download" : _probed.Title;
        var suggestedName = SanitizeFileName(title) + "." + format.Container;

        var dialog = new SaveFileDialog
        {
            FileName = suggestedName,
            Filter = $"{format.Container.ToUpperInvariant()} file|*.{format.Container}|All files|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;

        var job = new HaulJob(_probedUrl, format, dialog.FileName, null, null);
        var plan = HaulPlanner.Build([job]);

        if (!plan.IsRunnable)
        {
            StatusText.Text = plan.Problems.Count > 0 ? plan.Problems[0].Message : "Could not build a plan.";
            return;
        }

        ProbeButton.IsEnabled = false;
        DownloadButton.IsEnabled = false;
        DownloadProgress.Value = 0;
        StatusText.Text = "Downloading...";

        var progress = new Progress<HaulProgress>(p =>
        {
            var doneMb = p.BytesDone / 1024.0 / 1024.0;

            if (p.BytesTotal is > 0)
            {
                DownloadProgress.Value = (double)p.BytesDone / p.BytesTotal.Value;
                StatusText.Text = $"Downloading... {doneMb:0.0} / {p.BytesTotal.Value / 1024.0 / 1024.0:0.0} MB";
            }
            else
            {
                StatusText.Text = $"Downloading... {doneMb:0.0} MB";
            }
        });

        try
        {
            var report = await new HaulRunner(HttpClient).RunAsync(plan, progress);
            StatusText.Text = report.IsClean
                ? "Saved."
                : report.Results.FirstOrDefault(r => r.Outcome == HaulOutcome.Failed)?.Error ?? "Download failed.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Download failed: " + ex.Message;
        }
        finally
        {
            ProbeButton.IsEnabled = true;
            DownloadButton.IsEnabled = true;
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
