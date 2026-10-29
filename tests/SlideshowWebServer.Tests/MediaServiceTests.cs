using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SlideshowWebServer.Tests;

public sealed class MediaServiceTests
{
    [Theory]
    [InlineData("photo.png", true, "image/png", "image")]
    [InlineData("PHOTO.JPG", true, "image/jpeg", "image")]
    [InlineData("holiday.2026.jpeg", true, "image/jpeg", "image")]
    [InlineData("anim.gif", true, "image/gif", "image")]
    [InlineData("vector.SVG", true, "image/svg+xml", "image")]
    [InlineData("modern.webp", true, "image/webp", "image")]
    [InlineData("bitmap.bmp", true, "image/bmp", "image")]
    [InlineData("clip.mp4", true, "video/mp4", "video")]
    [InlineData("clip.WEBM", true, "video/webm", "video")]
    [InlineData("clip.ogg", true, "video/ogg", "video")]
    [InlineData("clip.avi", true, "video/avi", "video")]
    [InlineData("clip.MOV", true, "video/quicktime", "video")]
    [InlineData("notes.txt", false, "", "")]
    [InlineData("page.html", false, "", "")]
    [InlineData("archive.tar.gz", false, "", "")]
    [InlineData("photo.png.tmp", false, "", "")]
    [InlineData("photo.png.old", false, "", "")]
    [InlineData("noext", false, "", "")]
    [InlineData(".png", true, "image/png", "image")]
    [InlineData("", false, "", "")]
    public void MediaTypes_ClassifiesExtensionsCorrectly(
        string fileName,
        bool expectedSupported,
        string expectedContentType,
        string expectedCategory
    )
    {
        bool actual = MediaTypes.TryGetFromFileName(fileName, out MediaType mediaType);
        Assert.Equal(expectedSupported, actual);
        if (expectedSupported)
        {
            Assert.Equal(expectedContentType, mediaType.ContentType);
            Assert.Equal(expectedCategory, mediaType.Category);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../cache-sibling/secret.png")]
    [InlineData("..\\cache-sibling\\secret.png")]
    [InlineData("sub/photo.png")]
    [InlineData("sub\\photo.png")]
    [InlineData("/etc/passwd")]
    [InlineData("secret.txt")]
    [InlineData("staging.png.tmp")]
    [InlineData("displaced.png.old")]
    [InlineData("missing.png")]
    public void TryResolveMediaFile_RejectsInvalidTraversalAndUnsupportedInputs(string requestedName)
    {
        using TempWorkspace workspace = new();
        string siblingDir = workspace.CreateDirectory("cache-sibling");
        workspace.WriteFile(siblingDir, "secret.png", [1, 2, 3]);
        workspace.WriteFile(workspace.CachePath, "secret.txt", "nope"u8);
        workspace.WriteFile(workspace.CachePath, "staging.png.tmp", [1, 2]);
        workspace.WriteFile(workspace.CachePath, "displaced.png.old", [1, 2]);
        workspace.WriteFile(workspace.CachePath, "valid.png", [137, 80, 78, 71]);

        SlideshowConfig config = new()
        {
            FolderPath = workspace.SourcePath,
            CacheFolderPath = workspace.CachePath,
        };
        MediaService service = new(config, NullLogger<MediaService>.Instance, workspace.RootPath);

        bool resolved = service.TryResolveMediaFile(requestedName, out ResolvedMediaFile media);
        Assert.False(resolved);
        Assert.Null(media.PhysicalPath);
    }

    [Fact]
    public void TryResolveMediaFile_RejectsNullByteInFileName()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile(workspace.CachePath, "valid.png", [1, 2, 3]);

        SlideshowConfig config = new()
        {
            FolderPath = workspace.SourcePath,
            CacheFolderPath = workspace.CachePath,
        };
        MediaService service = new(config, NullLogger<MediaService>.Instance, workspace.RootPath);

        bool resolved = service.TryResolveMediaFile("valid.png\0.txt", out _);
        Assert.False(resolved);
    }

    [Fact]
    public void TryResolveMediaFile_RejectsSymlinkEscapingCacheDirectory()
    {
        using TempWorkspace workspace = new();
        Directory.CreateDirectory(workspace.CachePath);
        string outsideFile = workspace.WriteFile(workspace.RootPath, "outside.png", [1, 2, 3, 4]);
        string symlinkPath = Path.Combine(workspace.CachePath, "linked.png");

        try
        {
            File.CreateSymbolicLink(symlinkPath, outsideFile);
        }
        catch (IOException)
        {
            // Symbolic links require elevated privileges on unconfigured Windows hosts.
            return;
        }

        SlideshowConfig config = new()
        {
            FolderPath = workspace.SourcePath,
            CacheFolderPath = workspace.CachePath,
        };
        MediaService service = new(config, NullLogger<MediaService>.Instance, workspace.RootPath);

        Assert.Empty(service.RefreshCacheIndex());
        Assert.False(service.TryResolveMediaFile("linked.png", out _));
    }

    [Fact]
    public void GetMediaFiles_SortsAlphabeticallySkipsTempArtifactsAndUpdatesVersionOnEdit()
    {
        using TempWorkspace workspace = new();
        DateTime initialTime = new(2025, 4, 1, 8, 0, 0, DateTimeKind.Utc);
        workspace.WriteFile(workspace.CachePath, "zeta.mp4", [1], initialTime);
        workspace.WriteFile(workspace.CachePath, "Alpha.png", [2, 3], initialTime);
        workspace.WriteFile(workspace.CachePath, "beta.jpg", [4, 5, 6], initialTime);
        workspace.WriteFile(workspace.CachePath, "staging.png.tmp", [7], initialTime);
        workspace.WriteFile(workspace.CachePath, "displaced.png.old", [8], initialTime);
        workspace.WriteFile(workspace.CachePath, "readme.md", [9], initialTime);

        SlideshowConfig config = new()
        {
            FolderPath = workspace.SourcePath,
            CacheFolderPath = workspace.CachePath,
            DisplayOrder = "Alpha",
        };
        MediaService service = new(config, NullLogger<MediaService>.Instance, workspace.RootPath);

        MediaFile[] files = service.GetMediaFiles();
        Assert.Equal(["Alpha.png", "beta.jpg", "zeta.mp4"], files.Select(static f => f.Name).ToArray());
        Assert.Equal(["image", "image", "video"], files.Select(static f => f.Type).ToArray());
        string initialAlphaVersion = files[0].Version;
        Assert.False(string.IsNullOrWhiteSpace(initialAlphaVersion));

        workspace.WriteFile(workspace.CachePath, "Alpha.png", [2, 3, 4, 5], initialTime.AddSeconds(10));
        MediaFile[] refreshed = service.RefreshCacheIndex();
        Assert.NotEqual(initialAlphaVersion, refreshed[0].Version);
    }
}
