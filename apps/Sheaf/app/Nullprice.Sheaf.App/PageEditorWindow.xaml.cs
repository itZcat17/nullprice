using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Nullprice.Sheaf.Core;

namespace Nullprice.Sheaf.App;

/// <summary>
/// Renders one page at a fixed working DPI and drives every "click or drag on the page" tool:
/// in-place text editing (M5) plus the M7 markup tools (highlight/underline/strikethrough,
/// line/rectangle/ellipse shapes, freehand ink, sticky notes) built on M6's
/// <see cref="AnnotationEdit"/>/<see cref="AnnotationWriter"/> foundation. One shared
/// screen&lt;-&gt;PDF-space coordinate mapping and canvas serves every tool, per the plan's
/// original "general page canvas, not a text-only dialog" design.
///
/// Interaction model, per tool: region-based markup (Highlight/Underline/StrikeOut/Rectangle/
/// Ellipse) and Line/Arrow use a single mouse-down-drag-up gesture — consistent with
/// redaction's existing box-drawing UX. Ink captures every point of the drag as one stroke
/// (continuous point capture, since a box can't represent freehand). Sticky notes are a single
/// click that opens an inline text box for the note's contents.
/// </summary>
public partial class PageEditorWindow : Window
{
    private const double Dpi = 150;

    private enum Tool { EditText, Highlight, Underline, Strikeout, Line, Arrow, Rectangle, Ellipse, Ink, StickyNote, NewText }

    private readonly string _pdfPath;
    private readonly int _pageIndex;
    private readonly string? _password;
    private readonly List<TextEdit> _pendingEdits = [];
    private readonly List<AnnotationEdit> _pendingAnnotations = [];
    private readonly WpfGlyphFontLoader _glyphFontLoader = new();

    private PdfDocument? _doc;
    private byte[]? _currentContent;
    private double _mediaBoxX0, _mediaBoxY0, _mediaBoxHeight;
    private TextRunLocation? _editingRun;

    private Tool _tool = Tool.EditText;
    private Point? _dragStart;
    private Shape? _previewShape;
    private Polyline? _inkPreview;
    private readonly List<(double X, double Y)> _inkPoints = [];
    private TextBox? _noteBox;
    private double _noteBoxPdfX, _noteBoxPdfY;
    private TextBox? _newTextBox;
    private double _newTextPdfX, _newTextPdfY;

    public IReadOnlyList<TextEdit> PendingEdits => _pendingEdits;
    public IReadOnlyList<AnnotationEdit> PendingAnnotations => _pendingAnnotations;

    public PageEditorWindow(string pdfPath, int pageIndex, string? password = null)
    {
        InitializeComponent();
        _pdfPath = pdfPath;
        _pageIndex = pageIndex;
        _password = password;
        FontPicker.ItemsSource = Fonts.SystemFontFamilies.OrderBy(f => f.Source, StringComparer.OrdinalIgnoreCase).ToList();
        FontPicker.SelectedIndex = 0;
        EditTextTool.IsChecked = true; // set after InitializeComponent so OnToolChanged can safely touch every named element
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

    // ---- tool selection -------------------------------------------------------------------

    private void OnToolChanged(object sender, RoutedEventArgs e)
    {
        _tool = sender switch
        {
            _ when ReferenceEquals(sender, EditTextTool) => Tool.EditText,
            _ when ReferenceEquals(sender, HighlightTool) => Tool.Highlight,
            _ when ReferenceEquals(sender, UnderlineTool) => Tool.Underline,
            _ when ReferenceEquals(sender, StrikeoutTool) => Tool.Strikeout,
            _ when ReferenceEquals(sender, LineTool) => Tool.Line,
            _ when ReferenceEquals(sender, ArrowTool) => Tool.Arrow,
            _ when ReferenceEquals(sender, RectangleTool) => Tool.Rectangle,
            _ when ReferenceEquals(sender, EllipseTool) => Tool.Ellipse,
            _ when ReferenceEquals(sender, InkTool) => Tool.Ink,
            _ when ReferenceEquals(sender, StickyNoteTool) => Tool.StickyNote,
            _ when ReferenceEquals(sender, NewTextTool) => Tool.NewText,
            _ => _tool,
        };

        ToolHint.Text = _tool switch
        {
            Tool.EditText => "Click a line of text on the page to edit it in place.",
            Tool.Highlight or Tool.Underline or Tool.Strikeout => "Drag a box over text to mark it.",
            Tool.Line or Tool.Arrow => "Drag to draw a line.",
            Tool.Rectangle or Tool.Ellipse => "Drag to draw a shape.",
            Tool.Ink => "Drag to draw freehand.",
            Tool.StickyNote => "Click to place a note.",
            Tool.NewText => "Click to place new text in the chosen font.",
            _ => "",
        };
    }

    // ---- text editing (M5) -----------------------------------------------------------------

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_tool == Tool.EditText) { OnPageClicked(sender, e); return; }
        CommitPendingEditBoxIfAny();
        if (_doc is null) return;

