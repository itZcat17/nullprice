namespace Nullprice.Sheaf.Core.Tests;

/// <summary>Halves the byte count without touching actual image data — Core's Recompress
/// path only cares about the bytes it's given back and the reported filter, not real JPEG
/// validity, matching the FakeProcessor pattern in Batch's tests.</summary>
public sealed class FakeRecompressor : IRasterRecompressor
{
    public byte[] Recompress(byte[] encodedImageBytes, string sourceFilterName, int quality, out string resultFilterName)
    {
        resultFilterName = "DCTDecode";
        return encodedImageBytes[..(encodedImageBytes.Length / 2 + 1)];
    }

    public long EstimateSize(byte[] encodedImageBytes, string sourceFilterName, int quality) =>
        Recompress(encodedImageBytes, sourceFilterName, quality, out _).Length;
}
