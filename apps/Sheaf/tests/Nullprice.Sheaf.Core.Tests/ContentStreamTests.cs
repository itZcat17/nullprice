namespace Nullprice.Sheaf.Core.Tests;

public class ContentStreamTests
{
    [Fact]
    public void Reads_operators_and_operands_in_order()
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes("1 0 0 1 50 100 cm /F1 12 Tf (Hi) Tj");
        var ops = ContentStreamReader.Read(bytes);

        Assert.Equal(3, ops.Count);
        Assert.Equal("cm", ops[0].Operator);
        Assert.Equal(6, ops[0].Operands.Count);
        Assert.Equal("Tf", ops[1].Operator);
        Assert.Equal("Tj", ops[2].Operator);
        Assert.Equal("Hi", System.Text.Encoding.ASCII.GetString(((PdfString)ops[2].Operands[0]).Bytes));
    }

    [Fact]
    public void Walker_tracks_a_cm_translation_into_the_ctm()
    {
        var ops = ContentStreamReader.Read(System.Text.Encoding.ASCII.GetBytes("1 0 0 1 50 100 cm re"));
        GraphicsState? captured = null;

        ContentStreamWalker.Walk(ops, PdfMatrix.Identity, (op, state) =>
        {
            if (op.Operator == "re") captured = state;
        });

        Assert.NotNull(captured);
        Assert.Equal(50, captured!.Ctm.E);
        Assert.Equal(100, captured.Ctm.F);
    }

    [Fact]
    public void Walker_restores_the_ctm_that_q_saved_once_Q_runs()
    {
        var ops = ContentStreamReader.Read(System.Text.Encoding.ASCII.GetBytes("q 1 0 0 1 50 50 cm Q re"));
        GraphicsState? captured = null;

        ContentStreamWalker.Walk(ops, PdfMatrix.Identity, (op, state) =>
        {
            if (op.Operator == "re") captured = state;
        });

        Assert.Equal(0, captured!.Ctm.E);
        Assert.Equal(0, captured.Ctm.F);
    }
}
