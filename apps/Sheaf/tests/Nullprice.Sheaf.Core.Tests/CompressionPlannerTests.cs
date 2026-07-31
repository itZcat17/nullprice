namespace Nullprice.Sheaf.Core.Tests;

public class CompressionPlannerTests
{
    [Fact]
    public void Finds_the_jpeg_image_and_estimates_its_recompressed_size()
    {
        var doc = PdfDocument.Open(PdfTestFixtures.BuildDocumentWithTextAndImage(imageByteCount: 200)).Document!;

        var plan = CompressionPlanner.Build(doc, [0], quality: 50, new FakeRecompressor());

        var item = Assert.Single(plan.Items);
        Assert.Equal(0, item.PageIndex);
        Assert.Equal("Im1", item.XObjectName);
        Assert.Equal(200, item.OriginalSize);
        Assert.Equal(101, item.EstimatedSize); // FakeRecompressor halves (200/2 + 1)
    }

    [Fact]
    public void Ignores_pages_outside_the_requested_range()
    {
        var doc = PdfDocument.Open(PdfTestFixtures.BuildDocumentWithTextAndImage()).Document!;

        var plan = CompressionPlanner.Build(doc, [], quality: 50, new FakeRecompressor());

        Assert.Empty(plan.Items);
    }
}
