using System.Net.Http;
using System.Text.RegularExpressions;

namespace Nullprice.Haul.Core;

/// <summary>
/// Resolves a URL into downloadable formats. Implemented by <see cref="GenericMediaProber"/> for
/// direct file links and simple video pages, and (from M2) by a YouTube-specific prober — the
/// App tries each registered prober in turn and uses the first that claims the URL.
/// </summary>
public interface IMediaProber
{
    bool CanHandle(string url);
    Task<ProbeResult> ProbeAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// Handles two cases, deliberately not more: a URL that already points straight at an audio or
/// video file (checked via a HEAD request's Content-Type), and a plain web page exposing its
/// video through an <c>og:video</c> meta tag or a <c>&lt;video&gt;</c>/<c>&lt;source&gt;</c> tag.
/// Anything else is refused by name rather than guessed at — this is not a general per-site
/// extractor.
/// </summary>
public sealed class GenericMediaProber(HttpClient httpClient) : IMediaProber
{
    private const string RefusalMessage = "Haul supports YouTube links and direct audio/video file links.";
    private const int MaxHtmlScanBytes = 512 * 1024;

    private static readonly Regex OgVideoTag = new(
        """<meta[^>]+property=["']og:video(?::url|:secure_url)?["'][^>]+content=["']([^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VideoSrcTag = new(
        """<video[^>]+src=["']([^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SourceSrcTag = new(
        """<source[^>]+src=["']([^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleTag = new(
        """<title>([^<]+)</title>""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool CanHandle(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public async Task<ProbeResult> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !CanHandle(url))
        {
            return Refused();
        }

        var direct = await TryAsDirectMediaAsync(uri, cancellationToken).ConfigureAwait(false);
        if (direct is not null) return direct;

        return await TryAsVideoPageAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProbeResult?> TryAsDirectMediaAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null) return null;

            var isAudio = mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
            var isVideo = mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
            if (!isAudio && !isVideo) return null;

            var container = mediaType.Contains('/') ? mediaType[(mediaType.IndexOf('/') + 1)..] : mediaType;
            var format = new HaulFormat(
                "direct", container, isAudio ? "audio" : "video", null, null, uri.ToString());

            var title = Path.GetFileName(uri.LocalPath) is { Length: > 0 } name ? name : uri.Host;
            return new ProbeResult(title, null, [format], []);
        }
        catch (HttpRequestException)
        {
            // Some servers reject HEAD outright. Fall through to the GET-based page scan.
            return null;
        }
    }

    private async Task<ProbeResult> TryAsVideoPageAsync(Uri uri, CancellationToken cancellationToken)
    {
        string html;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return Refused();

            html = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return Refused();
        }

        var found = FirstMatch(html, OgVideoTag) ?? FirstMatch(html, VideoSrcTag) ?? FirstMatch(html, SourceSrcTag);
        if (found is null) return Refused();

        if (!Uri.TryCreate(uri, found, out var resolved)) return Refused();

        var extension = Path.GetExtension(resolved.LocalPath).TrimStart('.').ToLowerInvariant();
        var container = string.IsNullOrEmpty(extension) ? "mp4" : extension;

        var format = new HaulFormat("direct", container, "unknown", null, null, resolved.ToString());
        var title = TitleTag.Match(html) is { Success: true } titleMatch
            ? titleMatch.Groups[1].Value.Trim()
            : uri.Host;

        return new ProbeResult(title, null, [format], []);
    }

    private static string? FirstMatch(string html, Regex pattern)
    {
        var match = pattern.Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var buffer = new char[MaxHtmlScanBytes];
        var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new string(buffer, 0, read);
    }

    private static ProbeResult Refused() => new(string.Empty, null, [], [RefusalMessage]);
}
