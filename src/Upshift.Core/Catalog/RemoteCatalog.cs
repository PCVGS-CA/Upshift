using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Upshift.Core.Catalog;

/// <summary>
/// An optional catalog.json from the address in Settings ("catalogUrl"). It's downloaded in the background and kept in
/// the data folder; from the next start it replaces the built-in catalog, but only while it's valid, newer than the
/// built-in one ("updated" date) and the setting still points at the address it came from.
/// </summary>
public static class RemoteCatalog
{
    private sealed class Saved
    {
        public string Url { get; set; } = "";
        public string? ETag { get; set; }
        public DateTime DownloadedUtc { get; set; }
        public string Json { get; set; } = "";
    }

    private static string SavedPath(string dataDir) => Path.Combine(dataDir, "catalog", "remote-catalog.json");

    /// <summary>The downloaded catalog when it should be used instead of <paramref name="builtIn"/>; otherwise null.</summary>
    public static UpscalerCatalog? LoadIfNewer(string dataDir, string? catalogUrl, UpscalerCatalog builtIn)
    {
        if (string.IsNullOrWhiteSpace(catalogUrl)) return null;
        try
        {
            var path = SavedPath(dataDir);
            if (!File.Exists(path) || JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) is not { } saved) return null;
            if (!saved.Url.Equals(catalogUrl.Trim(), StringComparison.Ordinal)) return null;
            return Validate(saved.Json, builtIn, out var catalog) is null && IsNewer(catalog!, builtIn) ? catalog : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Downloads the catalog at <paramref name="url"/> and keeps it if it's valid and newer. Returns a line for the Updates page.</summary>
    public static async Task<string> RefreshAsync(string dataDir, string url, UpscalerCatalog builtIn, string userAgent, CancellationToken ct)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "The catalog address must start with https://, so the built-in catalog is used.";

        var path = SavedPath(dataDir);
        Saved? previous = null;
        try { previous = File.Exists(path) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)) : null; }
        catch (JsonException) { }
        if (previous is not null && previous.Url != url) previous = null;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (previous?.ETag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", previous.ETag);

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotModified && previous is not null)
                return Describe(previous.Json, builtIn, "not modified");
            if (!response.IsSuccessStatusCode)
                return $"The online catalog answered HTTP {(int)response.StatusCode}, so the built-in catalog is used.";

            var json = await response.Content.ReadAsStringAsync(ct);
            if (Validate(json, builtIn, out var catalog) is { } problem)
                return $"The downloaded catalog isn't valid ({problem}), so the built-in catalog is used.";
            if (!IsNewer(catalog!, builtIn))
                return $"The downloaded catalog ({catalog!.Updated}) isn't newer than the built-in one ({builtIn.Updated}), so the built-in catalog is used.";

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var saved = new Saved { Url = url, ETag = response.Headers.ETag?.ToString(), DownloadedUtc = DateTime.UtcNow, Json = json };
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(saved));
            File.Move(path + ".tmp", path, overwrite: true);
            return $"A newer catalog ({catalog!.Updated}) was downloaded. It's used from the next start of Upshift.";
        }
        catch (HttpRequestException ex)
        {
            return $"The catalog couldn't be downloaded ({ex.HttpRequestError}), so the {(previous is null ? "built-in" : "last downloaded")} catalog is used.";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return "The online catalog didn't answer in time, so the built-in catalog is used.";
        }
    }

    private static string Describe(string json, UpscalerCatalog builtIn, string how) =>
        Validate(json, builtIn, out var catalog) is null && IsNewer(catalog!, builtIn)
            ? $"Online catalog ({catalog!.Updated}): {how}."
            : $"Online catalog: {how}; the built-in catalog is newer and is used.";

    /// <summary>Null when the JSON is a usable catalog; otherwise what's wrong with it.</summary>
    public static string? Validate(string json, UpscalerCatalog builtIn, out UpscalerCatalog? catalog)
    {
        catalog = null;
        try { catalog = JsonSerializer.Deserialize<UpscalerCatalog>(json, CatalogLoader.JsonOptions); }
        catch (JsonException ex) { return $"not readable JSON: {ex.Message}"; }
        if (catalog is null) return "empty";
        if (catalog.Schema != builtIn.Schema) return $"schema {catalog.Schema}, this version of Upshift reads schema {builtIn.Schema}";
        if (ParseDate(catalog.Updated) is null) return "no valid \"updated\" date";
        if (catalog.Components.Count == 0) return "no components";
        var repo = new Regex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$");
        foreach (var c in catalog.Components)
        {
            if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Name)) return "a component without an id or name";
            if (!repo.IsMatch(c.Repo)) return $"{c.Id} has an invalid repo \"{c.Repo}\"";
            if (c.AssetPattern is not null)
            {
                try { _ = new Regex(c.AssetPattern); }
                catch (ArgumentException) { return $"{c.Id} has an invalid assetPattern"; }
            }
        }
        if (catalog.Components.GroupBy(c => c.Id).Any(g => g.Count() > 1)) return "duplicate component ids";
        if (catalog.Components.All(c => c.Id != "optiscaler")) return "no OptiScaler entry";
        return null;
    }

    private static bool IsNewer(UpscalerCatalog candidate, UpscalerCatalog builtIn) =>
        ParseDate(candidate.Updated) is { } a && (ParseDate(builtIn.Updated) is not { } b || a > b);

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
