using Microsoft.Extensions.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace SlideshowWebServer.Tests;

public sealed class ImageResizeTests
{
    private static readonly ImageBounds FullHd = new(1920, 1080);
    private static readonly DateTime SourceMtime = new(2025, 10, 25, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(4000, 3000, 1920, 1080, 1440, 1080)]
    [InlineData(3000, 4000, 1920, 1080, 810, 1080)]
    [InlineData(4000, 1000, 1920, 1080, 1920, 480)]
    [InlineData(1921, 1081, 1920, 1080, 1919, 1080)]
    [InlineData(4000, 3000, 1920, 0, 1920, 1440)]
    [InlineData(4000, 3000, 0, 1080, 1440, 1080)]
    [InlineData(10000, 1, 1920, 1080, 1920, 1)]
    [InlineData(1, 10000, 1920, 1080, 1, 1080)]
    public void ComputeTargetSize_FitsInsideBoundsKeepingAspect(
        int width,
        int height,
        int maxWidth,
        int maxHeight,
        int expectedWidth,
        int expectedHeight
    )
    {
        Size? target = ImageResizer.ComputeTargetSize(width, height, new ImageBounds(maxWidth, maxHeight));

        Assert.Equal(new Size(expectedWidth, expectedHeight), target);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(800, 600, 1920, 1080)]
    [InlineData(1, 1, 1920, 1080)]
    [InlineData(5000, 1080, 0, 1080)]
    [InlineData(1920, 5000, 1920, 0)]
    public void ComputeTargetSize_ReturnsNullWhenImageAlreadyFits(int width, int height, int maxWidth, int maxHeight)
    {
        Assert.Null(ImageResizer.ComputeTargetSize(width, height, new ImageBounds(maxWidth, maxHeight)));
    }

