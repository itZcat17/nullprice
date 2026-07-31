namespace Nullprice.Sheaf.Core;

/// <summary>The one thing Core cannot do itself for compression: turn image bytes into
/// smaller image bytes. Implemented in the App project against WIC — the same
/// interface-in-Core, implementation-in-App boundary as <see cref="IPdfRenderer"/> and
/// Batch's <c>IImageProcessor</c>.</summary>
public interface IRasterRecompressor
{
    byte[] Recompress(byte[] encodedImageBytes, string sourceFilterName, int quality, out string resultFilterName);

    long EstimateSize(byte[] encodedImageBytes, string sourceFilterName, int quality);
}
