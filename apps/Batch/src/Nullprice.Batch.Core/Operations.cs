namespace Nullprice.Batch.Core;

public enum ImageFormat
{
    Jpeg,
    Png,
    Bmp,
    Tiff,
}

/// <summary>How a resize reconciles the requested box with the image's aspect ratio.</summary>
public enum ResizeStrategy
{
    /// <summary>Scale until the whole image fits inside the box. Aspect preserved, may letterbox.</summary>
    Fit,

    /// <summary>Scale until the box is covered, cropping the overflow. Aspect preserved.</summary>
    Fill,

    /// <summary>Force exactly these dimensions, distorting if necessary.</summary>
    Exact,

    /// <summary>Scale by a percentage of the original.</summary>
    Percent,
}

public enum Corner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center,
}

/// <summary>One step in a pipeline. Closed hierarchy — the processor switches over these.</summary>
public abstract record ImageOperation
{
    public abstract string Describe();
}

public sealed record ResizeOperation(ResizeStrategy Mode, int Width, int Height, double Percent = 100.0)
    : ImageOperation
{
    /// <summary>
    /// Resolves the requested resize against a real image size. Kept here rather than in the
    /// processor so the arithmetic is testable without touching a single pixel.
    /// </summary>
    public (int Width, int Height) ResolveFor(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (0, 0);

        switch (Mode)
        {
            case ResizeStrategy.Percent:
                {
                    var scale = Percent / 100.0;
                    return (AtLeastOne(sourceWidth * scale), AtLeastOne(sourceHeight * scale));
                }

            case ResizeStrategy.Exact:
                return (Math.Max(1, Width), Math.Max(1, Height));

            case ResizeStrategy.Fit:
                {
                    var scale = Math.Min((double)Width / sourceWidth, (double)Height / sourceHeight);
                    return (AtLeastOne(sourceWidth * scale), AtLeastOne(sourceHeight * scale));
                }

            case ResizeStrategy.Fill:
                {
                    var scale = Math.Max((double)Width / sourceWidth, (double)Height / sourceHeight);
                    return (AtLeastOne(sourceWidth * scale), AtLeastOne(sourceHeight * scale));
                }

            default:
                return (sourceWidth, sourceHeight);
        }
    }

    private static int AtLeastOne(double value) => Math.Max(1, (int)Math.Round(value));

    public override string Describe() => Mode switch
    {
        ResizeStrategy.Percent => $"Resize to {Percent:0.##}%",
        ResizeStrategy.Exact => $"Resize to exactly {Width}×{Height}",
        ResizeStrategy.Fit => $"Fit inside {Width}×{Height}",
        ResizeStrategy.Fill => $"Fill {Width}×{Height}, cropping overflow",
        _ => "Resize",
    };
}

public sealed record ConvertOperation(ImageFormat Format, int Quality = 85) : ImageOperation
{
    /// <summary>Quality only means anything for lossy formats.</summary>
    public bool SupportsQuality => Format == ImageFormat.Jpeg;

    public string Extension => Format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Png => ".png",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Tiff => ".tiff",
        _ => ".img",
    };

    public override string Describe() =>
        SupportsQuality ? $"Convert to {Format} at quality {Quality}" : $"Convert to {Format}";
}

public sealed record WatermarkOperation(
    string Text,
    Corner Position = Corner.BottomRight,
    double Opacity = 0.55,
    double RelativeSize = 0.04,
    int Margin = 16) : ImageOperation
{
    public override string Describe() => $"Watermark \"{Text}\" at {Position}";
}

/// <summary>
/// Drops EXIF and other embedded metadata. Worth its own operation because the usual
/// reason to run a batch over holiday photos is to strip GPS coordinates out of them.
/// </summary>
public sealed record StripMetadataOperation : ImageOperation
{
    public override string Describe() => "Strip all embedded metadata";
}

/// <summary>
/// An ordered list of operations. Order is significant: resizing before watermarking gives
/// a different result from the reverse, so the pipeline never reorders anything itself.
/// </summary>
public sealed record Pipeline(IReadOnlyList<ImageOperation> Operations)
{
    public static Pipeline Empty { get; } = new([]);

    public bool IsEmpty => Operations.Count == 0;

    /// <summary>The last conversion wins, and it decides the output extension.</summary>
    public ConvertOperation? Conversion =>
        Operations.OfType<ConvertOperation>().LastOrDefault();

    public Pipeline With(ImageOperation operation) =>
        new([.. Operations, operation]);

    public Pipeline Without(int index) =>
        index < 0 || index >= Operations.Count
            ? this
            : new([.. Operations.Where((_, i) => i != index)]);

    /// <summary>Moves an operation up or down, since order changes the result.</summary>
    public Pipeline Move(int index, int offset)
    {
        var target = index + offset;
        if (index < 0 || index >= Operations.Count) return this;
        if (target < 0 || target >= Operations.Count) return this;

        var list = Operations.ToList();
        (list[index], list[target]) = (list[target], list[index]);
        return new Pipeline(list);
    }

    public IEnumerable<string> Describe() => Operations.Select(o => o.Describe());
}
