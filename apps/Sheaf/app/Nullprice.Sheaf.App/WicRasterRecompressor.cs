using System.IO;
using System.Windows.Media.Imaging;
using Nullprice.Sheaf.Core;

namespace Nullprice.Sheaf.App;

/// <summary>Recompresses embedded JPEG images via WIC — the same "OS component, not a NuGet
/// dependency" choice Batch makes for its own image pipeline
/// (<see cref="WicImageProcessor"/> in the Batch app).</summary>
public sealed class WicRasterRecompressor : IRasterRecompressor
{
    public byte[] Recompress(byte[] encodedImageBytes, string sourceFilterName, int quality, out string resultFilterName)
    {
        using var input = new MemoryStream(encodedImageBytes);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        using var output = new MemoryStream();
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        encoder.Save(output);

        resultFilterName = "DCTDecode";
        return output.ToArray();
    }

    public long EstimateSize(byte[] encodedImageBytes, string sourceFilterName, int quality) =>
        Recompress(encodedImageBytes, sourceFilterName, quality, out _).Length;
}
