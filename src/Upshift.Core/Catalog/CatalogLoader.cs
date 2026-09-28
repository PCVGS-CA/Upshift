using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Upshift.Core.Catalog;

public static class CatalogLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The copy of catalog.json compiled into the app.</summary>
    public static UpscalerCatalog LoadBuiltIn()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("catalog.json")
            ?? throw new InvalidOperationException("The built-in catalog.json is missing from the build.");
        return JsonSerializer.Deserialize<UpscalerCatalog>(stream, JsonOptions) ?? new UpscalerCatalog();
    }
}
