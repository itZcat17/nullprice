namespace Nullprice.Sheaf.Core.Tests;

public class TextEditPlannerTests
{
    [Fact]
    public void No_problem_when_every_character_is_supported()
    {
        var font = new FakeGlyphFont(new HashSet<int> { 'H', 'i' });

        Assert.Null(TextEditPlanner.Validate(font, "Hi"));
    }

    [Fact]
    public void Names_the_missing_character()
    {
        var font = new FakeGlyphFont(new HashSet<int> { 'H' });

        var problem = TextEditPlanner.Validate(font, "Hi");

        Assert.NotNull(problem);
        Assert.Contains("'i'", problem!.Message);
    }

    [Fact]
    public void Names_every_distinct_missing_character_once()
    {
        var font = new FakeGlyphFont(new HashSet<int>());

        var problem = TextEditPlanner.Validate(font, "aab");

        Assert.Contains("'a'", problem!.Message);
        Assert.Contains("'b'", problem.Message);
    }
}