    [Theory]
    [InlineData("1920", "1080", 1920, 1080)]
    [InlineData("0", "0", 0, 0)]
    [InlineData("-5", "-1", 0, 0)]
    [InlineData("wide", "1080px", 0, 0)]
    [InlineData("", " ", 0, 0)]
    [InlineData(null, "720", 0, 720)]
    public void FromConfiguration_ParsesMaxImageSizeAndIgnoresInvalidValues(
        string? width,
        string? height,
        int expectedWidth,
        int expectedHeight
    )
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                [
                    new KeyValuePair<string, string?>("maxImageWidth", width),
                    new KeyValuePair<string, string?>("maxImageHeight", height),
                ]
            )
            .Build();

        SlideshowConfig config = SlideshowConfig.FromConfiguration(configuration);

        Assert.Equal(new ImageBounds(expectedWidth, expectedHeight), config.ImageBounds);
    }

    [Theory]
    [InlineData("photo.jpg", "JPEG")]
    [InlineData("photo.JPEG", "JPEG")]
    [InlineData("photo.png", "PNG")]
    [InlineData("photo.webp", "Webp")]
    [InlineData("photo.bmp", "BMP")]
    public async Task Sync_DownscalesOversizedImagesAndKeepsTheirFormat(string fileName, string expectedFormat)
    {
        using TempWorkspace workspace = new();
        WriteImage(workspace.SourcePath, fileName, 4000, 3000);
        (_, MediaSyncService sync) = MediaSyncServiceTests.CreateServices(workspace, imageBounds: FullHd);

        SyncResult result = await sync.SyncOnceAsync(CancellationToken.None);

        string cachedPath = Path.Combine(workspace.CachePath, fileName);
        ImageInfo info = Image.Identify(cachedPath);
        Assert.Equal(1, result.Copied);
        Assert.Equal(new Size(1440, 1080), info.Size);
        Assert.Equal(expectedFormat, info.Metadata.DecodedImageFormat?.Name, ignoreCase: true);
        Assert.Equal(SourceMtime, File.GetLastWriteTimeUtc(cachedPath));
    }

    [Fact]
    public async Task Sync_DoesNotReprocessImagesThatAreAlreadyCurrent()
    {
        using TempWorkspace workspace = new();
        WriteImage(workspace.SourcePath, "big.jpg", 4000, 3000);
        WriteImage(workspace.SourcePath, "small.jpg", 800, 600);
        workspace.WriteFile(workspace.SourcePath, "clip.mp4", [1, 2, 3], SourceMtime);
        (_, MediaSyncService sync) = MediaSyncServiceTests.CreateServices(workspace, imageBounds: FullHd);

        SyncResult first = await sync.SyncOnceAsync(CancellationToken.None);
        SyncResult second = await sync.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(new SyncResult(Copied: 3, Skipped: 0, Removed: 0, Failed: 0), first);
        Assert.Equal(new SyncResult(Copied: 0, Skipped: 3, Removed: 0, Failed: 0), second);
    }

    [Fact]
    public async Task Sync_RotatesPerExifOrientationAndStripsMetadata()
    {
        using TempWorkspace workspace = new();
        ExifProfile exif = new();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        WriteImage(workspace.SourcePath, "portrait.jpg", 4000, 3000, exif);
        (_, MediaSyncService sync) = MediaSyncServiceTests.CreateServices(workspace, imageBounds: FullHd);

        _ = await sync.SyncOnceAsync(CancellationToken.None);

        ImageInfo info = Image.Identify(Path.Combine(workspace.CachePath, "portrait.jpg"));
        Assert.Equal(new Size(810, 1080), info.Size);
        Assert.Null(info.Metadata.ExifProfile);
    }

    [Theory]
    [InlineData("small.jpg", 800, 600)]
    [InlineData("exact.jpg", 1920, 1080)]
    [InlineData("animated.gif", 4000, 3000)]
    public async Task Sync_CopiesImagesThatFitOrCannotBeResizedByteForByte(string fileName, int width, int height)
    {
        using TempWorkspace workspace = new();
        string sourcePath = WriteImage(workspace.SourcePath, fileName, width, height);
        (_, MediaSyncService sync) = MediaSyncServiceTests.CreateServices(workspace, imageBounds: FullHd);

        _ = await sync.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(Path.Combine(workspace.CachePath, fileName)));
    }

    [Fact]
    public async Task Sync_LeavesImagesUntouchedAndWritesNoManifestWhenResizeIsOff()
    {
        using TempWorkspace workspace = new();
        string sourcePath = WriteImage(workspace.SourcePath, "big.jpg", 4000, 3000);
        (_, MediaSyncService sync) = MediaSyncServiceTests.CreateServices(workspace);

        _ = await sync.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(Path.Combine(workspace.CachePath, "big.jpg")));
        Assert.False(File.Exists(Path.Combine(workspace.CachePath, ImageCacheManifest.FileName)));
    }

    [Theory]
    [InlineData("garbage.jpg", new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 })]
    [InlineData("truncated.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 })]
    [InlineData("empty.png", new byte[0])]
    [InlineData("text.webp", new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F })]
    public async Task Sync_CachesTheOriginalOnceWhenAnImageCannotBeDecoded(string fileName, byte[] content)
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile(workspace.SourcePath, fileName, content, SourceMtime);
        (_, MediaSyncService sync) = MediaSyncServiceTests.CreateServices(workspace, imageBounds: FullHd);

        SyncResult first = await sync.SyncOnceAsync(CancellationToken.None);
        SyncResult second = await sync.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(content, File.ReadAllBytes(Path.Combine(workspace.CachePath, fileName)));
        Assert.Equal(new SyncResult(Copied: 1, Skipped: 0, Removed: 0, Failed: 0), first);
        Assert.Equal(new SyncResult(Copied: 0, Skipped: 1, Removed: 0, Failed: 0), second);
        Assert.Empty(Directory.GetFiles(workspace.CachePath, "*.tmp"));
    }

    [Fact]
    public async Task Sync_ReprocessesEverythingWhenBoundsChange()
    {
        using TempWorkspace workspace = new();
        WriteImage(workspace.SourcePath, "big.jpg", 4000, 3000);
        (_, MediaSyncService fullHd) = MediaSyncServiceTests.CreateServices(workspace, imageBounds: FullHd);
        _ = await fullHd.SyncOnceAsync(CancellationToken.None);

        (_, MediaSyncService smaller) = MediaSyncServiceTests.CreateServices(
            workspace,
            imageBounds: new ImageBounds(960, 540)
        );
        SyncResult result = await smaller.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.Copied);
        Assert.Equal(new Size(720, 540), Image.Identify(Path.Combine(workspace.CachePath, "big.jpg")).Size);
    }

    [Fact]
    public async Task Sync_RestoresOriginalsWhenResizeIsTurnedOff()
    {
        using TempWorkspace workspace = new();
        string sourcePath = WriteImage(workspace.SourcePath, "big.jpg", 4000, 3000);
        (_, MediaSyncService resizing) = MediaSyncServiceTests.CreateServices(workspace, imageBounds: FullHd);
        _ = await resizing.SyncOnceAsync(CancellationToken.None);

        (_, MediaSyncService plain) = MediaSyncServiceTests.CreateServices(workspace);
        SyncResult result = await plain.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.Copied);
        Assert.Equal(File.ReadAllBytes(sourcePath), File.ReadAllBytes(Path.Combine(workspace.CachePath, "big.jpg")));
    }

    [Fact]
    public async Task Sync_ReprocessesWhenTheSourceImageIsReplaced()
    {
        using TempWorkspace workspace = new();
        WriteImage(workspace.SourcePath, "photo.jpg", 4000, 3000);
        (_, MediaSyncService sync) = MediaSyncServiceTests.CreateServices(workspace, imageBounds: FullHd);
        _ = await sync.SyncOnceAsync(CancellationToken.None);

        WriteImage(workspace.SourcePath, "photo.jpg", 3000, 4000, lastWriteUtc: SourceMtime.AddHours(1));
        SyncResult result = await sync.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.Copied);
        Assert.Equal(new Size(810, 1080), Image.Identify(Path.Combine(workspace.CachePath, "photo.jpg")).Size);
    }

    [Fact]
    public async Task Sync_ReprocessesWhenTheCachedCopyIsDeletedOrAltered()
    {
        using TempWorkspace workspace = new();
        WriteImage(workspace.SourcePath, "a.jpg", 4000, 3000);
        WriteImage(workspace.SourcePath, "b.jpg", 4000, 3000);
        (_, MediaSyncService sync) = MediaSyncServiceTests.CreateServices(workspace, imageBounds: FullHd);
        _ = await sync.SyncOnceAsync(CancellationToken.None);

        File.Delete(Path.Combine(workspace.CachePath, "a.jpg"));
        workspace.WriteFile(workspace.CachePath, "b.jpg", [1, 2, 3], SourceMtime);
        SyncResult result = await sync.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(2, result.Copied);
        Assert.Equal(new Size(1440, 1080), Image.Identify(Path.Combine(workspace.CachePath, "a.jpg")).Size);
        Assert.Equal(new Size(1440, 1080), Image.Identify(Path.Combine(workspace.CachePath, "b.jpg")).Size);
    }

    [Fact]
    public async Task Sync_KeepsManifestOutOfThePlaylistAndPrunesRemovedImages()
    {
        using TempWorkspace workspace = new();
        WriteImage(workspace.SourcePath, "keep.jpg", 4000, 3000);
        string dropped = WriteImage(workspace.SourcePath, "drop.jpg", 4000, 3000);
        (MediaService media, MediaSyncService sync) = MediaSyncServiceTests.CreateServices(
            workspace,
            imageBounds: FullHd
        );
        _ = await sync.SyncOnceAsync(CancellationToken.None);

        File.Delete(dropped);
        SyncResult result = await sync.SyncOnceAsync(CancellationToken.None);

        string manifestPath = Path.Combine(workspace.CachePath, ImageCacheManifest.FileName);
        Assert.Equal(new SyncResult(Copied: 0, Skipped: 1, Removed: 1, Failed: 0), result);
        Assert.True(File.Exists(manifestPath));
        Assert.Equal(["keep.jpg"], media.GetMediaFiles().Select(static f => f.Name).ToArray());
        string manifest = File.ReadAllText(manifestPath);
        Assert.Contains("keep.jpg", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("drop.jpg", manifest, StringComparison.Ordinal);
        Assert.False(media.TryResolveMediaFile(ImageCacheManifest.FileName, out _));
    }

    [Theory]
    [InlineData("{\"keep.jpg\": {\"sourceLength\": 1")]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"keep.jpg\": null}")]
    [InlineData("<BOM>{")]
    public async Task Sync_RebuildsAnUnreadableManifestByReprocessing(string manifestContent)
    {
        manifestContent = manifestContent.Replace("<BOM>", ((char)0xFEFF).ToString(), StringComparison.Ordinal);
        using TempWorkspace workspace = new();
        WriteImage(workspace.SourcePath, "keep.jpg", 4000, 3000);
        (_, MediaSyncService sync) = MediaSyncServiceTests.CreateServices(workspace, imageBounds: FullHd);
        _ = await sync.SyncOnceAsync(CancellationToken.None);

        string manifestPath = Path.Combine(workspace.CachePath, ImageCacheManifest.FileName);
        File.WriteAllText(manifestPath, manifestContent);
        SyncResult rebuilt = await sync.SyncOnceAsync(CancellationToken.None);
        SyncResult settled = await sync.SyncOnceAsync(CancellationToken.None);

        Assert.Equal(1, rebuilt.Copied);
        Assert.Equal(0, rebuilt.Failed);
        Assert.Equal(new SyncResult(Copied: 0, Skipped: 1, Removed: 0, Failed: 0), settled);
    }

    private static string WriteImage(
        string directory,
        string fileName,
        int width,
        int height,
        ExifProfile? exif = null,
        DateTime? lastWriteUtc = null
    )
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        using Image<Rgba32> image = new(width, height, new Rgba32(200, 120, 40, 255));
        image.Metadata.ExifProfile = exif;

        IImageEncoder encoder = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegEncoder(),
            ".png" => new PngEncoder(),
            ".webp" => new WebpEncoder(),
            ".bmp" => new BmpEncoder(),
            ".gif" => new GifEncoder(),
            _ => throw new ArgumentException($"No test encoder for {fileName}", nameof(fileName)),
        };

        image.Save(path, encoder);
        File.SetLastWriteTimeUtc(path, lastWriteUtc ?? SourceMtime);
        return path;
    }
}
