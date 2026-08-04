using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Nullprice.Haul.Core;

namespace Nullprice.Haul.Core.Tests;

public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "haul-tests", Guid.NewGuid().ToString("n"));

    public string Out => Ensure(Path.Combine(Root, "out"));

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
        catch { }
    }
}

/// <summary>Routes every request through a caller-supplied function, so no test touches the network.</summary>
public sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}

public class HaulPlannerTests
{
    private static HaulJob Job(string url, string outputPath) =>
        new(url, new HaulFormat("direct", "mp4", "video", null, null, url), outputPath, null, null);

    [Fact]
    public void Empty_queue_is_a_problem_not_a_crash()
    {
        var plan = HaulPlanner.Build([]);
        Assert.False(plan.IsRunnable);
        Assert.Contains(plan.Problems, p => p.Message.Contains("Nothing queued"));
    }

    [Fact]
    public void Single_job_is_runnable()
    {
        using var box = new Sandbox();
        var plan = HaulPlanner.Build([Job("https://example.com/a.mp4", Path.Combine(box.Out, "a.mp4"))]);

        Assert.True(plan.IsRunnable);
        Assert.Equal(1, plan.Total);
    }

    [Fact]
    public void Two_jobs_colliding_on_one_output_path_are_refused()
    {
        using var box = new Sandbox();
        var destination = Path.Combine(box.Out, "a.mp4");

        var plan = HaulPlanner.Build([
            Job("https://example.com/a.mp4", destination),
            Job("https://example.com/b.mp4", destination),
        ]);

        Assert.False(plan.IsRunnable);
        Assert.Contains(plan.Problems, p => p.Message.Contains("written twice"));
    }
}

public class HaulRunnerTests
{
    private static HaulJob Job(string url, string outputPath) =>
        new(url, new HaulFormat("direct", "mp4", "video", null, null, url), outputPath, null, null);

    [Fact]
    public async Task Downloads_the_bytes_and_leaves_them_at_the_final_name()
    {
        using var box = new Sandbox();
        var bytes = new byte[10_000];
        Random.Shared.NextBytes(bytes);

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(bytes)),
        });
        using var client = new HttpClient(handler);

        var destination = Path.Combine(box.Out, "a.mp4");
        var plan = HaulPlanner.Build([Job("https://example.com/a.mp4", destination)]);
        var report = await new HaulRunner(client).RunAsync(plan);

        Assert.True(report.IsClean);
        Assert.Equal(1, report.Downloaded);
        Assert.True(File.Exists(destination));
        Assert.False(File.Exists(destination + ".haulpart"));
        Assert.Equal(bytes, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task One_failed_link_does_not_abandon_the_rest()
    {
        using var box = new Sandbox();

        var handler = new FakeHttpMessageHandler(request =>
            request.RequestUri!.AbsoluteUri.Contains("broken")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream([1, 2, 3])) });
        using var client = new HttpClient(handler);

        var plan = HaulPlanner.Build([
            Job("https://example.com/good1.mp4", Path.Combine(box.Out, "good1.mp4")),
            Job("https://example.com/broken.mp4", Path.Combine(box.Out, "broken.mp4")),
            Job("https://example.com/good2.mp4", Path.Combine(box.Out, "good2.mp4")),
        ]);

        var report = await new HaulRunner(client).RunAsync(plan);

        Assert.False(report.IsClean);
        Assert.Equal(2, report.Downloaded);
        Assert.Equal(1, report.Failed);
        Assert.Contains(report.Results, r => r.Outcome == HaulOutcome.Failed && r.Error is not null);
    }

    [Fact]
    public async Task Cancellation_mid_download_leaves_no_partial_file_at_the_final_name()
    {
        using var box = new Sandbox();
        var bytes = new byte[300_000]; // several buffer-sized chunks, so more than one progress report fires
        Random.Shared.NextBytes(bytes);

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(bytes)),
        });
        using var client = new HttpClient(handler);

        var destination = Path.Combine(box.Out, "a.mp4");
        var plan = HaulPlanner.Build([Job("https://example.com/a.mp4", destination)]);

        using var cts = new CancellationTokenSource();
        var seen = 0;
        var progress = new Progress<HaulProgress>(_ =>
        {
            if (Interlocked.Increment(ref seen) > 2) cts.Cancel();
        });

        var report = await new HaulRunner(client).RunAsync(plan, progress, cts.Token);

        Assert.True(report.WasCancelled);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".haulpart"));
    }
}

public class GenericMediaProberTests
{
    private static HttpResponseMessage MediaHeadResponse(string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    private static HttpResponseMessage HtmlPageResponse(string html)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        return response;
    }

    [Fact]
    public async Task A_direct_media_link_resolves_from_the_head_response_alone()
    {
        var handler = new FakeHttpMessageHandler(_ => MediaHeadResponse("video/mp4"));
        using var client = new HttpClient(handler);

        var result = await new GenericMediaProber(client).ProbeAsync("https://example.com/clip.mp4", default);

        Assert.Single(result.Formats);
        Assert.Equal("https://example.com/clip.mp4", result.Formats[0].DirectUrl);
        Assert.Empty(result.SkippedReasons);
    }

    [Fact]
    public async Task An_og_video_tag_is_extracted_from_the_page()
    {
        const string html = """
            <html><head>
            <title>A clip</title>
            <meta property="og:video" content="https://cdn.example.com/clip.mp4">
            </head><body></body></html>
            """;

        var handler = new FakeHttpMessageHandler(request =>
            request.Method == HttpMethod.Head ? HtmlPageResponse("") : HtmlPageResponse(html));
        using var client = new HttpClient(handler);

        var result = await new GenericMediaProber(client).ProbeAsync("https://example.com/watch", default);

        Assert.Single(result.Formats);
        Assert.Equal("https://cdn.example.com/clip.mp4", result.Formats[0].DirectUrl);
        Assert.Equal("A clip", result.Title);
    }

    [Fact]
    public async Task A_video_tag_with_a_relative_src_resolves_against_the_page_url()
    {
        const string html = """<html><body><video src="/media/clip.mp4"></video></body></html>""";

        var handler = new FakeHttpMessageHandler(request =>
            request.Method == HttpMethod.Head ? HtmlPageResponse("") : HtmlPageResponse(html));
        using var client = new HttpClient(handler);

        var result = await new GenericMediaProber(client).ProbeAsync("https://example.com/watch", default);

        Assert.Single(result.Formats);
        Assert.Equal("https://example.com/media/clip.mp4", result.Formats[0].DirectUrl);
    }

    [Fact]
    public async Task A_page_with_nothing_usable_is_refused_by_name()
    {
        var handler = new FakeHttpMessageHandler(request =>
            request.Method == HttpMethod.Head
                ? HtmlPageResponse("")
                : HtmlPageResponse("<html><body>Nothing to see here.</body></html>"));
        using var client = new HttpClient(handler);

        var result = await new GenericMediaProber(client).ProbeAsync("https://example.com/blog-post", default);

        Assert.Empty(result.Formats);
        Assert.Contains(result.SkippedReasons, r => r.Contains("YouTube links and direct audio/video file links"));
    }

    [Fact]
    public async Task An_invalid_url_is_refused_without_making_a_request()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("Should not be called for an invalid URL."));
        using var client = new HttpClient(handler);

        var result = await new GenericMediaProber(client).ProbeAsync("not a url", default);

        Assert.Empty(result.Formats);
        Assert.NotEmpty(result.SkippedReasons);
    }
}
