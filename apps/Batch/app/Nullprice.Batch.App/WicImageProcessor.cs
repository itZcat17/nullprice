using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nullprice.Batch.Core;

namespace Nullprice.Batch.App;

/// <summary>
/// The pixel half of Batch, on Windows Imaging Component via WPF.
/// <para>
/// WIC is used rather than a NuGet imaging library on purpose: it ships with Windows, is
/// hardware accelerated, and adds no dependency or licence to a tool being given away.
/// The cost is that supported formats are whatever codecs the machine has.
/// </para>
/// <para>
/// Everything runs on a background thread with every intermediate bitmap frozen, so no
/// image ever belongs to a thread other than the one that made it.
/// </para>
/// </summary>
public sealed class WicImageProcessor : IImageProcessor
{
    public (int Width, int Height) Measure(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            // DelayCreation plus None means the header is read without decoding pixels.
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);

            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return (0, 0);
        }
    }

    public Task ApplyAsync(BatchItem item, Pipeline pipeline, CancellationToken cancellationToken) =>
        Task.Run(() => Apply(item, pipeline, cancellationToken), cancellationToken);

    private static void Apply(BatchItem item, Pipeline pipeline, CancellationToken cancellationToken)
    {
        BitmapSource current;
        BitmapMetadata? sourceMetadata = null;

        using (var input = File.OpenRead(item.SourcePath))
        {
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            // Cloned because the decoder's own metadata object is tied to the stream.
            if (frame.Metadata is BitmapMetadata metadata)
            {
                try { sourceMetadata = metadata.Clone(); } catch { sourceMetadata = null; }
            }

            current = frame;
            current.Freeze();
        }

        var stripMetadata = false;

        foreach (var operation in pipeline.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (operation)
            {
                case ResizeOperation resize:
                    current = ApplyResize(current, resize);
                    break;

                case WatermarkOperation watermark:
                    current = ApplyWatermark(current, watermark);
                    break;

                case StripMetadataOperation:
                    stripMetadata = true;
                    break;

                case ConvertOperation:
                    // Format is decided at encode time, not mid-pipeline.
                    break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Encode(current, item.DestinationPath, pipeline.Conversion, stripMetadata ? null : sourceMetadata);
    }

    private static BitmapSource ApplyResize(BitmapSource source, ResizeOperation resize)
    {
        var (targetWidth, targetHeight) = resize.ResolveFor(source.PixelWidth, source.PixelHeight);
        if (targetWidth <= 0 || targetHeight <= 0) return source;

        if (resize.Mode == ResizeStrategy.Fill)
        {
            // Scale to cover, then take the middle. ResolveFor already returned the
            // covering size, so this crop is what makes Fill differ from Fit.
            var scaled = Scale(source, targetWidth, targetHeight);
            var cropWidth = Math.Min(scaled.PixelWidth, resize.Width);
            var cropHeight = Math.Min(scaled.PixelHeight, resize.Height);

            var x = Math.Max(0, (scaled.PixelWidth - cropWidth) / 2);
            var y = Math.Max(0, (scaled.PixelHeight - cropHeight) / 2);

            var cropped = new CroppedBitmap(scaled, new Int32Rect(x, y, cropWidth, cropHeight));
            cropped.Freeze();
            return cropped;
        }

        return Scale(source, targetWidth, targetHeight);
    }

    private static BitmapSource Scale(BitmapSource source, int width, int height)
    {
        var transform = new ScaleTransform(
            width / (double)source.PixelWidth,
            height / (double)source.PixelHeight);

        var scaled = new TransformedBitmap(source, transform);
        scaled.Freeze();
        return scaled;
    }

    private static BitmapSource ApplyWatermark(BitmapSource source, WatermarkOperation mark)
    {
        if (string.IsNullOrWhiteSpace(mark.Text)) return source;

        // Size relative to the image so one setting works for thumbnails and full frames.
        var fontSize = Math.Max(8.0, source.PixelHeight * mark.RelativeSize);

        var text = new FormattedText(
            mark.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            fontSize,
            Brushes.White,
            pixelsPerDip: 1.0);

        var x = mark.Position switch
        {
            Corner.TopLeft or Corner.BottomLeft => mark.Margin,
            Corner.TopRight or Corner.BottomRight => source.PixelWidth - text.Width - mark.Margin,
            _ => (source.PixelWidth - text.Width) / 2,
        };

        var y = mark.Position switch
        {
            Corner.TopLeft or Corner.TopRight => mark.Margin,
            Corner.BottomLeft or Corner.BottomRight => source.PixelHeight - text.Height - mark.Margin,
            _ => (source.PixelHeight - text.Height) / 2,
        };

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));

            // A soft dark pad behind the text so it stays readable over a light image.
            dc.PushOpacity(mark.Opacity * 0.5);
            dc.DrawRectangle(
                Brushes.Black,
                null,
                new Rect(x - 6, y - 3, text.Width + 12, text.Height + 6));
            dc.Pop();

            dc.PushOpacity(mark.Opacity);
            dc.DrawText(text, new Point(x, y));
            dc.Pop();
        }

        var target = new RenderTargetBitmap(
            source.PixelWidth,
            source.PixelHeight,
            96, 96,
            PixelFormats.Pbgra32);

        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static void Encode(
        BitmapSource image,
        string destination,
        ConvertOperation? conversion,
        BitmapMetadata? metadata)
    {
        var encoder = CreateEncoder(conversion, destination);

        BitmapFrame frame;
        try
        {
            frame = BitmapFrame.Create(image, thumbnail: null, metadata, colorContexts: null);
        }
        catch
        {
            // Metadata is often incompatible across formats — JPEG EXIF into PNG, say.
            // Losing it is far better than failing the file, so fall back to none.
            frame = BitmapFrame.Create(image);
        }

        encoder.Frames.Add(frame);

        // Written to a temporary neighbour and moved into place, so an interrupted run
        // never leaves a truncated image that looks finished.
        var partial = destination + ".part";
        using (var output = File.Create(partial))
        {
            encoder.Save(output);
        }

        if (File.Exists(destination)) File.Delete(destination);
        File.Move(partial, destination);
    }

    private static BitmapEncoder CreateEncoder(ConvertOperation? conversion, string destination)
    {
        var format = conversion?.Format ?? FormatFromExtension(Path.GetExtension(destination));

        return format switch
        {
            Core.ImageFormat.Jpeg => new JpegBitmapEncoder
            {
                QualityLevel = Math.Clamp(conversion?.Quality ?? 85, 1, 100),
            },
            Core.ImageFormat.Png => new PngBitmapEncoder(),
            Core.ImageFormat.Bmp => new BmpBitmapEncoder(),
            Core.ImageFormat.Tiff => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
    }

    private static Core.ImageFormat FormatFromExtension(string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => Core.ImageFormat.Jpeg,
            ".bmp" => Core.ImageFormat.Bmp,
            ".tif" or ".tiff" => Core.ImageFormat.Tiff,
            _ => Core.ImageFormat.Png,
        };
}
