namespace Nullprice.Sheaf.Core;

/// <summary>A rasterized page: raw premultiplied BGRA8 pixels, top-left origin. Plain data —
/// no UI framework type — so Core can define the contract without depending on WPF.</summary>
public sealed record RenderedPage(int PixelWidth, int PixelHeight, byte[] Bgra32Pixels);

/// <summary>The one thing Core cannot do itself: turn a PDF page into pixels. Implemented in
/// the App project against <c>Windows.Data.Pdf</c> (the OS-provided PDF rasterizer, ships
/// with Windows 10/11) and stubbed in tests — the same boundary Batch draws around
/// <c>IImageProcessor</c>/WIC. Rendering is read-only: it has no bearing on the actual
/// editing/writing logic, which is all Sheaf's own code regardless of what renders the
/// preview.</summary>
public interface IPdfRenderer
{
    Task<int> GetPageCountAsync(string path, string? password, CancellationToken cancellationToken);

    Task<RenderedPage> RenderPageAsync(string path, int pageIndex, double dpi, string? password, CancellationToken cancellationToken);
}
