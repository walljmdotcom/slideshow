using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace SlideshowWebServer;

public static class EndpointExtensions
{
    private const string SvgContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
    private static readonly string[] GetAndHeadMethods = ["GET", "HEAD"];

    public static void AddSlideshowEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.Use(static (HttpContext context, RequestDelegate next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return next(context);
        });

        RouteGroupBuilder api = app.MapGroup("/api");
        api.MapGet(
            "config",
            static (MediaService mediaService) =>
                TypedResults.Json(mediaService.ClientConfig, JsonContext.Default.SlideshowClientConfig)
        );
        api.MapGet(
            "files",
            static (MediaService mediaService) =>
                TypedResults.Json(mediaService.GetMediaFiles(), JsonContext.Default.MediaFileArray)
        );

        app.MapMethods("/media/{fileName}", GetAndHeadMethods, GetMediaFile);
        app.MapMethods("/{*path}", GetAndHeadMethods, GetEmbeddedFile);
    }

    private static Results<FileStreamHttpResult, NotFound<string>> GetMediaFile(
        HttpContext httpContext,
        MediaService mediaService,
        string fileName
    )
    {
        if (!mediaService.TryOpenMediaFile(fileName, out MediaStreamResult media))
        {
            return TypedResults.NotFound("File not found");
        }

        httpContext.Response.Headers.CacheControl = "no-cache";
        if (media.IsSvg)
        {
            httpContext.Response.Headers.ContentSecurityPolicy = SvgContentSecurityPolicy;
        }

        return TypedResults.Stream(
            media.Stream,
            contentType: media.ContentType,
            fileDownloadName: null,
            lastModified: media.LastModified,
            entityTag: media.ETag,
            enableRangeProcessing: true
        );
    }

    private static Results<FileContentHttpResult, NotFound<string>> GetEmbeddedFile(
        HttpContext httpContext,
        EmbeddedFileProvider fileProvider,
        string? path
    )
    {
        string targetPath = string.IsNullOrEmpty(path) || path == "/" ? "index.html" : path;
        if (!fileProvider.TryGetAsset(targetPath, out EmbeddedAsset? asset))
        {
            return TypedResults.NotFound("File not found");
        }

        httpContext.Response.Headers.CacheControl = "no-cache";
        return TypedResults.Bytes(
            asset.Content,
            contentType: asset.ContentType,
            fileDownloadName: null,
            enableRangeProcessing: false,
            lastModified: null,
            entityTag: asset.ETag
        );
    }
}
