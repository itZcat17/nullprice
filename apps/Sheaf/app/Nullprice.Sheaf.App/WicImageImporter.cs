using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nullprice.Sheaf.App;

/// <summary>
/// Decodes an arbitrary external image file (PNG/JPG/BMP/…) via WIC — the same "OS component,
/// not a NuGet dependency" choice <see cref="WicRasterRecompressor"/> already makes — and
/// re-encodes it as JPEG for embedding. Unlike <see cref="WicRasterRecompressor"/> (which
/// recompresses an image already embedded in a PDF, whose declared <c>/ColorSpace</c> it
/// inherits), this always converts to 24-bit RGB explicitly before encoding: the PDF side
/// (<see cref="Nullprice.Sheaf.Core.AnnotationWriter"/>) always declares <c>/DeviceRGB</c>, so
/// the actual JPEG component count has to match regardless of the source file's own color
/// format (grayscale, indexed, CMYK, …).
/// </summary>
public static class WicImageImporter
{
    public static (byte[] JpegBytes, int PixelWidth, int PixelHeight) ImportAsJpeg(string filePath, int quality = 90)
    {
        using var input = File.OpenRead(filePath);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var rgb = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);

        using var output = new MemoryStream();
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(rgb));
        encoder.Save(output);

        return (output.ToArray(), frame.PixelWidth, frame.PixelHeight);
    }
}
