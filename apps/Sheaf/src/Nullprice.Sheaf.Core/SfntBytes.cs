namespace Nullprice.Sheaf.Core;

/// <summary>Big-endian byte IO shared by every sfnt (TrueType) reader/writer in the M8 font
/// subsetting engine (<see cref="TrueTypeFont"/>, <see cref="CompositeGlyphWalker"/>,
/// <see cref="TrueTypeSubsetter"/>, <see cref="SfntWriter"/>) — every multi-byte field in an
/// sfnt file is big-endian, unlike the rest of Sheaf's own PDF format.</summary>
internal static class SfntBytes
{
    public static string Tag(byte[] bytes, int offset) => System.Text.Encoding.ASCII.GetString(bytes, offset, 4);

    public static ushort ReadUInt16BE(byte[] bytes, int offset) => (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

    public static short ReadInt16BE(byte[] bytes, int offset) => (short)ReadUInt16BE(bytes, offset);

    public static uint ReadUInt32BE(byte[] bytes, int offset) =>
        ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];

    /// <summary>A 16.16 fixed-point value (sfnt's <c>Fixed</c> type), as a signed integer divided
    /// by 65536 — used for fields like <c>post.italicAngle</c>.</summary>
    public static double ReadFixed(byte[] bytes, int offset) => (int)ReadUInt32BE(bytes, offset) / 65536.0;

    public static void WriteUInt16BE(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }

    public static void WriteUInt32BE(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }
}
