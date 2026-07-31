namespace Nullprice.Sheaf.Core.Tests;

public class ContentStreamRedactorTests
{
    [Fact]
    public void Drops_a_text_run_whose_bounding_box_intersects_the_region()
    {
        // Text placed at (50,700), 24pt font — "Secret" is roughly 24*0.6*6 = 86.4 wide, 24 tall.
        var content = "BT /F1 24 Tf 50 700 Td (Secret) Tj ET"u8.ToArray();
        var regions = new[] { new RedactionRegion(PageIndex: 0, X: 40, Y: 690, Width: 200, Height: 60) };

        var result = ContentStreamRedactor.Redact(content, pageIndex: 0, regions);
        var ops = ContentStreamReader.Read(result);

        Assert.DoesNotContain(ops, op => op.Operator == "Tj");
    }

    [Fact]
    public void Leaves_a_text_run_outside_the_region_untouched()
    {
        var content = "BT /F1 24 Tf 50 700 Td (Visible) Tj ET"u8.ToArray();
        var regions = new[] { new RedactionRegion(PageIndex: 0, X: 500, Y: 500, Width: 50, Height: 50) };

        var result = ContentStreamRedactor.Redact(content, pageIndex: 0, regions);
        var ops = ContentStreamReader.Read(result);

        Assert.Contains(ops, op => op.Operator == "Tj");
    }

    [Fact]
    public void Drops_an_image_whose_placement_intersects_the_region()
    {
        // The image XObject's unit square, placed via cm at (100,100) size 50x50.
        var content = "q 50 0 0 50 100 100 cm /Im1 Do Q"u8.ToArray();
        var regions = new[] { new RedactionRegion(PageIndex: 0, X: 90, Y: 90, Width: 100, Height: 100) };

        var result = ContentStreamRedactor.Redact(content, pageIndex: 0, regions);
        var ops = ContentStreamReader.Read(result);

        Assert.DoesNotContain(ops, op => op.Operator == "Do");
    }

    [Fact]
    public void Ignores_regions_for_a_different_page()
    {
        var content = "BT /F1 24 Tf 50 700 Td (Secret) Tj ET"u8.ToArray();
        var regions = new[] { new RedactionRegion(PageIndex: 1, X: 40, Y: 690, Width: 200, Height: 60) };

        var result = ContentStreamRedactor.Redact(content, pageIndex: 0, regions);
        var ops = ContentStreamReader.Read(result);

        Assert.Contains(ops, op => op.Operator == "Tj");
    }

    [Fact]
    public void No_regions_returns_the_original_bytes_unchanged()
    {
        var content = "BT /F1 24 Tf 50 700 Td (Text) Tj ET"u8.ToArray();

        var result = ContentStreamRedactor.Redact(content, pageIndex: 0, []);

        Assert.Equal(content, result);
    }
}
