using static Nullprice.Sheaf.Core.SfntBytes;

namespace Nullprice.Sheaf.Core;

/// <summary>
/// Assembles a set of sfnt tables (tag → raw bytes) into a complete, spec-valid TrueType font
/// file: table directory (sorted by tag, with the binary-search hint fields the spec requires),
/// 4-byte padding between tables, per-table checksums, and the whole-font
/// <c>head.checkSumAdjustment</c> dance. Shared by production code
/// (<see cref="TrueTypeSubsetter"/>) and by test fixtures that build synthetic fonts, so
/// fixture-backed tests exercise the same assembly path production output does.
/// </summary>
public static class SfntWriter
{
    public static byte[] Build(IReadOnlyDictionary<string, byte[]> tables)
    {
        var orderedTags = tables.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();
        var numTables = orderedTags.Count;

        var searchRange = 1;
        var entrySelector = 0;
        while (searchRange * 2 <= numTables) { searchRange *= 2; entrySelector++; }
        searchRange *= 16;
        var rangeShift = numTables * 16 - searchRange;

        var directoryStart = 12;
        var dataStart = directoryStart + numTables * 16;

        var padded = new Dictionary<string, byte[]>();
        var offsets = new Dictionary<string, int>();
        var offset = dataStart;
        foreach (var tag in orderedTags)
        {
            var raw = tables[tag];
            var pad = (4 - raw.Length % 4) % 4;
            var buf = new byte[raw.Length + pad];
            Array.Copy(raw, buf, raw.Length);
            padded[tag] = buf;
            offsets[tag] = offset;
            offset += buf.Length;
        }

        var result = new byte[offset];
        WriteUInt32BE(result, 0, 0x00010000); // sfnt version: classic TrueType (not 'OTTO')
        WriteUInt16BE(result, 4, (ushort)numTables);
        WriteUInt16BE(result, 6, (ushort)searchRange);
        WriteUInt16BE(result, 8, (ushort)entrySelector);
        WriteUInt16BE(result, 10, (ushort)rangeShift);

        var dirOffset = directoryStart;
        foreach (var tag in orderedTags)
        {
            var buf = padded[tag];
            var tagBytes = System.Text.Encoding.ASCII.GetBytes(tag);
            Array.Copy(tagBytes, 0, result, dirOffset, 4);
            WriteUInt32BE(result, dirOffset + 4, TableChecksum(buf));
            WriteUInt32BE(result, dirOffset + 8, (uint)offsets[tag]);
            WriteUInt32BE(result, dirOffset + 12, (uint)tables[tag].Length); // unpadded actual length
            dirOffset += 16;

            Array.Copy(buf, 0, result, offsets[tag], buf.Length);
        }

        // head.checkSumAdjustment must be computed last: with the field itself zeroed (callers
        // are expected to have already zeroed it — TrueTypeSubsetter's BuildHead does), sum the
        // whole assembled file the same way, then store 0xB1B0AFBA minus that sum. This value is
        // patched directly into the final buffer rather than folded back into the table
        // directory's own stored checksum for 'head', which — per spec — stays exactly as
        // computed above (with the adjustment field at zero).
        if (offsets.TryGetValue("head", out var headOffset))
        {
            var wholeFontChecksum = TableChecksum(result);
            WriteUInt32BE(result, headOffset + 8, 0xB1B0AFBAu - wholeFontChecksum);
        }

        return result;
    }

    private static uint TableChecksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var b = 0; b < 4; b++)
            {
                var idx = i + b;
                word = (word << 8) | (idx < data.Length ? data[idx] : (byte)0);
            }
            sum += word;
        }
        return sum;
    }
}
