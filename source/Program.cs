using System.Runtime.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using SlideshowWebServer;

WebApplicationOptions hostOptions = new()
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
};

WebApplicationBuilder builder = WebApplication.CreateBuilder(hostOptions);

string currentDirectory = Directory.GetCurrentDirectory();
string? loadedConfigPath = SlideshowConfiguration.ConfigureSources(
    builder.Configuration.Sources,
    AppContext.BaseDirectory,
    currentDirectory
);
SlideshowConfiguration.EnsureConfigured(
    builder.Configuration,
    loadedConfigPath,
    AppContext.BaseDirectory,
    currentDirectory
);

SlideshowConfig slideshowConfig = SlideshowConfig.FromConfiguration(builder.Configuration);

builder.Services.Configure<JsonOptions>(static options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, JsonContext.Default);
});

builder.Services.AddSingleton(slideshowConfig);
builder.Services.AddSingleton<MediaService>(sp =>
{
    ILogger<MediaService> logger = sp.GetRequiredService<ILogger<MediaService>>();
    MediaService service = new(slideshowConfig, logger);
    Log.ConfigurationLoaded(
        logger,
        loadedConfigPath ?? "command-line/environment",
        service.SourceFolderPath,
        service.CacheFolderPath,
        service.ClientConfig.ImageDuration,
        service.ClientConfig.DisplayOrder
    );
    return service;
});
builder.Services.AddSingleton<EmbeddedFileProvider>();
builder.Services.AddHostedService<MediaSyncService>();

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService();
    builder.Logging.AddEventLog(ConfigureWindowsEventLog);
}

if (OperatingSystem.IsLinux())
{
    builder.Host.UseSystemd();
}

WebApplication app = builder.Build();
app.AddSlideshowEndpoints();
await app.RunAsync();

public partial class Program
{
    [SupportedOSPlatform("windows")]
    private static void ConfigureWindowsEventLog(EventLogSettings options)
    {
        options.SourceName = "SlideshowService";
        options.LogName = "Application";
    }
}
