using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SlideshowWebServer.Tests;

public sealed class HttpEndpointsTests
{
    [Fact]
    public async Task ApiEndpoints_OmitInternalServerPathsAndReturnVersionedFiles()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile(workspace.CachePath, "hero.png", [137, 80, 78, 71]);

        await using WebApplication app = await StartServerAsync(workspace);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage configResponse = await client.GetAsync("/api/config");
        Assert.Equal(HttpStatusCode.OK, configResponse.StatusCode);
        Assert.Equal("nosniff", string.Join(",", configResponse.Headers.GetValues("X-Content-Type-Options")));

        string configBody = await configResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("folderPath", configBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cacheFolderPath", configBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("syncIntervalSeconds", configBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(workspace.RootPath, configBody, StringComparison.OrdinalIgnoreCase);

        using JsonDocument configDoc = JsonDocument.Parse(configBody);
        Assert.Equal(7, configDoc.RootElement.GetProperty("imageDuration").GetInt32());
        Assert.Equal(2, configDoc.RootElement.GetProperty("fadeTransitionDuration").GetInt32());

        using HttpResponseMessage filesResponse = await client.GetAsync("/api/files");
        Assert.Equal(HttpStatusCode.OK, filesResponse.StatusCode);

        string filesBody = await filesResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("path", filesBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(workspace.RootPath, filesBody, StringComparison.OrdinalIgnoreCase);

        using JsonDocument filesDoc = JsonDocument.Parse(filesBody);
        JsonElement first = filesDoc.RootElement[0];
        Assert.Equal("hero.png", first.GetProperty("name").GetString());
        Assert.Equal("image", first.GetProperty("type").GetString());
        string? version = first.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));

        using HttpResponseMessage immutableResponse = await client.GetAsync($"/media/hero.png?v={version}");
        Assert.Equal(HttpStatusCode.OK, immutableResponse.StatusCode);
        CacheControlHeaderValue? immutableCache = immutableResponse.Headers.CacheControl;
        Assert.NotNull(immutableCache);
        Assert.True(immutableCache.Private);
        Assert.Equal(TimeSpan.FromSeconds(31536000), immutableCache.MaxAge);
        Assert.Contains(immutableCache.Extensions, static ext => ext.Name == "immutable");

