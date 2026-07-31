using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nullprice.Sheaf.Core;

namespace Nullprice.Sheaf.App;

/// <summary>
/// Renders one page at a fixed working DPI and lets the user click a line of text to edit it
/// in place. Deliberately built as a general "page canvas" rather than a text-edit-only
/// dialog: the coordinate mapping (screen pixels &lt;-&gt; PDF user-space) and render/click
/// plumbing here is exactly what shape/ink/highlight tools will need too once they exist,
/// so this is the shared foundation for the whole "click or drag on the page" family of
/// features, not a one-off.
/// </summary>
public partial class PageEditorWindow : Window
{
    private const double Dpi = 150;

    private readonly string _pdfPath;
    private readonly int _pageIndex;
    private readonly string? _password;
    private readonly List<TextEdit> _pendingEdits = [];
    private readonly WpfGlyphFontLoader _glyphFontLoader = new();

    private PdfDocument? _doc;
    private byte[]? _currentContent;
    private double _mediaBoxX0, _mediaBoxY0, _mediaBoxHeight;
    private TextRunLocation? _editingRun;

    public IReadOnlyList<TextEdit> PendingEdits => _pendingEdits;

    public PageEditorWindow(string pdfPath, int pageIndex, string? password = null)
    {
        InitializeComponent();
        _pdfPath = pdfPath;
        _pageIndex = pageIndex;
        _password = password;
        Loaded += async (_, _) => await LoadPageAsync();
    }

    private async Task LoadPageAsync()
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(_pdfPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Couldn't read this file: {ex.Message}";
            return;
        }

        var opened = PdfDocument.Open(bytes, _password);
        if (opened.Status != PdfOpenStatus.Success || opened.Document is null)
        {
            StatusText.Text = opened.Message ?? "Could not open this PDF for editing.";
            return;
        }

        _doc = opened.Document;
        if (_pageIndex < 0 || _pageIndex >= _doc.Pages.Count)
        {
            StatusText.Text = "That page no longer exists.";
            return;
        }

        var page = _doc.Pages[_pageIndex];
        var mediaBox = _doc.Objects.Resolve(page.Dictionary.Get("MediaBox")) as PdfArray;
        var box = mediaBox?.Items.Select(i => (_doc.Objects.Resolve(i) as PdfNumber)?.Value ?? 0).ToArray() ?? [0, 0, 612, 792];
        _mediaBoxX0 = box[0];
        _mediaBoxY0 = box[1];
        _mediaBoxHeight = box[3] - box[1];

        _currentContent = ContentStreamCombiner.Combine(_doc.Objects, page.Dictionary.Get("Contents"));

        var renderer = new WindowsDataPdfRenderer();
        var rendered = await renderer.RenderPageAsync(_pdfPath, _pageIndex, Dpi, _password, CancellationToken.None);

        var bitmap = BitmapSource.Create(
            rendered.PixelWidth, rendered.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, rendered.Bgra32Pixels, rendered.PixelWidth * 4);
        bitmap.Freeze();

        PageImage.Source = bitmap;
        PageImage.Width = rendered.PixelWidth;
        PageImage.Height = rendered.PixelHeight;
        PageCanvas.Width = rendered.PixelWidth;
        PageCanvas.Height = rendered.PixelHeight;

        StatusText.Text = "No edits queued yet.";
    }

    private void OnPageClicked(object sender, MouseButtonEventArgs e)
    {
        CommitPendingEditBoxIfAny();
        if (_doc is null || _currentContent is null) return;

        var pos = e.GetPosition(PageImage);
        var (pdfX, pdfY) = ToPdfSpace(pos.X, pos.Y);

        var found = ContentStreamTextEditor.FindTextAt(_currentContent, pdfX, pdfY);
        if (found is null) return;

        _editingRun = found;
        EditBox.Text = found.Text;

        var (screenX, screenY) = ToScreenSpace(found.X, found.Y);
        var fontSizePixels = Math.Max(10, found.FontSize * (Dpi / 72.0));
        EditBox.FontSize = fontSizePixels;
        EditBox.MinWidth = 60;
        EditBox.HorizontalAlignment = HorizontalAlignment.Left;
        EditBox.VerticalAlignment = VerticalAlignment.Top;
        EditBox.Margin = new Thickness(screenX, screenY - fontSizePixels, 0, 0);
        EditBox.Visibility = Visibility.Visible;
        EditBox.Focus();
        EditBox.SelectAll();
    }

    private void OnEditBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitPendingEditBoxIfAny();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EditBox.Visibility = Visibility.Collapsed;
            _editingRun = null;
            e.Handled = true;
        }
    }

    private void OnEditBoxLostFocus(object sender, RoutedEventArgs e) => CommitPendingEditBoxIfAny();

    private void CommitPendingEditBoxIfAny()
    {
        if (_editingRun is null || EditBox.Visibility != Visibility.Visible) return;

        var run = _editingRun;
        var newText = EditBox.Text;
        EditBox.Visibility = Visibility.Collapsed;
        _editingRun = null;

        if (newText == run.Text || _doc is null || run.FontResourceName is null) return;

        var pageDict = _doc.Pages[_pageIndex].Dictionary;
        var fontDict = EmbeddedFontExtractor.ResolveFontResource(_doc.Objects, pageDict, run.FontResourceName);
        if (fontDict is null)
        {
            StatusText.Text = "Couldn't resolve this text's font.";
            return;
        }

        var extracted = EmbeddedFontExtractor.Extract(_doc.Objects, fontDict);
        if (extracted is null)
        {
            StatusText.Text = "This isn't a simple embedded TrueType font — in-place editing isn't supported for it.";
            return;
        }

        var glyphFont = _glyphFontLoader.Load(extracted.FontFileBytes);
        if (glyphFont is not null)
        {
            var problem = TextEditPlanner.Validate(glyphFont, newText);
            if (problem is not null)
            {
                StatusText.Text = problem.Message;
                return;
            }
        }

        _pendingEdits.Add(new TextEdit(_pageIndex, run.OperatorIndex, newText, run.FontResourceName));
        StatusText.Text = glyphFont is null
            ? $"{_pendingEdits.Count} edit(s) queued (font coverage couldn't be double-checked)."
            : $"{_pendingEdits.Count} edit(s) queued.";
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        CommitPendingEditBoxIfAny();
        DialogResult = true;
        Close();
    }

    private (double X, double Y) ToPdfSpace(double pixelX, double pixelY)
    {
        var scale = 72.0 / Dpi;
        return (_mediaBoxX0 + pixelX * scale, _mediaBoxY0 + _mediaBoxHeight - pixelY * scale);
    }

    private (double X, double Y) ToScreenSpace(double pdfX, double pdfY)
    {
        var scale = Dpi / 72.0;
        return ((pdfX - _mediaBoxX0) * scale, (_mediaBoxY0 + _mediaBoxHeight - pdfY) * scale);
    }
}
