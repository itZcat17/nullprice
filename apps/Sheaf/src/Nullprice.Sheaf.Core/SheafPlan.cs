namespace Nullprice.Sheaf.Core;

/// <summary>Why a plan could not be built. These are user mistakes, not exceptions.</summary>
public sealed record PlanProblem(string Message);

/// <summary>A PDF to pull pages from. <see cref="PageIndices"/> null means every page —
/// this is how "extract" is expressed (a source with only the wanted indices) and how
/// "merge" is expressed (several sources, each usually with null/all indices).</summary>
public sealed record MergeSource(string Path, IReadOnlyList<int>? PageIndices = null, string? Password = null);

/// <summary>How hard to recompress this output's embedded JPEG images (1-100, passed through
/// to <see cref="IRasterRecompressor"/>). Omitted means images are left exactly as imported.</summary>
public sealed record CompressionSettings(int Quality);

/// <summary>Replaces one text-showing operator's shown text in place. <see cref="OperatorIndex"/>
/// is captured against the page's content stream immediately after import/rotation, before any
/// other content-mutating step runs — text edits apply first, ahead of <see cref="SheafOutput.Redactions"/>,
/// specifically so that index stays valid: rewriting an operator never changes how many
/// operators exist, so applying every text edit for a page (in any order) never invalidates
/// another edit's index, but redaction *removing* operators would silently shift indices if it
/// ran first. <see cref="FontResourceName"/> is the page's own <c>/Font</c> resource key (e.g.
/// "F1") the target operator was drawn with, captured at the same time as the click that found
/// it — needed at write time to re-derive the same <see cref="EmbeddedFontExtractor"/> mapping
/// without re-walking the whole content stream's graphics state again.</summary>
public sealed record TextEdit(int PageIndex, int OperatorIndex, string NewText, string FontResourceName);

/// <summary>One PDF to write. <see cref="Operations"/> reorders, rotates, or drops pages from
/// the pages this output selects out of the plan's sources — this is how "split" is expressed
/// (several outputs, each selecting/ordering a different subset). <see cref="TextEdits"/>,
/// <see cref="Redactions"/>, and <see cref="Compression"/> are applied in that order after the
/// page selection is final: text edits first (see <see cref="TextEdit"/> for why), then
/// redaction (geometry-based, safe to run on whatever content edits left behind), then
/// compression, so a page's content is fully settled before anything about its images is
/// recompressed.</summary>
public sealed record SheafOutput(
    string Path,
    IReadOnlyList<PageOperation> Operations,
    IReadOnlyList<TextEdit>? TextEdits = null,
    IReadOnlyList<RedactionRegion>? Redactions = null,
    CompressionSettings? Compression = null);

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