        using HttpResponseMessage staleQueryResponse = await client.GetAsync("/media/hero.png?v=stale");
        Assert.Equal(HttpStatusCode.OK, staleQueryResponse.StatusCode);
        Assert.True(staleQueryResponse.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task MediaEndpoint_SupportsHeadConditional304AndIfRangePartialContentWithoutAttachmentHeader()
    {
        using TempWorkspace workspace = new();
        DateTime mtime = new(2024, 3, 15, 10, 30, 0, DateTimeKind.Utc);
        byte[] videoBytes = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        workspace.WriteFile(workspace.CachePath, "clip.mp4", videoBytes, mtime);

        await using WebApplication app = await StartServerAsync(workspace);
        using HttpClient client = CreateClient(app);

        using HttpRequestMessage headRequest = new(HttpMethod.Head, "/media/clip.mp4");
        using HttpResponseMessage headResponse = await client.SendAsync(headRequest);
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Equal(videoBytes.Length, headResponse.Content.Headers.ContentLength);
        Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync());

        using HttpResponseMessage fullResponse = await client.GetAsync("/media/clip.mp4");
        Assert.Equal(HttpStatusCode.OK, fullResponse.StatusCode);
        Assert.Null(fullResponse.Content.Headers.ContentDisposition);
        Assert.Equal("no-cache", fullResponse.Headers.CacheControl?.ToString());
        Assert.Equal(new DateTimeOffset(mtime), fullResponse.Content.Headers.LastModified);
        EntityTagHeaderValue? etag = fullResponse.Headers.ETag;
        Assert.NotNull(etag);

        using HttpRequestMessage revalRequest = new(HttpMethod.Get, "/media/clip.mp4");
        revalRequest.Headers.IfNoneMatch.Add(etag);
        using HttpResponseMessage revalResponse = await client.SendAsync(revalRequest);
        Assert.Equal(HttpStatusCode.NotModified, revalResponse.StatusCode);
        Assert.Empty(await revalResponse.Content.ReadAsByteArrayAsync());

        using HttpRequestMessage firstRange = new(HttpMethod.Get, "/media/clip.mp4");
        firstRange.Headers.Range = new RangeHeaderValue(0, 3);
        using HttpResponseMessage firstRangeResponse = await client.SendAsync(firstRange);
        Assert.Equal(HttpStatusCode.PartialContent, firstRangeResponse.StatusCode);
        Assert.Equal([0, 1, 2, 3], await firstRangeResponse.Content.ReadAsByteArrayAsync());

        DateTimeOffset? firstLastModified = firstRangeResponse.Content.Headers.LastModified;
        Assert.NotNull(firstLastModified);

        using HttpRequestMessage secondRange = new(HttpMethod.Get, "/media/clip.mp4");
        secondRange.Headers.Range = new RangeHeaderValue(4, 7);
        secondRange.Headers.IfRange = new RangeConditionHeaderValue(firstLastModified.Value);
        using HttpResponseMessage secondRangeResponse = await client.SendAsync(secondRange);
        Assert.Equal(HttpStatusCode.PartialContent, secondRangeResponse.StatusCode);
        Assert.Equal([4, 5, 6, 7], await secondRangeResponse.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MediaEndpoint_SandboxesSvgAndBlocksTraversal()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile(workspace.CachePath, "graphic.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8);
        string sibling = workspace.CreateDirectory("cache-extra");
        workspace.WriteFile(sibling, "secret.png", [1, 2, 3]);

        await using WebApplication app = await StartServerAsync(workspace);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage svgResponse = await client.GetAsync("/media/graphic.svg");
        Assert.Equal(HttpStatusCode.OK, svgResponse.StatusCode);
        Assert.Equal("image/svg+xml", svgResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "sandbox",
            string.Join(",", svgResponse.Headers.GetValues("Content-Security-Policy")),
            StringComparison.Ordinal
        );

        using HttpResponseMessage traversalResponse = await client.GetAsync("/media/..%5ccache-extra%5csecret.png");
        Assert.Equal(HttpStatusCode.NotFound, traversalResponse.StatusCode);
    }

    [Fact]
    public async Task EmbeddedStaticAssets_ServeIndexCssJsWithETagAndReturnConstant404()
    {
        using TempWorkspace workspace = new();
        Directory.CreateDirectory(workspace.CachePath);

        await using WebApplication app = await StartServerAsync(workspace);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage indexResponse = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        Assert.Equal("text/html", indexResponse.Content.Headers.ContentType?.MediaType);
        EntityTagHeaderValue? etag = indexResponse.Headers.ETag;
        Assert.NotNull(etag);

        using HttpRequestMessage cachedIndexReq = new(HttpMethod.Get, "/");
        cachedIndexReq.Headers.IfNoneMatch.Add(etag);
        using HttpResponseMessage cachedIndexResp = await client.SendAsync(cachedIndexReq);
        Assert.Equal(HttpStatusCode.NotModified, cachedIndexResp.StatusCode);

        using HttpResponseMessage cssResponse = await client.GetAsync("/styles.css");
        Assert.Equal(HttpStatusCode.OK, cssResponse.StatusCode);
        Assert.Equal("text/css", cssResponse.Content.Headers.ContentType?.MediaType);

        using HttpResponseMessage jsResponse = await client.GetAsync("/script.js");
        Assert.Equal(HttpStatusCode.OK, jsResponse.StatusCode);
        Assert.Equal("text/javascript", jsResponse.Content.Headers.ContentType?.MediaType);

        using HttpResponseMessage missingResponse = await client.GetAsync("/<script>alert(1)</script>");
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        string notFoundBody = await missingResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>", notFoundBody, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient CreateClient(WebApplication app) =>
        new() { BaseAddress = new Uri(app.Urls.First()) };

    private static async Task<WebApplication> StartServerAsync(TempWorkspace workspace)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        SlideshowConfig config = new()
        {
            ImageDuration = 7,
            FadeTransitionDuration = 2,
            FolderPath = workspace.SourcePath,
            CacheFolderPath = workspace.CachePath,
        };

        builder.Services.Configure<JsonOptions>(static options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, JsonContext.Default);
        });
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<MediaService>(sp =>
            new MediaService(
                config,
                sp.GetRequiredService<ILogger<MediaService>>(),
                workspace.RootPath
            )
        );
        builder.Services.AddSingleton<EmbeddedFileProvider>();

        WebApplication app = builder.Build();
        app.AddSlideshowEndpoints();
        await app.StartAsync();
        return app;
    }
}
