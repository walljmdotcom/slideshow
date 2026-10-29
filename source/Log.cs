using Microsoft.Extensions.Logging;

namespace SlideshowWebServer;

internal static partial class Log
{
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Config {ConfigPath}: Source={Source} Cache={Cache} Duration={Duration}s Order={Order}"
    )]
    public static partial void ConfigurationLoaded(
        ILogger logger,
        string configPath,
        string source,
        string cache,
        int duration,
        string order
    );

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "Cache folder does not exist yet: {CacheFolderPath}"
    )]
    public static partial void CacheFolderMissing(ILogger logger, string cacheFolderPath);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Indexed {FileCount} media files in cache {CacheFolderPath}"
    )]
    public static partial void CacheIndexed(ILogger logger, int fileCount, string cacheFolderPath);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Error,
        Message = "Error reading media from cache folder: {CacheFolderPath}"
    )]
    public static partial void CacheReadFailed(ILogger logger, Exception exception, string cacheFolderPath);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Rejected invalid or out-of-bounds media request: {FileName}"
    )]
    public static partial void MediaRequestRejected(ILogger logger, string fileName);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Debug,
        Message = "Media file not found in cache: {FileName}"
    )]
    public static partial void MediaFileNotFound(ILogger logger, string fileName);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Debug,
        Message = "Serving media file {FileName} ({FileSize} bytes)"
    )]
    public static partial void ServingMediaFile(ILogger logger, string fileName, long fileSize);

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Error,
        Message = "Error opening media file: {FileName}"
    )]
    public static partial void MediaOpenFailed(ILogger logger, Exception exception, string fileName);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Information,
        Message = "MediaSyncService starting. Source={Source} Cache={Cache} Interval={IntervalSeconds}s"
    )]
    public static partial void SyncStarting(ILogger logger, string source, string cache, double intervalSeconds);

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Error,
        Message = "Could not create or access cache folder {CacheFolderPath}"
    )]
    public static partial void CacheDirectoryFailed(ILogger logger, Exception exception, string cacheFolderPath);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Warning,
        Message = "Source folder unavailable, skipping sync: {Source}"
    )]
    public static partial void SourceUnavailable(ILogger logger, string source);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Warning,
        Message = "Source folder probe failed, skipping sync: {Source}"
    )]
    public static partial void SourceProbeFailed(ILogger logger, Exception exception, string source);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Warning,
        Message = "Source folder {Source} has 0 media files while cache holds {CachedCount}; keeping cached files"
    )]
    public static partial void SourceEmptyKeepingCache(ILogger logger, string source, int cachedCount);

    [LoggerMessage(
        EventId = 14,
        Level = LogLevel.Warning,
        Message = "Failed to copy {Name} from source to cache"
    )]
    public static partial void FileCopyFailed(ILogger logger, Exception exception, string name);

    [LoggerMessage(
        EventId = 15,
        Level = LogLevel.Warning,
        Message = "Failed to remove stale cache file {Name}"
    )]
    public static partial void StaleFileDeleteFailed(ILogger logger, Exception exception, string name);

    [LoggerMessage(
        EventId = 16,
        Level = LogLevel.Information,
        Message = "Sync done: copied={Copied} skipped={Skipped} removed={Removed} failed={Failed} src={SourceCount}"
    )]
    public static partial void SyncCompleted(
        ILogger logger,
        int copied,
        int skipped,
        int removed,
        int failed,
        int sourceCount
    );
}
