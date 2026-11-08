using System.Text.Json;

namespace SlideshowWebServer;

/// <summary>The source file and resize settings a cached image was produced from.</summary>
internal sealed record ImageCacheEntry(
    long SourceLength,
    long SourceLastWriteTicks,
    int MaxWidth,
    int MaxHeight,
    long CachedLength
);

/// <summary>
/// Records how each cached image was produced. A resized image no longer matches its source's length, so the
/// sync's length-and-mtime comparison cannot tell whether it is current.
/// </summary>
internal sealed class ImageCacheManifest
{
    public const string FileName = ".image-cache.json";

    private readonly string path;
    private readonly Dictionary<string, ImageCacheEntry> entries;
    private bool changed;

    private ImageCacheManifest(string path, Dictionary<string, ImageCacheEntry> entries)
    {
        this.path = path;
        this.entries = entries;
    }

    /// <summary>Loads the manifest in <paramref name="cacheFolder"/>. A missing or bad one loads empty.</summary>
    public static async Task<ImageCacheManifest> LoadAsync(string cacheFolder, CancellationToken cancellationToken)
    {
        string path = Path.Combine(cacheFolder, FileName);
        Dictionary<string, ImageCacheEntry>? entries = null;
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            entries = await JsonSerializer.DeserializeAsync(
                stream,
                JsonContext.Default.DictionaryStringImageCacheEntry,
                cancellationToken
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Every image is re-processed once, which rebuilds the manifest.
        }

        Dictionary<string, ImageCacheEntry> loaded = new(StringComparer.Ordinal);
        if (entries is not null)
        {
            foreach ((string name, ImageCacheEntry? entry) in entries)
            {
                if (entry is not null)
                {
                    loaded[name] = entry;
                }
            }
        }

        return new ImageCacheManifest(path, loaded);
    }

    public bool IsCurrent(string name, SourceMediaEntry source, ImageBounds bounds, long cachedLength) =>
        this.entries.TryGetValue(name, out ImageCacheEntry? entry)
        && entry.SourceLength == source.Length
        && Math.Abs(entry.SourceLastWriteTicks - source.LastWriteTimeUtc.Ticks) < TimeSpan.TicksPerMillisecond
        && entry.MaxWidth == bounds.MaxWidth
        && entry.MaxHeight == bounds.MaxHeight
        && entry.CachedLength == cachedLength;

    public void Record(string name, SourceMediaEntry source, ImageBounds bounds, long cachedLength)
    {
        this.entries[name] = new ImageCacheEntry(
            source.Length,
            source.LastWriteTimeUtc.Ticks,
            bounds.MaxWidth,
            bounds.MaxHeight,
            cachedLength
        );
        this.changed = true;
    }

    /// <summary>Drops entries for images no longer in the source.</summary>
    public void RetainOnly(IReadOnlySet<string> names)
    {
        foreach (string name in this.entries.Keys)
        {
            if (!names.Contains(name))
            {
                _ = this.entries.Remove(name);
                this.changed = true;
            }
        }
    }

    /// <summary>Writes the manifest when it changed, via a temp file so a crash never leaves it half-written.</summary>
    public async Task SaveIfChangedAsync(CancellationToken cancellationToken)
    {
        if (!this.changed)
        {
            return;
        }

        string tempPath = this.path + ".tmp";
        try
        {
            await using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    this.entries,
                    JsonContext.Default.DictionaryStringImageCacheEntry,
                    cancellationToken
                );
            }

            File.Move(tempPath, this.path, overwrite: true);
            this.changed = false;
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
            }

            throw;
        }
    }
}