        var pos = e.GetPosition(PageImage);

        if (_tool == Tool.StickyNote)
        {
            BeginStickyNote(pos);
            return;
        }

        if (_tool == Tool.NewText)
        {
            BeginNewText(pos);
            return;
        }

        PageImage.CaptureMouse();
        _dragStart = pos;

        if (_tool == Tool.Ink)
        {
            _inkPoints.Clear();
            var (px, py) = ToPdfSpace(pos.X, pos.Y);
            _inkPoints.Add((px, py));
            _inkPreview = new Polyline { Stroke = CurrentBrush(), StrokeThickness = CurrentWidth(), IsHitTestVisible = false };
            _inkPreview.Points.Add(pos);
            PageCanvas.Children.Add(_inkPreview);
        }
        else
        {
            _previewShape = CreatePreviewShape();
            PageCanvas.Children.Add(_previewShape);
            PositionPreview(pos, pos);
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is null) return;
        var pos = e.GetPosition(PageImage);

        if (_tool == Tool.Ink)
        {
            var (px, py) = ToPdfSpace(pos.X, pos.Y);
            _inkPoints.Add((px, py));
            _inkPreview?.Points.Add(pos);
        }
        else
        {
            PositionPreview(_dragStart.Value, pos);
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        PageImage.ReleaseMouseCapture();
        var start = _dragStart.Value;
        var end = e.GetPosition(PageImage);
        _dragStart = null;

        if (_tool == Tool.Ink)
        {
            if (_inkPreview is not null) PageCanvas.Children.Remove(_inkPreview);
            _inkPreview = null;

            if (_inkPoints.Count >= 2)
            {
                _pendingAnnotations.Add(new InkEdit(_pageIndex, [_inkPoints.ToList()], CurrentColorHex(), CurrentWidth()));
                StatusText.Text = $"{_pendingAnnotations.Count} markup(s) queued.";
            }
            _inkPoints.Clear();
            return;
        }

        if (_previewShape is not null) PageCanvas.Children.Remove(_previewShape);
        _previewShape = null;

        if (Math.Abs(end.X - start.X) < 3 && Math.Abs(end.Y - start.Y) < 3) return; // ignore an accidental click

        var (x1, y1) = ToPdfSpace(start.X, start.Y);
        var (x2, y2) = ToPdfSpace(end.X, end.Y);
        var x = Math.Min(x1, x2);
        var y = Math.Min(y1, y2);
        var w = Math.Abs(x2 - x1);
        var h = Math.Abs(y2 - y1);

        AnnotationEdit? edit = _tool switch
        {
            Tool.Highlight => new HighlightEdit(_pageIndex, x, y, w, h, CurrentColorHex()),
            Tool.Underline => new UnderlineEdit(_pageIndex, x, y, w, h, CurrentColorHex()),
            Tool.Strikeout => new StrikeOutEdit(_pageIndex, x, y, w, h, CurrentColorHex()),
            Tool.Rectangle => new RectShapeEdit(_pageIndex, x, y, w, h, CurrentColorHex(), CurrentWidth(), null),
            Tool.Ellipse => new EllipseShapeEdit(_pageIndex, x, y, w, h, CurrentColorHex(), CurrentWidth(), null),
            Tool.Line => new LineShapeEdit(_pageIndex, x1, y1, x2, y2, CurrentColorHex(), CurrentWidth(), Arrow: false),
            Tool.Arrow => new LineShapeEdit(_pageIndex, x1, y1, x2, y2, CurrentColorHex(), CurrentWidth(), Arrow: true),
            _ => null,
        };

        if (edit is null) return;
        _pendingAnnotations.Add(edit);
        StatusText.Text = $"{_pendingAnnotations.Count} markup(s) queued.";
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

    // ---- markup preview shapes --------------------------------------------------------------

    private Shape CreatePreviewShape()
    {
        var brush = CurrentBrush();
        Shape shape = _tool switch
        {
            Tool.Line or Tool.Arrow => new Line { Stroke = brush, StrokeThickness = CurrentWidth(), Stretch = Stretch.None },
            Tool.Ellipse => new Ellipse { Stroke = brush, StrokeThickness = 2 },
            Tool.Highlight => new Rectangle { Fill = new SolidColorBrush(CurrentColor()) { Opacity = 0.35 } },
            _ => new Rectangle { Stroke = brush, StrokeThickness = 2 },
        };
        shape.HorizontalAlignment = HorizontalAlignment.Left;
        shape.VerticalAlignment = VerticalAlignment.Top;
        shape.IsHitTestVisible = false;
        return shape;
    }

    private void PositionPreview(Point a, Point b)
    {
        if (_previewShape is Line line)
        {
            line.X1 = a.X; line.Y1 = a.Y; line.X2 = b.X; line.Y2 = b.Y;
            return;
        }
        if (_previewShape is null) return;

        _previewShape.Margin = new Thickness(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), 0, 0);
        _previewShape.Width = Math.Abs(b.X - a.X);
        _previewShape.Height = Math.Abs(b.Y - a.Y);
    }

