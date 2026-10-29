using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SlideshowWebServer.Tests;

public sealed class MediaSyncServiceTests
{
    [Fact]
    public async Task SyncOnceAsync_MirrorsSupportedFilesPreservesMtimeAndCleansOrphanArtifacts()
    {
        using TempWorkspace workspace = new();
        DateTime fixedMtime = new(2025, 6, 15, 14, 30, 0, DateTimeKind.Utc);

        workspace.WriteFile(workspace.SourcePath, "b.mp4", [10, 20, 30], fixedMtime);
        workspace.WriteFile(workspace.SourcePath, "a.png", [1, 2, 3, 4], fixedMtime);
        workspace.WriteFile(workspace.SourcePath, "ignore.txt", "skip"u8, fixedMtime);
        workspace.WriteFile(workspace.SourcePath, "partial.jpg.tmp", [9], fixedMtime);
        workspace.WriteFile(workspace.CachePath, "orphan.png.tmp", [99]);
        workspace.WriteFile(workspace.CachePath, "displaced.png.old", [88]);

        (MediaService mediaService, MediaSyncService syncService) = CreateServices(workspace);

        await syncService.SyncOnceAsync(CancellationToken.None);

        MediaFile[] cached = mediaService.GetMediaFiles();
        Assert.Equal(["a.png", "b.mp4"], cached.Select(static f => f.Name).ToArray());
        Assert.Equal(["image", "video"], cached.Select(static f => f.Type).ToArray());
        Assert.False(File.Exists(Path.Combine(workspace.CachePath, "orphan.png.tmp")));
        Assert.False(File.Exists(Path.Combine(workspace.CachePath, "displaced.png.old")));
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

    [Fact]
    public async Task SyncOnceAsync_SurvivesDanglingSymlinkInSourceWithoutDeletingCachedCopy()
    {
        using TempWorkspace workspace = new();
        Directory.CreateDirectory(workspace.SourcePath);
        workspace.WriteFile(workspace.SourcePath, "good.png", [10, 20]);
        workspace.WriteFile(workspace.CachePath, "dangling.png", [99, 98, 97]);

        string danglingLink = Path.Combine(workspace.SourcePath, "dangling.png");
        try
        {
            File.CreateSymbolicLink(danglingLink, Path.Combine(workspace.RootPath, "does-not-exist.png"));
        }
        catch (IOException)
        {
            // Symbolic links require elevated privileges on unconfigured Windows hosts.
            return;
        }

        (MediaService mediaService, MediaSyncService syncService) = CreateServices(workspace);
        _ = mediaService.RefreshCacheIndex();

        await syncService.SyncOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(workspace.CachePath, "good.png")));
        Assert.True(File.Exists(Path.Combine(workspace.CachePath, "dangling.png")));
        Assert.Equal(
            ["dangling.png", "good.png"],
            mediaService.GetMediaFiles().Select(static f => f.Name).ToArray()
        );
    }

    [Fact]
    public async Task SyncOnceAsync_UpdatesAndDeletesCachedFileWhileOpenWithFileShareDelete()
    {
        using TempWorkspace workspace = new();
        string sourceClip = workspace.WriteFile(workspace.SourcePath, "clip.mp4", [1, 2, 3, 4]);
        workspace.WriteFile(workspace.SourcePath, "keep.png", [9]);

        (MediaService mediaService, MediaSyncService syncService) = CreateServices(workspace);
        await syncService.SyncOnceAsync(CancellationToken.None);

        string cachedClip = Path.Combine(workspace.CachePath, "clip.mp4");
        await using FileStream activeReader = new(
            cachedClip,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
            }
        );

        File.WriteAllBytes(sourceClip, [5, 6, 7, 8, 9, 10]);
        await syncService.SyncOnceAsync(CancellationToken.None);
        Assert.Equal([5, 6, 7, 8, 9, 10], await File.ReadAllBytesAsync(cachedClip));

        File.Delete(sourceClip);
        await syncService.SyncOnceAsync(CancellationToken.None);
        Assert.Equal(["keep.png"], mediaService.GetMediaFiles().Select(static f => f.Name).ToArray());
    }

    [Fact]
    public async Task CopyStreamWithTimeoutAsync_FailsStalledStreamAndAllowsSlowProgressingStream()
    {
        using MemoryStream destination = new();
        await using StallingStream stalled = new();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            MediaSyncService.CopyStreamWithTimeoutAsync(
                stalled,
                destination,
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None
            )
        );

        await using DripStream progressing = new(chunkCount: 4, delayBetweenChunksMs: 35);
        using MemoryStream completed = new();
        await MediaSyncService.CopyStreamWithTimeoutAsync(
            progressing,
            completed,
            TimeSpan.FromMilliseconds(150),
            CancellationToken.None
        );
        Assert.Equal([1, 2, 3, 4], completed.ToArray());
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

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class DripStream : Stream
    {
        private readonly int chunkCount;
        private readonly int delayMs;
        private int emitted;

        public DripStream(int chunkCount, int delayBetweenChunksMs)
        {
            this.chunkCount = chunkCount;
            this.delayMs = delayBetweenChunksMs;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (this.emitted >= this.chunkCount)
            {
                return 0;
            }

            await Task.Delay(this.delayMs, cancellationToken);
            this.emitted++;
            buffer.Span[0] = (byte)this.emitted;
            return 1;
        }
    }
}
