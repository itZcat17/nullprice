namespace Nullprice.Sheaf.Core.Tests;

public class ContentStreamTextEditorTests
{
    [Fact]
    public void Finds_the_text_run_under_a_point()
    {
        var content = "BT /F1 24 Tf 50 700 Td (Hello) Tj ET"u8.ToArray();

        var found = ContentStreamTextEditor.FindTextAt(content, x: 60, y: 705);

        Assert.NotNull(found);
        Assert.Equal("Hello", found!.Text);
        Assert.Equal("F1", found.FontResourceName);
    }

    [Fact]
    public void Returns_null_when_nothing_is_under_the_point()
    {
        var content = "BT /F1 24 Tf 50 700 Td (Hello) Tj ET"u8.ToArray();

        Assert.Null(ContentStreamTextEditor.FindTextAt(content, x: 500, y: 500));
    }

    [Fact]
    public void Rewrites_the_operator_using_the_fonts_code_mapping()
    {
        var content = "BT /F1 24 Tf 50 700 Td (Hi) Tj ET"u8.ToArray();
        var font = new ExtractedFont(
            FontFileBytes: [],
            CodeToUnicode: new Dictionary<int, int>(),
            UnicodeToCode: new Dictionary<int, int> { ['B'] = 'B', ['y'] = 'y', ['e'] = 'e' },
            CodeToWidthEm: new Dictionary<int, double>());

        var rewritten = ContentStreamTextEditor.Rewrite(content, operatorIndex: 3, "Bye", font);

        Assert.NotNull(rewritten);
        var ops = ContentStreamReader.Read(rewritten!);
        var tj = Assert.Single(ops, op => op.Operator == "Tj");
        var str = (PdfString)tj.Operands[0];
        Assert.Equal("Bye", System.Text.Encoding.Latin1.GetString(str.Bytes));
    }

    [Fact]
    public void Returns_null_when_the_font_has_no_code_for_a_character()
    {
        var content = "BT /F1 24 Tf 50 700 Td (Hi) Tj ET"u8.ToArray();
        var font = new ExtractedFont([], new Dictionary<int, int>(), new Dictionary<int, int>(), new Dictionary<int, double>());

        Assert.Null(ContentStreamTextEditor.Rewrite(content, 3, "Hi", font));
    }

    [Fact]
    public void Returns_null_for_an_out_of_range_operator_index()
    {
        var content = "BT /F1 24 Tf 50 700 Td (Hi) Tj ET"u8.ToArray();
        var font = new ExtractedFont([], new Dictionary<int, int>(), new Dictionary<int, int>(), new Dictionary<int, double>());

        Assert.Null(ContentStreamTextEditor.Rewrite(content, 99, "Hi", font));
    }
}
