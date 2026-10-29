using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;

namespace SlideshowWebServer;

public sealed record EmbeddedAsset(byte[] Content, string ContentType, EntityTagHeaderValue ETag);

public sealed class EmbeddedFileProvider
{
    private const string ResourcePrefix = "web/";
    private readonly FrozenDictionary<string, EmbeddedAsset> assets;

    public EmbeddedFileProvider()
    {
        Assembly assembly = typeof(EmbeddedFileProvider).Assembly;
        Dictionary<string, EmbeddedAsset> loaded = new(StringComparer.Ordinal);
        Span<byte> hash = stackalloc byte[32];

        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            string normalized = resourceName.Replace('\\', '/');
            if (!normalized.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string relativePath = normalized[ResourcePrefix.Length..];
            if (relativePath.Length == 0)
            {
                continue;
            }

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            byte[] content = new byte[stream.Length];
            stream.ReadExactly(content);

            SHA256.HashData(content, hash);
            EntityTagHeaderValue etag = new($"\"{Convert.ToHexStringLower(hash[..8])}\"");

            loaded[relativePath] = new EmbeddedAsset(content, GetContentType(relativePath), etag);
        }

        this.assets = loaded.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public bool TryGetAsset(string path, [NotNullWhen(true)] out EmbeddedAsset? asset)
    {
        string normalized = path.TrimStart('/');
        return this.assets.TryGetValue(normalized, out asset);
    }

    private static string GetContentType(ReadOnlySpan<char> path)
    {
        ReadOnlySpan<char> ext = Path.GetExtension(path);
        return ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream",
        };
    }
}
