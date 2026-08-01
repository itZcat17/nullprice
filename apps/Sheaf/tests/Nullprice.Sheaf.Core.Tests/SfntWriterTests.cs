namespace Nullprice.Sheaf.Core.Tests;

public class SfntWriterTests
{
    [Fact]
    public void Whole_font_checksum_satisfies_the_spec_invariant_once_checkSumAdjustment_is_baked_in()
    {
        // The spec defines checkSumAdjustment as (0xB1B0AFBA - checksumOfWholeFileWithAdjustmentZeroed).
        // That means summing the *final* file (adjustment now filled in) must land exactly on
        // 0xB1B0AFBA — a self-checking invariant that verifies the checksum math directly,
        // without needing any TrueType glyph semantics.
        var tables = new Dictionary<string, byte[]>
        {
            ["head"] = new byte[54],
            ["abcd"] = [1, 2, 3], // deliberately odd length, to exercise padding
        };

        var bytes = SfntWriter.Build(tables);

        Assert.Equal(0, bytes.Length % 4);

        uint sum = 0;
        for (var i = 0; i < bytes.Length; i += 4)
            sum += (uint)((bytes[i] << 24) | (bytes[i + 1] << 16) | (bytes[i + 2] << 8) | bytes[i + 3]);

        Assert.Equal(0xB1B0AFBAu, sum);
    }

    [Fact]
    public void Table_directory_is_sorted_by_tag_and_records_each_tables_unpadded_length()
    {
        var tables = new Dictionary<string, byte[]>
        {
            ["zzzz"] = [1, 2, 3, 4, 5],
            ["aaaa"] = [9, 9],
        };

        var bytes = SfntWriter.Build(tables);

        var numTables = (bytes[4] << 8) | bytes[5];
        Assert.Equal(2, numTables);

        var firstTag = System.Text.Encoding.ASCII.GetString(bytes, 12, 4);
        var secondTag = System.Text.Encoding.ASCII.GetString(bytes, 28, 4);
        Assert.Equal("aaaa", firstTag);
        Assert.Equal("zzzz", secondTag);

        var zzzzLength = (bytes[40] << 24) | (bytes[41] << 16) | (bytes[42] << 8) | bytes[43];
        Assert.Equal(5, zzzzLength); // unpadded, even though its storage was padded to 8 bytes
    }

    [Fact]
    public void Without_a_head_table_no_checksum_adjustment_is_attempted()
    {
        var tables = new Dictionary<string, byte[]> { ["abcd"] = [1, 2, 3, 4] };
        var bytes = SfntWriter.Build(tables);

        Assert.Equal(0, bytes.Length % 4);
        Assert.True(bytes.Length > 0);
    }
}
