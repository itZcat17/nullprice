namespace Nullprice.Sheaf.Core.Tests;

public class PdfGarbageCollectorTests
{
    [Fact]
    public void Drops_an_object_nothing_reachable_from_root_points_to()
    {
        var objects = new PdfObjectTable();

        var reachableRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(reachableRef.Number, reachableRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Type"] = new PdfName("Catalog"),
        }));

        var orphanRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(orphanRef.Number, orphanRef.Generation, new PdfStream(PdfDictionary.Empty, "leftover secret"u8.ToArray()));

        var trailer = new PdfDictionary(new Dictionary<string, PdfObject> { ["Root"] = reachableRef });

        var pruned = PdfGarbageCollector.Prune(objects, trailer);

        Assert.True(pruned.TryGet(reachableRef.Number, reachableRef.Generation, out _));
        Assert.False(pruned.TryGet(orphanRef.Number, orphanRef.Generation, out _));
    }

    [Fact]
    public void Keeps_objects_reached_transitively_through_arrays_and_stream_dictionaries()
    {
        var objects = new PdfObjectTable();

        var deepRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(deepRef.Number, deepRef.Generation, new PdfNumber(42));

        var streamRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(streamRef.Number, streamRef.Generation, new PdfStream(
            new PdfDictionary(new Dictionary<string, PdfObject> { ["Deep"] = deepRef }), []));

        var rootRef = new PdfReference(objects.Allocate(), 0);
        objects.Set(rootRef.Number, rootRef.Generation, new PdfDictionary(new Dictionary<string, PdfObject>
        {
            ["Kids"] = new PdfArray([streamRef]),
        }));

        var trailer = new PdfDictionary(new Dictionary<string, PdfObject> { ["Root"] = rootRef });

        var pruned = PdfGarbageCollector.Prune(objects, trailer);

        Assert.True(pruned.TryGet(deepRef.Number, deepRef.Generation, out _));
        Assert.True(pruned.TryGet(streamRef.Number, streamRef.Generation, out _));
    }
}
