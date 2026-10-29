using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;

namespace SlideshowWebServer;

public sealed class SlideshowConfig
{
    public const string AlphabeticalDisplayOrder = "alpha";
    public const string RandomDisplayOrder = "random";

    public int ImageDuration { get; set; } = 5;
    public string FolderPath { get; set; } = "./media";
    public string? CacheFolderPath { get; set; }
    public int SyncIntervalSeconds { get; set; } = 60;
    public int FadeTransitionDuration { get; set; } = 1;
    public bool ZoomOnImage { get; set; } = true;
    public string DisplayOrder { get; set; } = AlphabeticalDisplayOrder;
    public bool AllowEmptySourceSync { get; set; }

    public static SlideshowConfig FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        SlideshowConfig config = new();

        if (int.TryParse(configuration["imageDuration"], out int imageDuration) && imageDuration > 0)
        {
            config.ImageDuration = imageDuration;
        }

        string? folderPath = configuration["folderPath"];
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            config.FolderPath = folderPath;
        }

        string? cacheFolderPath = configuration["cacheFolderPath"];
        if (!string.IsNullOrWhiteSpace(cacheFolderPath))
        {
            config.CacheFolderPath = cacheFolderPath;
        }

        if (int.TryParse(configuration["syncIntervalSeconds"], out int syncInterval) && syncInterval > 0)
        {
            config.SyncIntervalSeconds = syncInterval;
        }

        if (int.TryParse(configuration["fadeTransitionDuration"], out int fadeDuration) && fadeDuration >= 0)
        {
            config.FadeTransitionDuration = fadeDuration;
        }

        if (bool.TryParse(configuration["zoomOnImage"], out bool zoomOnImage))
        {
            config.ZoomOnImage = zoomOnImage;
        }

        string? displayOrder = configuration["displayOrder"];
        if (!string.IsNullOrWhiteSpace(displayOrder))
        {
            config.DisplayOrder = displayOrder;
        }

        if (bool.TryParse(configuration["allowEmptySourceSync"], out bool allowEmpty))
        {
            config.AllowEmptySourceSync = allowEmpty;
        }

        return config;
    }

    public string ResolveFolderPath(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        string raw = string.IsNullOrWhiteSpace(this.FolderPath) ? "./media" : this.FolderPath;
        return Path.GetFullPath(raw, baseDirectory);
    }

    public string ResolveCacheFolderPath(string baseDirectory, string? systemdCacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        if (!string.IsNullOrWhiteSpace(this.CacheFolderPath))
        {
            return Path.GetFullPath(this.CacheFolderPath, baseDirectory);
        }

        string? envCache = systemdCacheDirectory ?? Environment.GetEnvironmentVariable("CACHE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(envCache))
        {
            return Path.GetFullPath(envCache, baseDirectory);
        }

        return Path.GetFullPath("./cache", baseDirectory);
    }

    public SlideshowClientConfig ToClientConfig() =>
        new(
            ImageDuration: Math.Max(1, this.ImageDuration),
            FadeTransitionDuration: Math.Max(0, this.FadeTransitionDuration),
            ZoomOnImage: this.ZoomOnImage,
            DisplayOrder: string.IsNullOrWhiteSpace(this.DisplayOrder) ? AlphabeticalDisplayOrder : this.DisplayOrder
        );
}

public sealed record SlideshowClientConfig(
    int ImageDuration,
    int FadeTransitionDuration,
    bool ZoomOnImage,
    string DisplayOrder
);

public sealed record MediaFile(string Name, string Type, string Version);

internal static class SlideshowConfiguration
{
    private const string ConfigFileName = "config.json";

    private static readonly KeyValuePair<string, string?>[] DefaultEntries =
    [
        new("AllowedHosts", "*"),
        new("Urls", "http://localhost:5500"),
        new("Logging:LogLevel:Default", "Warning"),
        new("Logging:LogLevel:Microsoft.Hosting.Lifetime", "Information"),
        new("Logging:Debug:LogLevel:Default", "Debug"),
    ];

    public static string? ConfigureSources(
        IList<IConfigurationSource> sources,
        string baseDirectory,
        string currentDirectory
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        sources.Insert(0, new MemoryConfigurationSource { InitialData = DefaultEntries });

        string? configPath = FindConfigFile(baseDirectory, currentDirectory);
        if (configPath is not null)
        {
            string directory = Path.GetDirectoryName(configPath) ?? baseDirectory;
            JsonConfigurationSource jsonSource = new()
            {
                FileProvider = new PhysicalFileProvider(directory),
                Path = Path.GetFileName(configPath),
                Optional = false,
                ReloadOnChange = false,
            };
            jsonSource.ResolveFileProvider();
            sources.Insert(1, jsonSource);
        }

        return configPath;
    }

    public static void EnsureConfigured(
        IConfiguration configuration,
        string? loadedConfigPath,
        string baseDirectory,
        string currentDirectory
    )
    {
        if (loadedConfigPath is not null || !string.IsNullOrWhiteSpace(configuration["folderPath"]))
        {
            return;
        }

        string baseCandidate = Path.Combine(baseDirectory, ConfigFileName);
        string cwdCandidate = Path.Combine(currentDirectory, ConfigFileName);
        throw new FileNotFoundException(
            $"Missing {ConfigFileName}. Expected at '{baseCandidate}' or '{cwdCandidate}', or pass --folderPath."
        );
    }

    private static string? FindConfigFile(string baseDirectory, string currentDirectory)
    {
        string baseCandidate = Path.Combine(baseDirectory, ConfigFileName);
        if (File.Exists(baseCandidate))
        {
            return baseCandidate;
        }

        string cwdCandidate = Path.Combine(currentDirectory, ConfigFileName);
        if (File.Exists(cwdCandidate))
        {
            return cwdCandidate;
        }

        return null;
    }
}
