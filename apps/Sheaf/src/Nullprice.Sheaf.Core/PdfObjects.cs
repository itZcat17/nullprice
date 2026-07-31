namespace Nullprice.Sheaf.Core;

/// <summary>Base type for every value in a PDF's object graph (ISO 32000-1 §7.3).</summary>
public abstract record PdfObject;

public sealed record PdfNull : PdfObject
{
    public static readonly PdfNull Instance = new();
}

public sealed record PdfBoolean(bool Value) : PdfObject;

/// <summary>A PDF numeric object. PDF makes no syntactic distinction between integers and
/// reals; <see cref="IsInteger"/> only tracks how it was written so the writer round-trips
/// "1" instead of turning every integer into "1.0".</summary>
public sealed record PdfNumber(double Value, bool IsInteger = true) : PdfObject
{
    public int AsInt => (int)Math.Round(Value);
}

public sealed record PdfName(string Value) : PdfObject;

/// <summary>Raw bytes of a PDF string object (literal "(...)" or hex "&lt;...&gt;"). PDF
/// strings carry no fixed text encoding of their own, and nothing in Sheaf needs to interpret
/// their contents as text — they are only ever copied through untouched.</summary>
public sealed record PdfString(byte[] Bytes, bool WasHex = false) : PdfObject;

public sealed record PdfArray(IReadOnlyList<PdfObject> Items) : PdfObject
{
    public static readonly PdfArray Empty = new([]);
}

public sealed record PdfDictionary(IReadOnlyDictionary<string, PdfObject> Entries) : PdfObject
{
    public static readonly PdfDictionary Empty = new(new Dictionary<string, PdfObject>());

    public PdfObject? Get(string key) => Entries.TryGetValue(key, out var v) ? v : null;

    public PdfDictionary With(string key, PdfObject value)
    {
        var copy = new Dictionary<string, PdfObject>(Entries) { [key] = value };
        return new PdfDictionary(copy);
    }

    public PdfDictionary Without(string key)
    {
        if (!Entries.ContainsKey(key)) return this;
        var copy = new Dictionary<string, PdfObject>(Entries);
        copy.Remove(key);
        return new PdfDictionary(copy);
    }
}

/// <summary>An indirect object's stream: its dictionary plus the exact bytes between
/// "stream" and "endstream", still encoded however <c>/Filter</c> says. Call
/// <see cref="FilterCodec.Decode"/> to get the actual payload.</summary>
public sealed record PdfStream(PdfDictionary Dictionary, byte[] RawBytes) : PdfObject;

public sealed record PdfReference(int Number, int Generation) : PdfObject;

/// <summary>Resolves indirect references against the parsed (or, for a document being built
/// fresh, still-being-assembled) set of objects. Also owns object-number allocation, which
/// matters once more than one part of the codebase (page-tree edits, cross-document import)
/// needs to hand out fresh numbers into the same table without colliding — see
/// <see cref="Allocate"/>.</summary>
public sealed class PdfObjectTable
{
    private readonly Dictionary<(int Number, int Generation), PdfObject> _objects = [];
    private int _nextNumber = 1;

    public IReadOnlyDictionary<(int Number, int Generation), PdfObject> All => _objects;

    public void Set(int number, int generation, PdfObject value)
    {
        _objects[(number, generation)] = value;
        if (number >= _nextNumber) _nextNumber = number + 1;
    }

    public bool TryGet(int number, int generation, out PdfObject value) =>
        _objects.TryGetValue((number, generation), out value!);

    /// <summary>Hands out a fresh, never-before-used object number. Centralized here (rather
    /// than let each caller compute "max + 1" independently) because multiple importers can
    /// be allocating into the same destination table in an interleaved order — a locally
    /// cached counter would go stale and collide with what another caller just claimed.</summary>
    public int Allocate() => _nextNumber++;

    /// <summary>Follows a chain of indirect references to the underlying direct value. A
    /// broken reference or a reference cycle resolves to <see cref="PdfNull"/> rather than
    /// throwing — malformed input is common in real-world PDFs and should degrade gracefully.</summary>
    public PdfObject Resolve(PdfObject? obj)
    {
        var seen = new HashSet<(int, int)>();
        while (obj is PdfReference r)
        {
            if (!seen.Add((r.Number, r.Generation))) return PdfNull.Instance;
            if (!TryGet(r.Number, r.Generation, out var next)) return PdfNull.Instance;
            obj = next;
        }
        return obj ?? PdfNull.Instance;
    }
}
