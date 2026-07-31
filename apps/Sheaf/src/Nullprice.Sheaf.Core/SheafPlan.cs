namespace Nullprice.Sheaf.Core;

/// <summary>Why a plan could not be built. These are user mistakes, not exceptions.</summary>
public sealed record PlanProblem(string Message);

/// <summary>A PDF to pull pages from. <see cref="PageIndices"/> null means every page —
/// this is how "extract" is expressed (a source with only the wanted indices) and how
/// "merge" is expressed (several sources, each usually with null/all indices).</summary>
public sealed record MergeSource(string Path, IReadOnlyList<int>? PageIndices = null, string? Password = null);

/// <summary>One PDF to write. <see cref="Operations"/> reorders, rotates, or drops pages from
/// the pages this output selects out of the plan's sources — this is how "split" is expressed
/// (several outputs, each selecting/ordering a different subset).</summary>
public sealed record SheafOutput(string Path, IReadOnlyList<PageOperation> Operations);

public sealed record SheafPlan(
    IReadOnlyList<MergeSource> Sources,
    IReadOnlyList<SheafOutput> Outputs,
    IReadOnlyList<PlanProblem> Problems)
{
    public bool IsRunnable => Problems.Count == 0 && Outputs.Count > 0;
}

/// <summary>
/// Resolves sources and outputs into a runnable plan. Mirrors Batch's <c>BatchPlanner</c>:
/// the same two destructive mistakes are guarded here rather than trusted to the UI — an
/// output overwriting one of its own sources, and two outputs claiming the same path.
/// </summary>
public static class SheafPlanner
{
    public static SheafPlan Build(IEnumerable<MergeSource> sources, IEnumerable<SheafOutput> outputs)
    {
        var sourceList = sources.ToList();
        var outputList = outputs.ToList();
        var problems = new List<PlanProblem>();

        if (sourceList.Count == 0)
            problems.Add(new PlanProblem("Add at least one PDF to work with."));

        if (outputList.Count == 0)
            problems.Add(new PlanProblem("Nothing to write — add at least one output."));

        foreach (var source in sourceList)
        {
            if (!File.Exists(source.Path))
                problems.Add(new PlanProblem($"Not found: {Path.GetFileName(source.Path)}"));
        }

        var sourcePaths = new HashSet<string>(sourceList.Select(s => Path.GetFullPath(s.Path)), StringComparer.OrdinalIgnoreCase);
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var output in outputList)
        {
            var fullOutput = Path.GetFullPath(output.Path);

            if (sourcePaths.Contains(fullOutput))
            {
                problems.Add(new PlanProblem(
                    $"\"{Path.GetFileName(output.Path)}\" would overwrite one of the source files. Pick a different name or folder."));
            }

            if (claimed.ContainsKey(fullOutput))
            {
                problems.Add(new PlanProblem(
                    $"Two outputs would both write to \"{Path.GetFileName(output.Path)}\". Give them different names."));
            }
            else
            {
                claimed[fullOutput] = output.Path;
            }
        }

        return new SheafPlan(sourceList, outputList, problems);
    }
}
