using System.Runtime.InteropServices.WindowsRuntime;
using Nullprice.Sheaf.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;
using WinPdfPageRenderOptions = Windows.Data.Pdf.PdfPageRenderOptions;

namespace Nullprice.Sheaf.App;

/// <summary>
/// Renders PDF pages via <c>Windows.Data.Pdf</c>, the PDF rasterizer built into Windows
/// 10/11 — the same "let the OS do the specialist, read-only part" choice Batch makes with
/// WIC for image decoding. This class only ever reads a page and produces pixels; it has no
/// path back into the document's actual content, so it cannot be the thing that edits a PDF —
/// that stays entirely in <c>Nullprice.Sheaf.Core</c>.
/// </summary>
public sealed class WindowsDataPdfRenderer : IPdfRenderer
{
    public async Task<int> GetPageCountAsync(string path, string? password, CancellationToken cancellationToken)
    {
        var doc = await LoadAsync(path, password);
        return (int)doc.PageCount;
    }

    public async Task<RenderedPage> RenderPageAsync(string path, int pageIndex, double dpi, string? password, CancellationToken cancellationToken)
    {
        var doc = await LoadAsync(path, password);
        using var page = doc.GetPage((uint)pageIndex);

        var scale = dpi / 96.0;
        var options = new WinPdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * scale)),
        };

        using var memoryStream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(memoryStream, options).AsTask(cancellationToken);
        memoryStream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(memoryStream);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var buffer = new byte[4 * softwareBitmap.PixelWidth * softwareBitmap.PixelHeight];
        softwareBitmap.CopyToBuffer(buffer.AsBuffer());

        return new RenderedPage(softwareBitmap.PixelWidth, softwareBitmap.PixelHeight, buffer);
    }

    private static async Task<WinPdfDocument> LoadAsync(string path, string? password)
    {
        // A StorageFile-backed IRandomAccessStream is the direct path for an on-disk file —
        // Sheaf's plan-based flow always has one — rather than adapting a .NET Stream through
        // an extra WinRT stream wrapper.
        var file = await StorageFile.GetFileFromPathAsync(path);
        var stream = await file.OpenReadAsync();
        return string.IsNullOrEmpty(password)
            ? await WinPdfDocument.LoadFromStreamAsync(stream)
            : await WinPdfDocument.LoadFromStreamAsync(stream, password);
    }
}
