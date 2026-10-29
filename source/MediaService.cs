using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace SlideshowWebServer;

internal readonly record struct MediaType(string ContentType, string Category);

internal readonly record struct ResolvedMediaFile(
    string PhysicalPath,
    string ContentType,
    DateTimeOffset LastModified,
    EntityTagHeaderValue ETag,
    string Version,
    bool IsSvg
);

internal static class MediaTypes
{
    public static bool TryGetFromFileName(ReadOnlySpan<char> fileName, out MediaType mediaType)
    {
        ReadOnlySpan<char> ext = Path.GetExtension(fileName);
        if (ext.StartsWith('.'))
        {
            ext = ext[1..];
        }

        if (ext.IsEmpty || ext.Length > 4)
        {
            mediaType = default;
            return false;
        }

        Span<char> lower = stackalloc char[4];
        int written = ext.ToLowerInvariant(lower);
        MediaType? resolved = lower[..written] switch
        {
            "png" => new MediaType("image/png", "image"),
            "jpg" or "jpeg" => new MediaType("image/jpeg", "image"),
            "gif" => new MediaType("image/gif", "image"),
            "svg" => new MediaType("image/svg+xml", "image"),
            "webp" => new MediaType("image/webp", "image"),
            "bmp" => new MediaType("image/bmp", "image"),
            "mp4" => new MediaType("video/mp4", "video"),
            "webm" => new MediaType("video/webm", "video"),
            "ogg" => new MediaType("video/ogg", "video"),
            "avi" => new MediaType("video/avi", "video"),
            "mov" => new MediaType("video/quicktime", "video"),
            _ => null,
        };

        if (resolved is { } value)
        {
            mediaType = value;
            return true;
        }

        mediaType = default;
        return false;
    }

    public static bool IsTemporaryCacheArtifact(ReadOnlySpan<char> fileName) =>
        fileName.EndsWith(".tmp", StringComparison.Ordinal)
        || fileName.EndsWith(".old", StringComparison.Ordinal);

    public static string FormatVersion(DateTime lastWriteTimeUtc, long byteLength)
    {
        DateTime utc = DateTime.SpecifyKind(lastWriteTimeUtc, DateTimeKind.Utc);
        return $"{utc.Ticks:x}-{byteLength:x}";
    }
}

public sealed class MediaService
{
    private readonly ILogger<MediaService> logger;
    private readonly string cacheRootWithSeparator;
    private readonly bool isRandomOrder;
    private MediaFile[]? cachedFiles;

    public SlideshowConfig Config { get; }
    public SlideshowClientConfig ClientConfig { get; }
    public string SourceFolderPath { get; }
    public string CacheFolderPath { get; }
    public bool HasCachedFiles => Volatile.Read(ref this.cachedFiles) is { Length: > 0 };

    public MediaService(
        SlideshowConfig config,
        ILogger<MediaService> logger,
        string? baseDirectory = null,
        string? systemdCacheDirectory = null
    )
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        string resolvedBase = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        this.logger = logger;
        this.Config = config;
        this.ClientConfig = config.ToClientConfig();
        this.SourceFolderPath = config.ResolveFolderPath(resolvedBase);
        this.CacheFolderPath = config.ResolveCacheFolderPath(resolvedBase, systemdCacheDirectory);
        this.cacheRootWithSeparator = Path.EndsInDirectorySeparator(this.CacheFolderPath)
            ? this.CacheFolderPath
            : this.CacheFolderPath + Path.DirectorySeparatorChar;
        this.isRandomOrder = string.Equals(
            this.ClientConfig.DisplayOrder,
            SlideshowConfig.RandomDisplayOrder,
            StringComparison.OrdinalIgnoreCase
        );
    }

    public MediaFile[] GetMediaFiles()
    {
        MediaFile[] snapshot = Volatile.Read(ref this.cachedFiles) ?? this.RefreshCacheIndex();
        if (!this.isRandomOrder || snapshot.Length <= 1)
        {
            return snapshot;
        }

        MediaFile[] shuffled = [.. snapshot];
        Random.Shared.Shuffle(shuffled);
        return shuffled;
    }

    public MediaFile[] RefreshCacheIndex()
    {
        try
        {
            DirectoryInfo directory = new(this.CacheFolderPath);
            if (!directory.Exists)
            {
                Log.CacheFolderMissing(this.logger, this.CacheFolderPath);
                MediaFile[] empty = [];
                Volatile.Write(ref this.cachedFiles, empty);
                return empty;
            }

            List<MediaFile> discovered = [];
            foreach (FileInfo entry in directory.EnumerateFiles())
            {
                string fileName = entry.Name;
                if (MediaTypes.IsTemporaryCacheArtifact(fileName))
                {
                    continue;
                }

                if (!MediaTypes.TryGetFromFileName(fileName, out MediaType mediaType))
                {
                    continue;
                }

                try
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    string version = MediaTypes.FormatVersion(entry.LastWriteTimeUtc, entry.Length);
                    discovered.Add(new MediaFile(fileName, mediaType.Category, version));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            discovered.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            MediaFile[] sorted = [.. discovered];
            Volatile.Write(ref this.cachedFiles, sorted);
            Log.CacheIndexed(this.logger, sorted.Length, this.CacheFolderPath);
            return sorted;
        }
        catch (Exception ex)
        {
            Log.CacheReadFailed(this.logger, ex, this.CacheFolderPath);
            return Volatile.Read(ref this.cachedFiles) ?? [];
        }
    }

    internal bool TryResolveMediaFile(string fileName, out ResolvedMediaFile result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(fileName)
            || fileName is "." or ".."
            || fileName.IndexOfAny(['/', '\\', '\0']) >= 0
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            Log.MediaRequestRejected(this.logger, fileName);
            return false;
        }

        if (MediaTypes.IsTemporaryCacheArtifact(fileName)
            || !MediaTypes.TryGetFromFileName(fileName, out MediaType mediaType))
        {
            Log.MediaRequestRejected(this.logger, fileName);
            return false;
        }

        try
        {
            string resolvedPath = Path.GetFullPath(Path.Combine(this.cacheRootWithSeparator, fileName));
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (!resolvedPath.StartsWith(this.cacheRootWithSeparator, comparison))
            {
                Log.MediaRequestRejected(this.logger, fileName);
                return false;
            }

            FileInfo fileInfo = new(resolvedPath);
            if (!fileInfo.Exists)
            {
                Log.MediaFileNotFound(this.logger, fileName);
                return false;
            }

            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Log.MediaRequestRejected(this.logger, fileName);
                return false;
            }

            DateTime utcModified = DateTime.SpecifyKind(fileInfo.LastWriteTimeUtc, DateTimeKind.Utc);
            DateTimeOffset lastModified = new(utcModified);
            string version = MediaTypes.FormatVersion(utcModified, fileInfo.Length);
            EntityTagHeaderValue etag = new($"\"{version}\"");
            bool isSvg = string.Equals(mediaType.ContentType, "image/svg+xml", StringComparison.Ordinal);

            Log.ServingMediaFile(this.logger, fileName, fileInfo.Length);
            result = new ResolvedMediaFile(
                resolvedPath,
                mediaType.ContentType,
                lastModified,
                etag,
                version,
                isSvg
            );
            return true;
        }
        catch (Exception ex)
        {
            Log.MediaOpenFailed(this.logger, ex, fileName);
            return false;
        }
    }
}
