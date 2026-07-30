using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Nullprice.Batch.Core;

namespace Nullprice.Batch.App;

/// <summary>
/// The three operation dialogs, built in code rather than XAML.
/// <para>
/// They are small, near-identical in structure, and entirely mechanical — three more
/// .xaml/.xaml.cs pairs would be more files to keep in sync for no benefit. Each one only
/// collects a handful of values and returns a Core operation record.
/// </para>
/// </summary>
internal abstract class OperationDialog<T> : Window where T : ImageOperation
{
    public T? Result { get; protected set; }

    protected readonly StackPanel Body = new();

    protected OperationDialog(string title, double width = 380)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = System.Windows.ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xEE, 0xF0, 0xF1));
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
    }

    /// <summary>Call once subclasses have filled <see cref="Body"/>.</summary>
    protected void Compose()
    {
        var ok = new Button { Content = "Add step", Padding = new Thickness(16, 7, 16, 7), IsDefault = true };
        ok.Click += (_, _) =>
        {
            if (!TryBuild(out var problem))
            {
                MessageBox.Show(this, problem, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DialogResult = true;
        };

        var cancel = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        Body.Children.Add(buttons);
        Content = new Border { Padding = new Thickness(20), Child = Body };
    }

    /// <summary>Sets <see cref="Result"/>, or explains what the user needs to fix.</summary>
    protected abstract bool TryBuild(out string problem);

    protected TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
        Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x5A, 0x65, 0x6C)),
        Margin = new Thickness(0, 10, 0, 4),
    };

    protected static TextBox Field(string initial) => new()
    {
        Text = initial,
        Padding = new Thickness(7, 5, 7, 5),
        FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
    };

    protected static bool TryPositiveInt(TextBox box, out int value) =>
        int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value > 0;
}

internal sealed class ResizeDialog : OperationDialog<ResizeOperation>
{
    private readonly ComboBox _mode = new() { Padding = new Thickness(6, 4, 6, 4) };
    private readonly TextBox _width = Field("1920");
    private readonly TextBox _height = Field("1080");
    private readonly TextBox _percent = Field("50");

    public ResizeDialog() : base("Resize")
    {
        foreach (var name in new[]
        {
            "Fit inside the box (keeps aspect)",
            "Fill the box and crop (keeps aspect)",
            "Exactly these dimensions (may distort)",
            "Percentage of the original",
        })
        {
            _mode.Items.Add(name);
        }
        _mode.SelectedIndex = 0;

        Body.Children.Add(Label("MODE"));
        Body.Children.Add(_mode);

        Body.Children.Add(Label("WIDTH / HEIGHT IN PIXELS"));
        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_width, 0);
        Grid.SetColumn(_height, 2);
        pair.Children.Add(_width);
        pair.Children.Add(_height);
        Body.Children.Add(pair);

        Body.Children.Add(Label("PERCENTAGE (ONLY FOR PERCENTAGE MODE)"));
        Body.Children.Add(_percent);

        Compose();
    }

    protected override bool TryBuild(out string problem)
    {
        problem = string.Empty;

        var mode = _mode.SelectedIndex switch
        {
            1 => ResizeStrategy.Fill,
            2 => ResizeStrategy.Exact,
            3 => ResizeStrategy.Percent,
            _ => ResizeStrategy.Fit,
        };

        if (mode == ResizeStrategy.Percent)
        {
            if (!double.TryParse(_percent.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var pct) || pct <= 0)
            {
                problem = "Enter a percentage greater than zero.";
                return false;
            }

            Result = new ResizeOperation(mode, 0, 0, pct);
            return true;
        }

        if (!TryPositiveInt(_width, out var w) || !TryPositiveInt(_height, out var h))
        {
            problem = "Enter a width and height of at least one pixel.";
            return false;
        }

        Result = new ResizeOperation(mode, w, h);
        return true;
    }
}

internal sealed class ConvertDialog : OperationDialog<ConvertOperation>
{
    private readonly ComboBox _format = new() { Padding = new Thickness(6, 4, 6, 4) };
    private readonly Slider _quality = new()
    {
        Minimum = 1, Maximum = 100, Value = 85,
        IsSnapToTickEnabled = true, TickFrequency = 1,
    };
    private readonly TextBlock _qualityValue = new() { Margin = new Thickness(0, 4, 0, 0) };

    public ConvertDialog() : base("Convert")
    {
        foreach (var name in new[] { "JPEG", "PNG", "BMP", "TIFF" }) _format.Items.Add(name);
        _format.SelectedIndex = 0;
        _format.SelectionChanged += (_, _) => UpdateQualityState();

        Body.Children.Add(Label("FORMAT"));
        Body.Children.Add(_format);

        Body.Children.Add(Label("JPEG QUALITY"));
        Body.Children.Add(_quality);
        Body.Children.Add(_qualityValue);

        _quality.ValueChanged += (_, _) => UpdateQualityState();
        UpdateQualityState();

        Compose();
    }

    private void UpdateQualityState()
    {
        var isJpeg = _format.SelectedIndex == 0;
        _quality.IsEnabled = isJpeg;
        _qualityValue.Text = isJpeg
            ? $"{(int)_quality.Value}  —  85 is a good default"
            : "Not applicable: this format is lossless.";
    }

    protected override bool TryBuild(out string problem)
    {
        problem = string.Empty;

        var format = _format.SelectedIndex switch
        {
            1 => Core.ImageFormat.Png,
            2 => Core.ImageFormat.Bmp,
            3 => Core.ImageFormat.Tiff,
            _ => Core.ImageFormat.Jpeg,
        };

        Result = new ConvertOperation(format, (int)_quality.Value);
        return true;
    }
}

internal sealed class WatermarkDialog : OperationDialog<WatermarkOperation>
{
    private readonly TextBox _text = Field("");
    private readonly ComboBox _corner = new() { Padding = new Thickness(6, 4, 6, 4) };
    private readonly Slider _opacity = new()
    {
        Minimum = 0.05, Maximum = 1.0, Value = 0.55,
        TickFrequency = 0.05, IsSnapToTickEnabled = true,
    };
    private readonly TextBlock _opacityValue = new() { Margin = new Thickness(0, 4, 0, 0) };

    public WatermarkDialog() : base("Watermark")
    {
        foreach (var name in new[] { "Bottom right", "Bottom left", "Top right", "Top left", "Centre" })
            _corner.Items.Add(name);
        _corner.SelectedIndex = 0;

        Body.Children.Add(Label("TEXT"));
        Body.Children.Add(_text);

        Body.Children.Add(Label("POSITION"));
        Body.Children.Add(_corner);

        Body.Children.Add(Label("OPACITY"));
        Body.Children.Add(_opacity);
        Body.Children.Add(_opacityValue);

        _opacity.ValueChanged += (_, _) => _opacityValue.Text = $"{_opacity.Value:P0}";
        _opacityValue.Text = $"{_opacity.Value:P0}";

        Compose();
    }

    protected override bool TryBuild(out string problem)
    {
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(_text.Text))
        {
            problem = "Enter the text to stamp onto each image.";
            return false;
        }

        var corner = _corner.SelectedIndex switch
        {
            1 => Corner.BottomLeft,
            2 => Corner.TopRight,
            3 => Corner.TopLeft,
            4 => Corner.Center,
            _ => Corner.BottomRight,
        };

        Result = new WatermarkOperation(_text.Text.Trim(), corner, _opacity.Value);
        return true;
    }
}