    // ---- sticky notes -----------------------------------------------------------------------

    private void BeginStickyNote(Point pos)
    {
        if (_noteBox is not null) return;

        var (px, py) = ToPdfSpace(pos.X, pos.Y);
        _noteBoxPdfX = px;
        _noteBoxPdfY = py;

        _noteBox = new TextBox
        {
            Width = 220,
            Height = 70,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.LightYellow,
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(pos.X, pos.Y, 0, 0),
        };
        _noteBox.LostFocus += (_, _) => CommitStickyNote();
        _noteBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            PageCanvas.Children.Remove(_noteBox);
            _noteBox = null;
            e.Handled = true;
        };

        PageCanvas.Children.Add(_noteBox);
        _noteBox.Focus();
    }

    private void CommitStickyNote()
    {
        if (_noteBox is null) return;
        var text = _noteBox.Text;
        PageCanvas.Children.Remove(_noteBox);
        _noteBox = null;

        if (string.IsNullOrWhiteSpace(text)) return;
        _pendingAnnotations.Add(new StickyNoteEdit(_pageIndex, _noteBoxPdfX, _noteBoxPdfY, text));
        StatusText.Text = $"{_pendingAnnotations.Count} markup(s) queued.";
    }

    // ---- new text (M9) ----------------------------------------------------------------------

    private void BeginNewText(Point pos)
    {
        if (_newTextBox is not null) return;

        var (px, py) = ToPdfSpace(pos.X, pos.Y);
        _newTextPdfX = px;
        _newTextPdfY = py;

        var fontSizePixels = Math.Max(10, FontSizeSlider.Value * (Dpi / 72.0));
        _newTextBox = new TextBox
        {
            MinWidth = 60,
            AcceptsReturn = false, // single-line only in v1, consistent with M5's in-place edit boundary
            FontFamily = FontPicker.SelectedItem as FontFamily,
            FontSize = fontSizePixels,
            Padding = new Thickness(2),
            Background = Brushes.White,
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(pos.X, pos.Y - fontSizePixels, 0, 0),
        };
        _newTextBox.LostFocus += (_, _) => CommitNewText();
        _newTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitNewText(); e.Handled = true; }
            else if (e.Key == Key.Escape)
            {
                PageCanvas.Children.Remove(_newTextBox);
                _newTextBox = null;
                e.Handled = true;
            }
        };

        PageCanvas.Children.Add(_newTextBox);
        _newTextBox.Focus();
    }

    private void CommitNewText()
    {
        if (_newTextBox is null) return;
        var text = _newTextBox.Text;
        var fontFamily = _newTextBox.FontFamily;
        var fontSizePt = FontSizeSlider.Value;
        PageCanvas.Children.Remove(_newTextBox);
        _newTextBox = null;

        if (string.IsNullOrEmpty(text) || fontFamily is null) return;

        var (fontBytes, font, familyName, problem) = ResolveSystemFont(fontFamily);
        if (problem is not null || font is null)
        {
            StatusText.Text = problem ?? "This font isn't supported for embedding.";
            return;
        }

        var missing = text.Distinct().Where(c => !font.TryGetGlyphId(c, out _)).ToList();
        if (missing.Count > 0)
        {
            StatusText.Text = $"This font doesn't include: {string.Join(' ', missing)}";
            return;
        }

        _pendingAnnotations.Add(new FreeTextEdit(
            _pageIndex, _newTextPdfX, _newTextPdfY, fontSizePt, text, CurrentColorHex(), fontBytes!, familyName!));
        StatusText.Text = $"{_pendingAnnotations.Count} markup(s) queued.";
    }

    /// <summary>Resolves a WPF <see cref="FontFamily"/> chosen from <c>Fonts.SystemFontFamilies</c>
    /// down to the actual font file bytes <see cref="TrueTypeSubsetter"/> needs — WPF has no
    /// in-memory font-file API, so this reads <see cref="GlyphTypeface.FontUri"/>'s local file
    /// path directly, the same "read the real file WPF itself resolved" approach
    /// <see cref="WpfGlyphFontLoader"/> uses in reverse (there, an embedded font's bytes are
    /// written to a temp file for WPF to load; here, a file WPF already resolved is read back).
    /// A TrueType Collection face is addressed via a <c>#N</c> URI fragment.</summary>
    private static (byte[]? FontBytes, TrueTypeFont? Font, string? FamilyName, string? Problem) ResolveSystemFont(FontFamily fontFamily)
    {
        var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var glyphTypeface))
            return (null, null, null, "Couldn't resolve this font to an actual font file.");

        if (glyphTypeface.StyleSimulations != StyleSimulations.None)
            return (null, null, null, "This font's regular weight/style isn't actually installed (Windows would fake it), so it can't be embedded accurately.");

        var uri = glyphTypeface.FontUri;
        var ttcIndex = 0;
        if (!string.IsNullOrEmpty(uri.Fragment) && int.TryParse(uri.Fragment.TrimStart('#'), out var idx)) ttcIndex = idx;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(uri.LocalPath);
        }
        catch (Exception ex)
        {
            return (null, null, null, $"Couldn't read this font's file: {ex.Message}");
        }

        var parsed = TrueTypeFont.Parse(bytes, ttcIndex);
        return parsed.Font is null
            ? (null, null, null, parsed.Message ?? "This font isn't supported for embedding.")
            : (bytes, parsed.Font, fontFamily.Source, null);
    }

    // ---- shared color/width readers -----------------------------------------------------------

    private Color CurrentColor() => (Color)ColorConverter.ConvertFromString(CurrentColorHex());

    private string CurrentColorHex() => (ColorPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "#FFE600";

    private double CurrentWidth() => WidthSlider.Value;

    private Brush CurrentBrush() => new SolidColorBrush(CurrentColor());

    // ---- done -----------------------------------------------------------------------------

    private void OnDone(object sender, RoutedEventArgs e)
    {
        CommitPendingEditBoxIfAny();
        CommitStickyNote();
        CommitNewText();
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
