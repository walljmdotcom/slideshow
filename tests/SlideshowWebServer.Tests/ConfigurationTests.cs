using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.Memory;
using Xunit;

namespace SlideshowWebServer.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Precedence_DefaultsYieldToConfigJsonThenEnvironmentThenCommandLine()
    {
        using TempWorkspace workspace = new();
        string baseDir = workspace.CreateDirectory("base");
        string cwdDir = workspace.CreateDirectory("cwd");

        string configJson = """
            {
              "imageDuration": 12,
              "folderPath": "./slides",
              "fadeTransitionDuration": 2,
              "zoomOnImage": false,
              "displayOrder": "Random",
            }
            """;
        workspace.WriteFile(baseDir, "config.json", Encoding.UTF8.GetBytes(configJson));

        ConfigurationBuilder builder = new();
        builder.Add(new MemoryConfigurationSource
        {
            InitialData =
            [
                new KeyValuePair<string, string?>("Urls", "http://127.0.0.1:7000"),
                new KeyValuePair<string, string?>("imageDuration", "20"),
            ],
        });
        builder.Add(new CommandLineConfigurationSource
        {
            Args = ["--imageDuration=30", "--fadeTransitionDuration=4"],
        });

        string? loaded = SlideshowConfiguration.ConfigureSources(builder.Sources, baseDir, cwdDir);
        IConfigurationRoot configRoot = builder.Build();
        SlideshowConfig parsed = SlideshowConfig.FromConfiguration(configRoot);

        Assert.Equal(Path.Combine(baseDir, "config.json"), loaded);
        Assert.Equal("http://127.0.0.1:7000", configRoot["Urls"]);
        Assert.Equal(30, parsed.ImageDuration);
        Assert.Equal(4, parsed.FadeTransitionDuration);
        Assert.False(parsed.ZoomOnImage);
        Assert.Equal("Random", parsed.DisplayOrder);
        Assert.Equal(Path.Combine(baseDir, "slides"), parsed.ResolveFolderPath(baseDir));
    }

    [Fact]
    public void Defaults_BindLocalhost5500WhenNotOverridden()
    {
        using TempWorkspace workspace = new();
        string baseDir = workspace.CreateDirectory("base");
        workspace.WriteFile(baseDir, "config.json", "{}"u8);

        ConfigurationBuilder builder = new();
        _ = SlideshowConfiguration.ConfigureSources(builder.Sources, baseDir, baseDir);
        IConfigurationRoot configRoot = builder.Build();

        Assert.Equal("http://localhost:5500", configRoot["Urls"]);
    }

    [Fact]
    public void ConfigSearch_FallsBackToCurrentWorkingDirectoryWhenMissingInBaseDirectory()
    {
        using TempWorkspace workspace = new();
        string baseDir = workspace.CreateDirectory("base");
        string cwdDir = workspace.CreateDirectory("cwd");
        workspace.WriteFile(cwdDir, "config.json", """{"ImageDuration": 9}"""u8);

        ConfigurationBuilder builder = new();
        string? loaded = SlideshowConfiguration.ConfigureSources(builder.Sources, baseDir, cwdDir);
        IConfigurationRoot configRoot = builder.Build();
        SlideshowConfig parsed = SlideshowConfig.FromConfiguration(configRoot);

        Assert.Equal(Path.Combine(cwdDir, "config.json"), loaded);
        Assert.Equal(9, parsed.ImageDuration);
    }

    [Fact]
    public void EnsureConfigured_ThrowsWhenConfigMissingAndNoFolderPathOverride()
    {
        using TempWorkspace workspace = new();
        string baseDir = workspace.CreateDirectory("base");
        string cwdDir = workspace.CreateDirectory("cwd");

        ConfigurationBuilder builder = new();
        string? loaded = SlideshowConfiguration.ConfigureSources(builder.Sources, baseDir, cwdDir);
        IConfigurationRoot configRoot = builder.Build();

        Assert.Throws<FileNotFoundException>(
            () => SlideshowConfiguration.EnsureConfigured(configRoot, loaded, baseDir, cwdDir)
        );
    }

    [Fact]
    public void EnsureConfigured_SucceedsWithoutConfigFileWhenFolderPathPassedViaCli()
    {
        using TempWorkspace workspace = new();
        string baseDir = workspace.CreateDirectory("base");
        string cwdDir = workspace.CreateDirectory("cwd");

        ConfigurationBuilder builder = new();
        builder.AddCommandLine(["--folderPath=/srv/media"]);
        string? loaded = SlideshowConfiguration.ConfigureSources(builder.Sources, baseDir, cwdDir);
        IConfigurationRoot configRoot = builder.Build();

        SlideshowConfiguration.EnsureConfigured(configRoot, loaded, baseDir, cwdDir);
        SlideshowConfig parsed = SlideshowConfig.FromConfiguration(configRoot);
        Assert.Equal("/srv/media", parsed.FolderPath);
    }

    [Fact]
    public void ResolveCacheFolderPath_PrefersExplicitThenSystemdEnvThenDefaultBaseCache()
    {
        using TempWorkspace workspace = new();
        string baseDir = workspace.CreateDirectory("app");
        string systemdDir = workspace.CreateDirectory("var-cache-slideshow");

        SlideshowConfig defaultConfig = new();
        Assert.Equal(
            Path.Combine(baseDir, "cache"),
            defaultConfig.ResolveCacheFolderPath(baseDir, systemdCacheDirectory: "")
        );
        Assert.Equal(systemdDir, defaultConfig.ResolveCacheFolderPath(baseDir, systemdCacheDirectory: systemdDir));

        SlideshowConfig explicitConfig = new() { CacheFolderPath = "./custom-cache" };
        Assert.Equal(
            Path.Combine(baseDir, "custom-cache"),
            explicitConfig.ResolveCacheFolderPath(baseDir, systemdCacheDirectory: systemdDir)
        );
    }
}
