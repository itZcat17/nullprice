namespace Nullprice.Sheaf.Core.Tests;

public class FilterCodecTests
{
    [Fact]
    public void Flate_round_trips_arbitrary_bytes()
    {
        var original = System.Text.Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog. 0123456789.");
        var encoded = FilterCodec.Encode(original);

        var dict = new PdfDictionary(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("FlateDecode") });
        var decoded = FilterCodec.Decode(dict, encoded);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Png_up_predictor_reconstructs_original_rows()
    {
        // Two 3-byte rows, single-component (Colors=1) 8-bit samples, PNG "Up" filter (type 2):
        // each byte = original - byte directly above it in the previous row (first row's "above" is 0).
        byte[] row0 = [10, 20, 30];
        byte[] row1 = [15, 25, 35];

        var filtered = new List<byte> { 2 }; // filter type 2 = Up
        filtered.AddRange(row0.Select(b => b)); // first row: above = 0, so filtered == raw
        filtered.Add(2);
        for (var i = 0; i < row1.Length; i++) filtered.Add((byte)(row1[i] - row0[i]));

        var compressed = FilterCodec.Encode(filtered.ToArray());
        var parms = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Predictor"] = new PdfNumber(12),
            ["Colors"] = new PdfNumber(1),
            ["BitsPerComponent"] = new PdfNumber(8),
            ["Columns"] = new PdfNumber(3),
        });
        var dict = new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Filter"] = new PdfName("FlateDecode"),
            ["DecodeParms"] = parms,
        });

        var decoded = FilterCodec.Decode(dict, compressed);

        Assert.Equal(row0.Concat(row1), decoded);
    }

    [Fact]
    public void Unsupported_filter_throws_a_typed_exception()
    {
        var dict = new PdfDictionary(new Dictionary<string, PdfObject> { ["Filter"] = new PdfName("LZWDecode") });

        Assert.Throws<UnsupportedFilterException>(() => FilterCodec.Decode(dict, [1, 2, 3]));
    }
}
