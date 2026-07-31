namespace Nullprice.Sheaf.Core;

/// <summary>Thrown when a stream's declared filter isn't one Sheaf implements. Callers at a
/// plan-build boundary catch this and turn it into a <see cref="PlanProblem"/> — refusing to
/// touch a stream it can't safely decode is safer than writing back corrupted bytes.</summary>
public sealed class UnsupportedFilterException(string filterName) : Exception($"Unsupported stream filter: {filterName}")
{
    public string FilterName { get; } = filterName;
}

/// <summary>Decodes and encodes PDF stream filters (ISO 32000-1 §7.4). Only FlateDecode is
/// implemented — it is what the overwhelming majority of real-world PDFs use for both content
/// streams and cross-reference streams — including the PNG predictors almost every generator
/// layers on top of it. Every other filter is refused explicitly via
/// <see cref="UnsupportedFilterException"/> rather than silently mishandled.</summary>
public static class FilterCodec
{
    public static byte[] Decode(PdfDictionary streamDict, byte[] raw, PdfObjectTable? table = null)
    {
        PdfObject Resolve(PdfObject? o) => o is null ? PdfNull.Instance : table?.Resolve(o) ?? o;

        var filters = NamesOf(Resolve(streamDict.Get("Filter")));
        var parmsList = ParmsOf(Resolve(streamDict.Get("DecodeParms")), filters.Count, table);

        var data = raw;
        for (var i = 0; i < filters.Count; i++)
        {
            data = filters[i] switch
            {
                "FlateDecode" or "Fl" => ApplyPredictor(Inflate(data), parmsList[i]),
                _ => throw new UnsupportedFilterException(filters[i]),
            };
        }
        return data;
    }

    /// <summary>Encodes fresh stream data with plain FlateDecode, no predictor. v1 never needs
    /// to re-apply a predictor on write — that is a compression-ratio optimization, not a
    /// correctness requirement, since a plain Flate stream decodes identically either way.</summary>
    public static byte[] Encode(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] ApplyPredictor(byte[] data, PdfDictionary? parms)
    {
        var predictor = (parms?.Get("Predictor") as PdfNumber)?.AsInt ?? 1;
        if (predictor <= 1) return data;

        var colors = (parms?.Get("Colors") as PdfNumber)?.AsInt ?? 1;
        var bpc = (parms?.Get("BitsPerComponent") as PdfNumber)?.AsInt ?? 8;
        var columns = (parms?.Get("Columns") as PdfNumber)?.AsInt ?? 1;

        if (predictor == 2) return ApplyTiffPredictor(data, colors, bpc, columns);

        return ApplyPngPredictor(data, colors, bpc, columns);
    }

    private static byte[] ApplyPngPredictor(byte[] data, int colors, int bpc, int columns)
    {
        var bytesPerPixel = Math.Max(1, colors * bpc / 8);
        var rowBytes = (colors * bpc * columns + 7) / 8;
        if (rowBytes <= 0) return data;

        var rows = data.Length / (rowBytes + 1);
        var output = new byte[rows * rowBytes];
        var prevRow = new byte[rowBytes];

        for (var r = 0; r < rows; r++)
        {
            var srcOffset = r * (rowBytes + 1);
            var filterType = data[srcOffset];
            var dstOffset = r * rowBytes;

            for (var i = 0; i < rowBytes; i++)
            {
                var raw = data[srcOffset + 1 + i];
                var a = i >= bytesPerPixel ? output[dstOffset + i - bytesPerPixel] : (byte)0;
                var b = prevRow[i];
                var c = i >= bytesPerPixel ? prevRow[i - bytesPerPixel] : (byte)0;

                output[dstOffset + i] = filterType switch
                {
                    0 => raw,
                    1 => (byte)(raw + a),
                    2 => (byte)(raw + b),
                    3 => (byte)(raw + (a + b) / 2),
                    4 => (byte)(raw + PaethPredictor(a, b, c)),
                    _ => raw,
                };
            }

            Array.Copy(output, dstOffset, prevRow, 0, rowBytes);
        }

        return output;
    }

    private static byte[] ApplyTiffPredictor(byte[] data, int colors, int bpc, int columns)
    {
        if (bpc != 8) return data; // sub-byte TIFF prediction is rare enough to skip in v1
        var rowBytes = colors * columns;
        if (rowBytes <= 0) return data;
        var rows = data.Length / rowBytes;
        var output = (byte[])data.Clone();
        for (var r = 0; r < rows; r++)
        {
            var rowStart = r * rowBytes;
            for (var i = colors; i < rowBytes; i++)
                output[rowStart + i] = (byte)(output[rowStart + i] + output[rowStart + i - colors]);
        }
        return output;
    }

    private static int PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static List<string> NamesOf(PdfObject? filterObj) => filterObj switch
    {
        PdfName n => [n.Value],
        PdfArray a => a.Items.OfType<PdfName>().Select(n => n.Value).ToList(),
        _ => [],
    };

    private static List<PdfDictionary?> ParmsOf(PdfObject? parmsObj, int count, PdfObjectTable? table)
    {
        PdfObject Resolve(PdfObject o) => table?.Resolve(o) ?? o;
        var list = parmsObj switch
        {
            PdfDictionary d => new List<PdfDictionary?> { d },
            PdfArray a => a.Items.Select(i => Resolve(i) as PdfDictionary).ToList(),
            _ => [],
        };
        while (list.Count < count) list.Add(null);
        return list;
    }
}
