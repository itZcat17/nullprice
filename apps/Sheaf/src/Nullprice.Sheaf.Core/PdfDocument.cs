namespace Nullprice.Sheaf.Core;

public enum PdfOpenStatus { Success, WrongPassword, UnsupportedEncryption, ParseError }

public sealed record PdfOpenResult(PdfOpenStatus Status, PdfDocument? Document, string? Message)
{
    public static PdfOpenResult Ok(PdfDocument doc) => new(PdfOpenStatus.Success, doc, null);
    public static PdfOpenResult Fail(PdfOpenStatus status, string message) => new(status, null, message);
}

public sealed record PdfPageRef(int PageIndex, PdfReference ObjectRef, PdfDictionary Dictionary);

/// <summary>An open PDF document: its object graph plus a flattened page list with inherited
/// attributes (Resources/MediaBox/CropBox/Rotate) already resolved. This is the shared
/// foundation every Sheaf operation — page ops, redaction, compression, text editing — reads
/// and writes against.</summary>
public sealed class PdfDocument
{
    public PdfObjectTable Objects { get; }
    public PdfDictionary Trailer { get; }
    public IReadOnlyList<PdfPageRef> Pages { get; }

    private PdfDocument(PdfObjectTable objects, PdfDictionary trailer)
    {
        Objects = objects;
        Trailer = trailer;
        Pages = WalkPageTree();
    }

    /// <summary>Builds a document directly from an already-assembled object table (no
    /// parsing) — used when Sheaf itself constructs a new output document, e.g. after a
    /// merge/split/reorder, rather than opening one from disk.</summary>
    public static PdfDocument Create(PdfObjectTable objects, PdfDictionary trailer) => new(objects, trailer);

    public static PdfOpenResult Open(byte[] bytes, string? password = null)
    {
        ParsedPdf parsed;
        try
        {
            parsed = PdfParser.Parse(bytes, password);
        }
        catch (Exception ex)
        {
            return PdfOpenResult.Fail(PdfOpenStatus.ParseError, ex.Message);
        }

        if (parsed.Encryption == PdfEncryptionStatus.WrongPassword)
            return PdfOpenResult.Fail(PdfOpenStatus.WrongPassword, "That password didn't unlock this PDF.");

        if (parsed.Encryption == PdfEncryptionStatus.Unsupported)
            return PdfOpenResult.Fail(PdfOpenStatus.UnsupportedEncryption, parsed.EncryptionMessage ?? "This PDF's encryption isn't supported yet.");

        if (parsed.Objects.All.Count == 0)
            return PdfOpenResult.Fail(PdfOpenStatus.ParseError, "No objects could be read from this file.");

        return PdfOpenResult.Ok(new PdfDocument(parsed.Objects, parsed.Trailer));
    }

    public byte[] GetStreamData(PdfStream stream) => FilterCodec.Decode(stream.Dictionary, stream.RawBytes, Objects);

    public PdfDictionary? GetRoot() => Objects.Resolve(Trailer.Get("Root")) as PdfDictionary;

    private static readonly string[] InheritableKeys = ["Resources", "MediaBox", "CropBox", "Rotate"];

    private List<PdfPageRef> WalkPageTree()
    {
        var result = new List<PdfPageRef>();
        var root = GetRoot();
        var pagesRoot = root is null ? null : Objects.Resolve(root.Get("Pages")) as PdfDictionary;
        if (pagesRoot is null) return result;

        var visited = new HashSet<PdfDictionary>();

        void Walk(PdfReference? selfRef, PdfDictionary node, PdfDictionary inherited)
        {
            if (!visited.Add(node)) return; // cycle guard against a malformed page tree

            var merged = MergeInherited(inherited, node);
            var kids = Objects.Resolve(node.Get("Kids")) as PdfArray;

            if (kids is not null)
            {
                foreach (var kidRef in kids.Items)
                {
                    if (Objects.Resolve(kidRef) is PdfDictionary kidDict)
                        Walk(kidRef as PdfReference, kidDict, merged);
                }
            }
            else
            {
                result.Add(new PdfPageRef(result.Count, selfRef ?? new PdfReference(0, 0), merged));
            }
        }

        Walk(root?.Get("Pages") as PdfReference, pagesRoot, PdfDictionary.Empty);
        return result;
    }

    private static PdfDictionary MergeInherited(PdfDictionary inherited, PdfDictionary node)
    {
        var entries = new Dictionary<string, PdfObject>(node.Entries);
        foreach (var key in InheritableKeys)
        {
            if (!entries.ContainsKey(key) && inherited.Get(key) is { } v)
                entries[key] = v;
        }
        return new PdfDictionary(entries);
    }
}
