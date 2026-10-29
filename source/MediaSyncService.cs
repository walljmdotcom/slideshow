using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SlideshowWebServer;

internal sealed class MediaSyncService : BackgroundService
{
    private readonly ILogger<MediaSyncService> logger;
    private readonly MediaService mediaService;

    public MediaSyncService(ILogger<MediaSyncService> logger, MediaService mediaService)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(mediaService);

        this.logger = logger;
        this.mediaService = mediaService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so Host.StartAsync binds Kestrel and notifies systemd before touching a stalled SMB mount.
        await Task.Yield();

        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(5, this.mediaService.Config.SyncIntervalSeconds));
        Log.SyncStarting(
            this.logger,
            this.mediaService.SourceFolderPath,
            this.mediaService.CacheFolderPath,
            interval.TotalSeconds
        );

        _ = this.mediaService.RefreshCacheIndex();

        try
        {
            await this.SyncOnceAsync(stoppingToken);

            using PeriodicTimer timer = new(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.SyncOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        string source = this.mediaService.SourceFolderPath;
        string cache = this.mediaService.CacheFolderPath;

        try
        {
            Directory.CreateDirectory(cache);
        }
        catch (Exception ex)
        {
            Log.CacheDirectoryFailed(this.logger, ex, cache);
            return;
        }

        Dictionary<string, FileInfo> sourceByOrdinal = new(StringComparer.Ordinal);
        Dictionary<string, FileInfo> sourceByIgnoreCase = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            DirectoryInfo sourceDir = new(source);
            if (!sourceDir.Exists)
            {
                Log.SourceUnavailable(this.logger, source);
                return;
            }

            foreach (FileInfo entry in sourceDir.EnumerateFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fileName = entry.Name;
                if (fileName.EndsWith(".tmp", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!MediaTypes.TryGetFromFileName(fileName, out _))
                {
                    continue;
                }

                sourceByOrdinal[fileName] = entry;
                sourceByIgnoreCase[fileName] = entry;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.SourceProbeFailed(this.logger, ex, source);
            return;
        }

        Dictionary<string, FileInfo> cacheByOrdinal = new(StringComparer.Ordinal);
        foreach (FileInfo entry in new DirectoryInfo(cache).EnumerateFiles())
        {
            if (entry.Name.EndsWith(".tmp", StringComparison.Ordinal))
            {
                TryDeleteBestEffort(entry.FullName);
                continue;
            }

            cacheByOrdinal[entry.Name] = entry;
        }

        // An unmounted fstab or autofs mountpoint appears as an empty local directory when the NAS is down.
        if (sourceByOrdinal.Count == 0 && cacheByOrdinal.Count > 0 && !this.mediaService.Config.AllowEmptySourceSync)
        {
            Log.SourceEmptyKeepingCache(this.logger, source, cacheByOrdinal.Count);
            return;
        }

        int copied = 0;
        int skipped = 0;
        int failed = 0;

        foreach ((string name, FileInfo srcInfo) in sourceByOrdinal)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (cacheByOrdinal.TryGetValue(name, out FileInfo? cachedInfo) && IsUnchanged(cachedInfo, srcInfo))
            {
                skipped++;
                continue;
            }

            try
            {
                await CopyAtomicAsync(srcInfo, Path.Combine(cache, name), cancellationToken);
                copied++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                Log.FileCopyFailed(this.logger, ex, name);
            }
        }

        int removed = 0;
        HashSet<string>? postCopyCacheNames = null;

        foreach (string cachedName in cacheByOrdinal.Keys)
        {
            if (sourceByOrdinal.ContainsKey(cachedName))
            {
                continue;
            }

            if (sourceByIgnoreCase.TryGetValue(cachedName, out FileInfo? caseRenamedSource))
            {
                // Delete the old casing only when both casings exist as separate files on a case-sensitive filesystem.
                postCopyCacheNames ??= ReadDirectoryFileNamesOrdinal(cache);
                if (!postCopyCacheNames.Contains(cachedName) || !postCopyCacheNames.Contains(caseRenamedSource.Name))
                {
                    continue;
                }
            }

            try
            {
                File.Delete(Path.Combine(cache, cachedName));
                removed++;
            }
            catch (Exception ex)
            {
                Log.StaleFileDeleteFailed(this.logger, ex, cachedName);
            }
        }

        if (copied > 0 || removed > 0)
        {
            _ = this.mediaService.RefreshCacheIndex();
        }

        Log.SyncCompleted(this.logger, copied, skipped, removed, failed, sourceByOrdinal.Count);
    }

    private static bool IsUnchanged(FileInfo cached, FileInfo source) =>
        cached.Length == source.Length
        && Math.Abs((cached.LastWriteTimeUtc - source.LastWriteTimeUtc).Ticks) < TimeSpan.TicksPerMillisecond;

    private static HashSet<string> ReadDirectoryFileNamesOrdinal(string directoryPath)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string filePath in Directory.EnumerateFiles(directoryPath))
        {
            string name = Path.GetFileName(filePath);
            if (!name.EndsWith(".tmp", StringComparison.Ordinal))
            {
                _ = names.Add(name);
            }
        }

        return names;
    }

    private static async Task CopyAtomicAsync(
        FileInfo source,
        string destinationPath,
        CancellationToken cancellationToken
    )
    {
        string tempPath = destinationPath + ".tmp";

        try
        {
            FileStreamOptions readOptions = new()
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 81920,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            };
            FileStreamOptions writeOptions = new()
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 81920,
                Options = FileOptions.Asynchronous,
            };

            await using (FileStream src = new(source.FullName, readOptions))
            await using (FileStream dst = new(tempPath, writeOptions))
            {
                await src.CopyToAsync(dst, cancellationToken);
            }

            try
            {
                File.SetLastWriteTimeUtc(tempPath, source.LastWriteTimeUtc);
            }
            catch
            {
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDeleteBestEffort(tempPath);
            throw;
        }
    }

    private static void TryDeleteBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
