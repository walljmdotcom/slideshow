using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Processing;

namespace SlideshowWebServer;

/// <summary>Largest width and height a cached image may have. Zero leaves that axis unbounded.</summary>
internal readonly record struct ImageBounds(int MaxWidth, int MaxHeight)
{
    public bool IsEnabled => this.MaxWidth > 0 || this.MaxHeight > 0;
}

internal static class ImageResizer
{
    private static readonly JpegEncoder Jpeg = new() { Quality = 85 };
    private static readonly PngEncoder Png = new();
    private static readonly WebpEncoder Webp = new();
    private static readonly BmpEncoder Bmp = new();

    // Single-threaded so a large re-encode leaves a core free for the kiosk browser.
    private static readonly Configuration ResizeConfiguration = CreateConfiguration();

    /// <summary>GIF (animation), SVG (vector), and videos are always cached as-is.</summary>
    public static bool CanResize(ReadOnlySpan<char> fileName) => GetEncoder(fileName) is not null;

    /// <summary>The largest same-aspect size inside <paramref name="bounds"/>, or null if it fits.</summary>
    public static Size? ComputeTargetSize(int width, int height, ImageBounds bounds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        double widthScale = bounds.MaxWidth > 0 ? (double)bounds.MaxWidth / width : double.PositiveInfinity;
        double heightScale = bounds.MaxHeight > 0 ? (double)bounds.MaxHeight / height : double.PositiveInfinity;
        double scale = Math.Min(widthScale, heightScale);
        if (scale >= 1)
        {
            return null;
        }

        return new Size(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale))
        );
    }

    /// <summary>
    /// Writes a copy of <paramref name="inputPath"/> scaled to fit <paramref name="bounds"/>, upright per its EXIF
    /// orientation, with EXIF, XMP, and IPTC metadata removed. Returns false and writes nothing when it already fits.
    /// </summary>
    /// <param name="mediaName">The media file name, which picks the output format.</param>
    public static async Task<bool> TryResizeAsync(
        string inputPath,
        string mediaName,
        string outputPath,
        ImageBounds bounds,
        CancellationToken cancellationToken
    )
    {
        IImageEncoder encoder = GetEncoder(mediaName)
            ?? throw new NotSupportedException($"No encoder for {mediaName}.");

        DecoderOptions identifyOptions = new() { Configuration = ResizeConfiguration };
        ImageInfo info = await Image.IdentifyAsync(identifyOptions, inputPath, cancellationToken);

        bool swapsAxes = IsQuarterTurn(info.Metadata.ExifProfile);
        int uprightWidth = swapsAxes ? info.Height : info.Width;
        int uprightHeight = swapsAxes ? info.Width : info.Height;
        if (ComputeTargetSize(uprightWidth, uprightHeight, bounds) is not { } target)
        {
            return false;
        }

        // The decoder scales before orientation is applied, so it is given the target in stored axes.
        DecoderOptions decodeOptions = new()
        {
            Configuration = ResizeConfiguration,
            TargetSize = swapsAxes ? new Size(target.Height, target.Width) : target,
        };

        using Image image = await Image.LoadAsync(decodeOptions, inputPath, cancellationToken);
        image.Mutate(static context => context.AutoOrient());
        if (image.Size != target)
        {
            image.Mutate(context => context.Resize(target.Width, target.Height));
        }

        image.Metadata.ExifProfile = null;
        image.Metadata.XmpProfile = null;
        image.Metadata.IptcProfile = null;

        await image.SaveAsync(outputPath, encoder, cancellationToken);
        return true;
    }

    /// <summary>Returns pooled pixel buffers to the OS after a batch of resizes.</summary>
    public static void ReleaseRetainedMemory() => ResizeConfiguration.MemoryAllocator.ReleaseRetainedResources();

    private static Configuration CreateConfiguration()
    {
        Configuration configuration = Configuration.Default.Clone();
        configuration.MaxDegreeOfParallelism = 1;
        return configuration;
    }

    // EXIF orientations 5 through 8 rotate a quarter turn, so the upright image swaps width and height.
    private static bool IsQuarterTurn(ExifProfile? exif) =>
        exif is not null
        && exif.TryGetValue(ExifTag.Orientation, out IExifValue<ushort>? orientation)
        && orientation is { Value: >= 5 and <= 8 };

    private static IImageEncoder? GetEncoder(ReadOnlySpan<char> fileName)
    {
        ReadOnlySpan<char> ext = Path.GetExtension(fileName);
        if (ext.StartsWith('.'))
        {
            ext = ext[1..];
        }

        if (ext.IsEmpty || ext.Length > 4)
        {
            return null;
        }

        Span<char> lower = stackalloc char[4];
        int written = ext.ToLowerInvariant(lower);
        return lower[..written] switch
        {
            "jpg" or "jpeg" => Jpeg,
            "png" => Png,
            "webp" => Webp,
            "bmp" => Bmp,
            _ => null,
        };
    }
}
