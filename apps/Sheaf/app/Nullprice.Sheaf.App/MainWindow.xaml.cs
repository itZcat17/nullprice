using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Nullprice.Sheaf.Core;

namespace Nullprice.Sheaf.App;

/// <summary>
/// Scaffold shell: plain code-behind, no MVVM. Drives the Core plan/runner and shows every
/// page as its own tile, since page-level rearrangement is the whole point of the tool.
/// </summary>
public partial class MainWindow : Window
{
    private readonly List<string> _sourcePaths = [];
    private readonly List<int> _sourcePageCounts = [];
    private readonly List<PageTile> _tiles = [];
    private readonly WindowsDataPdfRenderer _renderer = new();
    private string? _outputPath;
    private CancellationTokenSource? _cts;

    private sealed class PageTile
    {
        public int SourceGroupIndex;
        public int PageIndexInSource;
        public int RotationDegrees;
        public Image ThumbnailImage = null!;
        public Border Container = null!;
    }

    public MainWindow()
    {
        InitializeComponent();
    }

    // ---- sources ------------------------------------------------------------

    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "PDF files (*.pdf)|*.pdf", Multiselect = true };
        if (dialog.ShowDialog() != true) return;

        AddFilesButton.IsEnabled = false;
        try
        {
            foreach (var path in dialog.FileNames)
                await AddSourceAsync(path);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't read one of those PDFs: {ex.Message}";
        }
        finally
        {
            AddFilesButton.IsEnabled = true;
        }

        UpdateStatus();
    }

    private async Task AddSourceAsync(string path)
    {
        var pageCount = await _renderer.GetPageCountAsync(path, null, CancellationToken.None);
        var sourceIndex = _sourcePaths.Count;
        _sourcePaths.Add(path);
        _sourcePageCounts.Add(pageCount);

        var fileName = Path.GetFileName(path);
        for (var i = 0; i < pageCount; i++)
        {
            var rendered = await _renderer.RenderPageAsync(path, i, 72, null, CancellationToken.None);
            AddTile(sourceIndex, i, fileName, ToBitmapSource(rendered));
        }
    }

    private static BitmapSource ToBitmapSource(RenderedPage page)
    {
        var bitmap = BitmapSource.Create(
            page.PixelWidth, page.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, page.Bgra32Pixels, page.PixelWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        _tiles.Clear();
        _sourcePaths.Clear();
        _sourcePageCounts.Clear();
        PageGrid.Items.Clear();
        UpdateStatus();
    }

    // ---- page tiles -----------------------------------------------------------

    private void AddTile(int sourceIndex, int pageIndexInSource, string sourceFileName, BitmapSource thumbnail)
    {
        var image = new Image
        {
            Source = thumbnail,
            Width = 110,
            Stretch = Stretch.Uniform,
        };

        var label = new TextBlock
        {
            Text = $"{sourceFileName}  p{pageIndexInSource + 1}",
            FontSize = 10,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Foreground = (Brush)FindResource("InkSoft"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 4),
            MaxWidth = 118,
        };

        var tile = new PageTile
        {
            SourceGroupIndex = sourceIndex,
            PageIndexInSource = pageIndexInSource,
            ThumbnailImage = image,
        };

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttonRow.Children.Add(MakeSmallButton("◄", () => Rotate(tile, -90)));
        buttonRow.Children.Add(MakeSmallButton("►", () => Rotate(tile, 90)));
        buttonRow.Children.Add(MakeSmallButton("↑", () => Move(tile, -1)));
        buttonRow.Children.Add(MakeSmallButton("↓", () => Move(tile, 1)));
        buttonRow.Children.Add(MakeSmallButton("✕", () => Delete(tile)));

        var stack = new StackPanel { Margin = new Thickness(6) };
        stack.Children.Add(new Border
        {
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            Child = image,
        });
        stack.Children.Add(label);
        stack.Children.Add(buttonRow);

        var container = new Border { Padding = new Thickness(4), Child = stack };
        tile.Container = container;

        _tiles.Add(tile);
        PageGrid.Items.Add(container);
    }

    private Button MakeSmallButton(string content, Action onClick)
    {
        var button = new Button { Content = content, Style = (Style)FindResource("Small") };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void Rotate(PageTile tile, int delta)
    {
        tile.RotationDegrees = ((tile.RotationDegrees + delta) % 360 + 360) % 360;
        tile.ThumbnailImage.LayoutTransform = new RotateTransform(tile.RotationDegrees);
    }

    private void Move(PageTile tile, int delta)
    {
        var index = _tiles.IndexOf(tile);
        var newIndex = index + delta;
        if (newIndex < 0 || newIndex >= _tiles.Count) return;

        _tiles.RemoveAt(index);
        _tiles.Insert(newIndex, tile);
        PageGrid.Items.RemoveAt(index);
        PageGrid.Items.Insert(newIndex, tile.Container);
    }

    private void Delete(PageTile tile)
    {
        _tiles.Remove(tile);
        PageGrid.Items.Remove(tile.Container);
        UpdateStatus();
    }

    // ---- output / run -----------------------------------------------------------

    private void OnChooseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "PDF file (*.pdf)|*.pdf", FileName = "output.pdf" };
        if (dialog.ShowDialog() != true) return;

        _outputPath = dialog.FileName;
        OutputText.Text = _outputPath;
    }

    /// <summary>
    /// Maps the page grid's current visual order back onto Core's plan model: every source
    /// contributes all of its pages to the base working list in file order (each
    /// <see cref="MergeSource"/> below leaves PageIndices null), and a single
    /// <see cref="ReorderOperation"/> re-expresses the tiles' on-screen order as indices into
    /// that concatenation — a tile the user deleted is simply absent from the mapping, which
    /// is how deletion is expressed rather than a separate per-page delete operation.
    /// </summary>
    private async void OnBuild(object sender, RoutedEventArgs e)
    {
        if (_tiles.Count == 0) { StatusText.Text = "Add at least one page first."; return; }
        if (string.IsNullOrEmpty(_outputPath)) { StatusText.Text = "Choose an output file first."; return; }

        var offsets = new int[_sourcePageCounts.Count];
        var running = 0;
        for (var i = 0; i < _sourcePageCounts.Count; i++)
        {
            offsets[i] = running;
            running += _sourcePageCounts[i];
        }

        var newOrder = _tiles.Select(t => offsets[t.SourceGroupIndex] + t.PageIndexInSource).ToList();
        var operations = new List<PageOperation> { new ReorderOperation(newOrder) };
        for (var i = 0; i < _tiles.Count; i++)
        {
            if (_tiles[i].RotationDegrees != 0)
                operations.Add(new RotateOperation(i, _tiles[i].RotationDegrees));
        }

        var sources = _sourcePaths.Select(p => new MergeSource(p)).ToList();
        var outputs = new List<SheafOutput> { new(_outputPath, operations) };
        var plan = SheafPlanner.Build(sources, outputs);

        if (!plan.IsRunnable)
        {
            StatusText.Text = string.Join(" ", plan.Problems.Select(p => p.Message));
            return;
        }

        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Bar.Value = 0;
        _cts = new CancellationTokenSource();

        var progress = new Progress<SheafProgress>(p =>
        {
            Bar.Value = p.Fraction;
            StatusText.Text = $"Writing {p.CurrentOutput}…";
        });

        try
        {
            var report = await new SheafRunner().RunAsync(plan, progress, _cts.Token);
            StatusText.Text = report.WasCancelled
                ? "Cancelled."
                : report.IsClean
                    ? $"Wrote {_outputPath}."
                    : string.Join(" ", report.Results.Where(r => r.Outcome == SheafOutcome.Failed).Select(r => r.Error));
        }
        finally
        {
            RunButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _cts = null;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_outputPath) || !File.Exists(_outputPath)) return;
        Process.Start(new ProcessStartInfo(_outputPath) { UseShellExecute = true });
    }

    private void UpdateStatus()
    {
        StatusText.Text = _tiles.Count == 0
            ? "Add one or more PDFs, arrange pages, and choose an output file."
            : $"{_tiles.Count} page(s) from {_sourcePaths.Count} file(s) ready.";
    }
}
