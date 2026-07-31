namespace Nullprice.Sheaf.Core;

/// <summary>
/// Rebuilds an object table containing only what's reachable from the trailer's Root,
/// dropping everything else. Run before every write, not just after redaction, because an
/// unreachable object's bytes are still physically present in an output file otherwise —
/// which would make "redaction" hollow, since the removed text or image would still sit
/// recoverable in the raw PDF objects even though nothing points at it anymore.
/// </summary>
public static class PdfGarbageCollector
{
    public static PdfObjectTable Prune(PdfObjectTable source, PdfDictionary trailer)
    {
        var reachable = new HashSet<(int, int)>();
        var stack = new Stack<PdfObject>();
        stack.Push(trailer);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            switch (current)
            {
                case PdfReference r:
                    if (!reachable.Add((r.Number, r.Generation))) continue;
                    if (source.TryGet(r.Number, r.Generation, out var target)) stack.Push(target);
                    break;
                case PdfDictionary d:
                    foreach (var value in d.Entries.Values) stack.Push(value);
                    break;
                case PdfArray a:
                    foreach (var item in a.Items) stack.Push(item);
                    break;
                case PdfStream s:
                    foreach (var value in s.Dictionary.Entries.Values) stack.Push(value);
                    break;
            }
        }

        var pruned = new PdfObjectTable();
        foreach (var (key, value) in source.All)
        {
            if (reachable.Contains(key)) pruned.Set(key.Number, key.Generation, value);
        }
        return pruned;
    }
}
