namespace Nullprice.Sheaf.Core;

/// <summary>Copies a page's full object closure (its dictionary, resources, content
/// stream(s), and everything those transitively reference) from one document's object table
/// into another, assigning the destination fresh object numbers and rewriting every reference
/// along the way. Needed because a PDF's object numbers only mean something within its own
/// file — merging two documents means giving every carried-over object a new, non-colliding
/// number in the destination.
/// <para>
/// Reusing one importer across every page pulled from the same source document is
/// deliberate: the reference map means a resource shared by several of that source's pages
/// (a font, an image) is copied into the destination exactly once, not once per page.
/// </para>
/// </summary>
public sealed class PdfGraphImporter(PdfObjectTable source, PdfObjectTable destination)
{
    private readonly Dictionary<(int, int), PdfReference> _map = [];

    public PdfReference ImportPage(PdfDictionary pageDict)
    {
        // /Parent is deliberately dropped: it points up into the source document's own page
        // tree, which would otherwise pull in every sibling page's full closure (and, via
        // their own /Parent, the whole tree) just to reach one page's ancestor. The caller
        // assigns a fresh /Parent once it builds the destination's own Pages node.
        var imported = ImportValue(pageDict.Without("Parent"));
        var num = destination.Allocate();
        destination.Set(num, 0, imported);
        return new PdfReference(num, 0);
    }

    private PdfObject ImportValue(PdfObject value) => value switch
    {
        PdfReference r => ImportReference(r),
        PdfArray a => new PdfArray(a.Items.Select(ImportValue).ToList()),
        PdfDictionary d => new PdfDictionary(d.Entries.ToDictionary(kv => kv.Key, kv => ImportValue(kv.Value))),
        PdfStream s => new PdfStream(new PdfDictionary(s.Dictionary.Entries.ToDictionary(kv => kv.Key, kv => ImportValue(kv.Value))), s.RawBytes),
        _ => value, // leaf values (numbers, names, strings, booleans, null) copy as-is
    };

    private PdfReference ImportReference(PdfReference r)
    {
        if (_map.TryGetValue((r.Number, r.Generation), out var existing)) return existing;

        // The mapping is reserved before recursing so reference cycles (a page's /Group
        // pointing back at itself, a font's /Descendants forming a loop, etc.) terminate.
        var num = destination.Allocate();
        var placeholder = new PdfReference(num, 0);
        _map[(r.Number, r.Generation)] = placeholder;
        destination.Set(num, 0, PdfNull.Instance);

        var imported = ImportValue(source.Resolve(r));
        destination.Set(num, 0, imported);

        return placeholder;
    }
}
