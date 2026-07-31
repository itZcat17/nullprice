namespace Nullprice.Sheaf.Core;

/// <summary>Adjustments applied to an output's already-selected page list (see
/// <see cref="SheafOutput"/>) — selection itself happens via each <see cref="MergeSource"/>'s
/// page indices; these operations reorder, rotate, or drop pages from that selection.</summary>
public abstract record PageOperation;

public sealed record ReorderOperation(IReadOnlyList<int> NewPageOrder) : PageOperation;

public sealed record RotateOperation(int PageIndex, int Degrees) : PageOperation;

public sealed record DeletePageOperation(int PageIndex) : PageOperation;
