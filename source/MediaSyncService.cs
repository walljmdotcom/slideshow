using System.Buffers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SlideshowWebServer;

internal readonly record struct SourceMediaEntry(
    string Name,
    string FullPath,
    long Length,
    DateTime LastWriteTimeUtc
);

internal sealed class MediaSyncService : BackgroundService
{
    private const int CopyBufferSize = 81920;
    private static readonly TimeSpan DefaultReadStallTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<MediaSyncService> logger;
    private readonly MediaService mediaService;
    private readonly TimeSpan readStallTimeout;

    public MediaSyncService(
        ILogger<MediaSyncService> logger,
        MediaService mediaService,
        TimeSpan? readStallTimeout = null
    )
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(mediaService);

        TimeSpan resolvedTimeout = readStallTimeout ?? DefaultReadStallTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(resolvedTimeout, TimeSpan.Zero);

        this.logger = logger;
        this.mediaService = mediaService;
        this.readStallTimeout = resolvedTimeout;
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

        await this.RunSafeSyncIterationAsync(stoppingToken);

        try
        {
            using PeriodicTimer timer = new(interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await this.RunSafeSyncIterationAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunSafeSyncIterationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.SyncOnceAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.SyncIterationFailed(this.logger, ex);
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

        HashSet<string> seenSourceOrdinal = new(StringComparer.Ordinal);
        Dictionary<string, string> seenSourceIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
        List<SourceMediaEntry> readableSources = [];
        int failed = 0;

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
                if (MediaTypes.IsTemporaryCacheArtifact(fileName))
                {
                    continue;
                }

                if (!MediaTypes.TryGetFromFileName(fileName, out _))
                {
                    continue;
                }

                // Record the name before stat so an unreadable entry or dangling symlink keeps its cached copy.
                _ = seenSourceOrdinal.Add(fileName);
                seenSourceIgnoreCase[fileName] = fileName;

                try
                {
                    readableSources.Add(
                        new SourceMediaEntry(fileName, entry.FullName, entry.Length, entry.LastWriteTimeUtc)
                    );
                }
                catch (Exception ex)
                {
                    failed++;
                    Log.SourceEntryStatFailed(this.logger, ex, fileName);
                }
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
            if (MediaTypes.IsTemporaryCacheArtifact(entry.Name))
            {
                TryDeleteBestEffort(entry.FullName);
                continue;
            }

            cacheByOrdinal[entry.Name] = entry;
        }

        // An unmounted fstab or autofs mountpoint appears as an empty local directory when the NAS is down.
        if (seenSourceOrdinal.Count == 0 && cacheByOrdinal.Count > 0 && !this.mediaService.Config.AllowEmptySourceSync)
        {
            Log.SourceEmptyKeepingCache(this.logger, source, cacheByOrdinal.Count);
            return;
        }

        int removed = 0;
        List<string> deferredCaseRenames = [];

        // Delete removed files before copying new ones so swaps do not exhaust small kiosk disks.
        foreach (string cachedName in cacheByOrdinal.Keys)
        {
            if (seenSourceOrdinal.Contains(cachedName))
            {
                continue;
            }

            if (seenSourceIgnoreCase.ContainsKey(cachedName))
            {
                deferredCaseRenames.Add(cachedName);
                continue;
            }

            if (this.TryDeleteCachedFile(Path.Combine(cache, cachedName), cachedName))
            {
                removed++;
            }
        }

        List<SourceMediaEntry> pendingCopies = [];
        int skipped = 0;

        foreach (SourceMediaEntry srcEntry in readableSources)
        {
            if (cacheByOrdinal.TryGetValue(srcEntry.Name, out FileInfo? cachedInfo)
                && IsUnchanged(cachedInfo, srcEntry))
            {
                skipped++;
                continue;
            }

            pendingCopies.Add(srcEntry);
        }

        // Copy smallest files first so images publish immediately on cold boot before multi-hundred-MB videos finish.
        pendingCopies.Sort(static (a, b) => a.Length.CompareTo(b.Length));

        int copied = 0;
        foreach (SourceMediaEntry srcEntry in pendingCopies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await this.CopyAtomicAsync(srcEntry, Path.Combine(cache, srcEntry.Name), cancellationToken);
                copied++;

                if (!this.mediaService.HasCachedFiles)
                {
                    _ = this.mediaService.RefreshCacheIndex();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                Log.FileCopyFailed(this.logger, ex, srcEntry.Name);
            }
        }

        if (deferredCaseRenames.Count > 0)
        {
            HashSet<string> postCopyCacheNames = ReadDirectoryFileNamesOrdinal(cache);
            foreach (string oldCasing in deferredCaseRenames)
            {
                // Delete the old casing only when both casings exist as separate files on a case-sensitive filesystem.
                if (!seenSourceIgnoreCase.TryGetValue(oldCasing, out string? newCasing)
                    || !postCopyCacheNames.Contains(oldCasing)
                    || !postCopyCacheNames.Contains(newCasing))
                {
                    continue;
                }

                if (this.TryDeleteCachedFile(Path.Combine(cache, oldCasing), oldCasing))
                {
                    removed++;
                }
            }
        }

        if (copied > 0 || removed > 0)
        {
            _ = this.mediaService.RefreshCacheIndex();
        }

        Log.SyncCompleted(this.logger, copied, skipped, removed, failed, seenSourceOrdinal.Count);
    }

