namespace Nullprice.Sheaf.Core;

/// <summary>A page's <c>/Contents</c> can be one stream or an array of several (ISO 32000-1
/// §7.8.2) — this decodes and concatenates them into one byte stream, the shape everything
/// else in Sheaf that reads or rewrites page content (redaction, text editing, the interactive
/// App-side hit-testing they share) actually wants to work with, regardless of how the source
/// PDF happened to split it up.</summary>
public static class ContentStreamCombiner
{
    public static byte[]? Combine(PdfObjectTable objects, PdfObject? contentsValue)
    {
        var streams = objects.Resolve(contentsValue) switch
        {
            PdfStream s => new List<PdfStream> { s },
            PdfArray a => a.Items.Select(i => objects.Resolve(i)).OfType<PdfStream>().ToList(),
            _ => null,
        };
        if (streams is null || streams.Count == 0) return null;

        using var combined = new MemoryStream();
        foreach (var stream in streams)
        {
            var decoded = FilterCodec.Decode(stream.Dictionary, stream.RawBytes, objects);
            combined.Write(decoded, 0, decoded.Length);
            combined.WriteByte((byte)'\n');
        }
        return combined.ToArray();
    }
}
