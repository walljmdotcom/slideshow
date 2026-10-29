namespace SlideshowWebServer.Tests;

internal sealed class TempWorkspace : IDisposable
{
    public string RootPath { get; }
    public string SourcePath { get; }
    public string CachePath { get; }

    public TempWorkspace()
    {
        this.RootPath = Path.Combine(Path.GetTempPath(), $"slideshow-tests-{Guid.NewGuid():N}");
        this.SourcePath = Path.Combine(this.RootPath, "source");
        this.CachePath = Path.Combine(this.RootPath, "cache");
        Directory.CreateDirectory(this.RootPath);
    }

    public string CreateDirectory(string relativePath)
    {
        string fullPath = Path.Combine(this.RootPath, relativePath);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    public string WriteFile(
        string directory,
        string fileName,
        ReadOnlySpan<byte> content,
        DateTime? lastWriteUtc = null
    )
    {
        Directory.CreateDirectory(directory);
        string fullPath = Path.Combine(directory, fileName);
        using (FileStream stream = new(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
        }

        if (lastWriteUtc is { } timestamp)
        {
            File.SetLastWriteTimeUtc(fullPath, DateTime.SpecifyKind(timestamp, DateTimeKind.Utc));
        }

        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.RootPath))
            {
                Directory.Delete(this.RootPath, recursive: true);
            }
        }
        catch
        {
        }
    }
}
