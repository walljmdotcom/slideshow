using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SlideshowWebServer.Tests;

public sealed class MediaSyncServiceTests
{
    [Fact]
    public async Task SyncOnceAsync_MirrorsSupportedFilesPreservesMtimeAndCleansOrphanTmp()
    {
        using TempWorkspace workspace = new();
        DateTime fixedMtime = new(2025, 6, 15, 14, 30, 0, DateTimeKind.Utc);

        workspace.WriteFile(workspace.SourcePath, "b.mp4", [10, 20, 30], fixedMtime);
        workspace.WriteFile(workspace.SourcePath, "a.png", [1, 2, 3, 4], fixedMtime);
        workspace.WriteFile(workspace.SourcePath, "ignore.txt", "skip"u8, fixedMtime);
        workspace.WriteFile(workspace.SourcePath, "partial.jpg.tmp", [9], fixedMtime);
        workspace.WriteFile(workspace.CachePath, "orphan.png.tmp", [99]);

        (MediaService mediaService, MediaSyncService syncService) = CreateServices(workspace);

        await syncService.SyncOnceAsync(CancellationToken.None);

        MediaFile[] cached = mediaService.GetMediaFiles();
        Assert.Equal(
            [
                new MediaFile("a.png", "image"),
                new MediaFile("b.mp4", "video"),
            ],
            cached
        );
        Assert.False(File.Exists(Path.Combine(workspace.CachePath, "orphan.png.tmp")));
        Assert.False(File.Exists(Path.Combine(workspace.CachePath, "ignore.txt")));
        Assert.Equal(fixedMtime, File.GetLastWriteTimeUtc(Path.Combine(workspace.CachePath, "a.png")));

        // Sub-millisecond tick differences across filesystems should not trigger redundant copies.
        File.SetLastWriteTimeUtc(Path.Combine(workspace.CachePath, "a.png"), fixedMtime.AddTicks(-3));
        DateTime cachedMtimeBefore = File.GetLastWriteTimeUtc(Path.Combine(workspace.CachePath, "a.png"));
        await syncService.SyncOnceAsync(CancellationToken.None);
        Assert.Equal(cachedMtimeBefore, File.GetLastWriteTimeUtc(Path.Combine(workspace.CachePath, "a.png")));
    }

    [Fact]
    public async Task SyncOnceAsync_PreservesCacheWhenSourceDirectoryMissing()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile(workspace.CachePath, "existing.png", [1, 2, 3]);

        (MediaService mediaService, MediaSyncService syncService) = CreateServices(workspace);
        _ = mediaService.RefreshCacheIndex();

        await syncService.SyncOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(workspace.CachePath, "existing.png")));
        Assert.Single(mediaService.GetMediaFiles());
    }

    [Fact]
    public async Task SyncOnceAsync_PreservesCacheWhenSourceIsEmptyUnmountedMountpoint()
    {
        using TempWorkspace workspace = new();
        Directory.CreateDirectory(workspace.SourcePath);
        workspace.WriteFile(workspace.CachePath, "cached1.png", [1, 2, 3]);
        workspace.WriteFile(workspace.CachePath, "cached2.mp4", [4, 5, 6]);

        (MediaService mediaService, MediaSyncService syncService) = CreateServices(workspace);
        _ = mediaService.RefreshCacheIndex();

        await syncService.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(2, mediaService.GetMediaFiles().Length);
        Assert.True(File.Exists(Path.Combine(workspace.CachePath, "cached1.png")));
        Assert.True(File.Exists(Path.Combine(workspace.CachePath, "cached2.mp4")));
    }

    [Fact]
    public async Task SyncOnceAsync_PrunesEmptySourceOnlyWhenAllowEmptySourceSyncEnabled()
    {
        using TempWorkspace workspace = new();
        Directory.CreateDirectory(workspace.SourcePath);
        workspace.WriteFile(workspace.CachePath, "cached1.png", [1, 2, 3]);

        (MediaService mediaService, MediaSyncService syncService) = CreateServices(
            workspace,
            allowEmptySourceSync: true
        );
        _ = mediaService.RefreshCacheIndex();

        await syncService.SyncOnceAsync(CancellationToken.None);

        Assert.Empty(mediaService.GetMediaFiles());
        Assert.Empty(Directory.GetFiles(workspace.CachePath));
    }

    [Fact]
    public async Task SyncOnceAsync_RemovesDeletedFilesWhenOtherSourceFilesRemain()
    {
        using TempWorkspace workspace = new();
        string oldSource = workspace.WriteFile(workspace.SourcePath, "old.png", [1, 2]);
        workspace.WriteFile(workspace.SourcePath, "keep.png", [3, 4]);

        (MediaService mediaService, MediaSyncService syncService) = CreateServices(workspace);
        await syncService.SyncOnceAsync(CancellationToken.None);
        Assert.Equal(2, mediaService.GetMediaFiles().Length);

        File.Delete(oldSource);
        await syncService.SyncOnceAsync(CancellationToken.None);

        MediaFile[] remaining = mediaService.GetMediaFiles();
        MediaFile single = Assert.Single(remaining);
        Assert.Equal("keep.png", single.Name);
        Assert.False(File.Exists(Path.Combine(workspace.CachePath, "old.png")));
    }

    [Fact]
    public async Task SyncOnceAsync_HandlesCaseRenameWithoutDeletingOrDuplicatingFile()
    {
        using TempWorkspace workspace = new();
        string upperPath = workspace.WriteFile(workspace.SourcePath, "Slide1.png", [1, 2, 3]);

        (MediaService mediaService, MediaSyncService syncService) = CreateServices(workspace);
        await syncService.SyncOnceAsync(CancellationToken.None);
        Assert.Single(mediaService.GetMediaFiles());

        // Replace Slide1.png with slide1.png (via intermediate name so case-insensitive filesystems rename cleanly).
        string tempRename = Path.Combine(workspace.SourcePath, "rename-step.png");
        File.Move(upperPath, tempRename);
        string lowerPath = Path.Combine(workspace.SourcePath, "slide1.png");
        File.Move(tempRename, lowerPath);
        File.WriteAllBytes(lowerPath, [5, 6, 7, 8]);

        await syncService.SyncOnceAsync(CancellationToken.None);

        string[] filesOnDisk = Directory.GetFiles(workspace.CachePath);
        string singleFile = Assert.Single(filesOnDisk);
        Assert.Equal([5, 6, 7, 8], await File.ReadAllBytesAsync(singleFile));
        Assert.Single(mediaService.GetMediaFiles());
    }

    private static (MediaService Media, MediaSyncService Sync) CreateServices(
        TempWorkspace workspace,
        bool allowEmptySourceSync = false
    )
    {
        SlideshowConfig config = new()
        {
            FolderPath = workspace.SourcePath,
            CacheFolderPath = workspace.CachePath,
            AllowEmptySourceSync = allowEmptySourceSync,
        };
        MediaService mediaService = new(config, NullLogger<MediaService>.Instance, workspace.RootPath);
        MediaSyncService syncService = new(NullLogger<MediaSyncService>.Instance, mediaService);
        return (mediaService, syncService);
    }
}
