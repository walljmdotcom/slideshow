using System.Text.Json.Serialization;

namespace SlideshowWebServer;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SlideshowClientConfig))]
[JsonSerializable(typeof(MediaFile[]))]
internal sealed partial class JsonContext : JsonSerializerContext;