    internal static async Task CopyStreamWithTimeoutAsync(
        Stream source,
        Stream destination,
        TimeSpan readStallTimeout,
        CancellationToken cancellationToken
    )
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        using CancellationTokenSource stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            while (true)
            {
                stallCts.CancelAfter(readStallTimeout);
                int bytesRead;
                try
                {
                    bytesRead = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), stallCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Source stream stalled for more than {readStallTimeout.TotalSeconds:0.#}s."
                    );
                }

                if (bytesRead == 0)
                {
                    break;
                }

                stallCts.CancelAfter(Timeout.InfiniteTimeSpan);
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsUnchanged(FileInfo cached, SourceMediaEntry source)
    {
        try
        {
            return cached.Length == source.Length
                && Math.Abs((cached.LastWriteTimeUtc - source.LastWriteTimeUtc).Ticks) < TimeSpan.TicksPerMillisecond;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static HashSet<string> ReadDirectoryFileNamesOrdinal(string directoryPath)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string filePath in Directory.EnumerateFiles(directoryPath))
        {
            string name = Path.GetFileName(filePath);
            if (!MediaTypes.IsTemporaryCacheArtifact(name))
            {
                _ = names.Add(name);
            }
        }

        return names;
    }

    private async Task CopyAtomicAsync(
        SourceMediaEntry source,
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
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = CopyBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            };
            FileStreamOptions writeOptions = new()
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = CopyBufferSize,
                Options = FileOptions.Asynchronous,
            };

            await using (FileStream src = new(source.FullPath, readOptions))
            await using (FileStream dst = new(tempPath, writeOptions))
            {
                await CopyStreamWithTimeoutAsync(src, dst, this.readStallTimeout, cancellationToken);
            }

            try
            {
                File.SetLastWriteTimeUtc(tempPath, source.LastWriteTimeUtc);
            }
            catch
            {
            }

            MoveIntoPlace(tempPath, destinationPath);
        }
        catch
        {
            TryDeleteBestEffort(tempPath);
            throw;
        }
    }

    private static void MoveIntoPlace(string tempPath, string destinationPath)
    {
        try
        {
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch (IOException) when (OperatingSystem.IsWindows() && File.Exists(destinationPath))
        {
            // An active stream opened with FileShare.Delete allows renaming the open target aside before replacing it.
            string displacedPath = $"{destinationPath}.{Guid.NewGuid():N}.old";
            File.Move(destinationPath, displacedPath, overwrite: true);
            try
            {
                File.Move(tempPath, destinationPath, overwrite: true);
            }
            finally
            {
                TryDeleteBestEffort(displacedPath);
            }
        }
    }

    private bool TryDeleteCachedFile(string fullPath, string fileName)
    {
        try
        {
            File.Delete(fullPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.StaleFileDeleteFailed(this.logger, ex, fileName);
            return false;
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
