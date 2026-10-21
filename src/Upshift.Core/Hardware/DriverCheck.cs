using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Upshift.Core.Models;

namespace Upshift.Core.Hardware;

/// <summary>Where the newest driver is looked up, per vendor (catalog "driverChecks"). Upshift only reads; it never downloads a driver.</summary>
public sealed class DriverChecks
{
    /// <summary>NVIDIA's driver lookup (JSON): the newest Game Ready driver's version and its details page.</summary>
    public DriverCheckSource? Nvidia { get; set; }
    /// <summary>AMD: the release notes address with {version} as "26-10-1"; the newest that exists is the newest driver.</summary>
    public DriverCheckSource? Amd { get; set; }
    /// <summary>Intel: a download page and the pattern its version matches.</summary>
    public DriverCheckSource? Intel { get; set; }
}

public sealed class DriverCheckSource
{
    public string Url { get; set; } = "";
    public string? VersionPattern { get; set; }
    public string? ReleaseNotesUrl { get; set; }
}

/// <summary>The newest driver, when it's newer than the installed one.</summary>
public sealed record NewerDriver(string Version, string ReleaseNotesUrl);

/// <summary>What the last lookup found (data\driver-check.json), so it runs at most once a day.</summary>
public sealed class DriverCheckRecord
{
    public DateTime CheckedUtc { get; set; }
    public string? Vendor { get; set; }
    public string? Latest { get; set; }
    public string? ReleaseNotesUrl { get; set; }
}

/// <summary>
/// Compares the graphics driver with the newest from NVIDIA, AMD or Intel. At most one lookup a day; a failed lookup
/// returns null and is written to logs\driver-check.log, never shown as an error.
/// </summary>
public static class DriverCheck
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public static string RecordPath(string dataDir) => Path.Combine(dataDir, "driver-check.json");

    public static async Task<NewerDriver?> RunAsync(GpuInfo? gpu, string? installed, DriverChecks? sources, string dataDir,
        HttpClient http, DateTime nowUtc, CancellationToken ct = default)
    {
        try
        {
            if (gpu is null || installed is null || sources is null) return null;
            var vendor = gpu.Vendor.ToString();
            var record = ReadRecord(dataDir);
            if (record is null || record.Vendor != vendor || nowUtc - record.CheckedUtc >= Interval || record.CheckedUtc > nowUtc)
            {
                record = new DriverCheckRecord { CheckedUtc = nowUtc, Vendor = vendor };
                try
                {
                    var found = gpu.Vendor switch
                    {
                        GpuVendor.Nvidia when sources.Nvidia is { } s => await NvidiaAsync(s, http, ct),
                        GpuVendor.Amd when sources.Amd is { } s => await AmdAsync(s, http, nowUtc, ct),
                        GpuVendor.Intel when sources.Intel is { } s => await PageAsync(s, http, ct),
                        _ => null
                    };
                    if (found is null) Log(dataDir, $"{vendor}: no version found");
                    else (record.Latest, record.ReleaseNotesUrl) = (found.Version, found.ReleaseNotesUrl);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    Log(dataDir, $"{vendor}: {ex.GetType().Name}: {ex.Message}");
                }
                WriteRecord(dataDir, record);
            }
            return record.Latest is { } latest && record.ReleaseNotesUrl is { } notes && DriverDetails.CompareVersions(latest, installed) > 0
                ? new NewerDriver(latest, notes) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            Log(dataDir, $"check failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // ---------------- per vendor ----------------

    private static async Task<NewerDriver?> NvidiaAsync(DriverCheckSource source, HttpClient http, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync(source.Url, ct));
        var info = doc.RootElement.GetProperty("IDS")[0].GetProperty("downloadInfo");
        var version = WebUtility.UrlDecode(info.GetProperty("Version").GetString());
        var details = info.TryGetProperty("DetailsURL", out var d) ? WebUtility.UrlDecode(d.GetString()) : null;
        if (string.IsNullOrWhiteSpace(version) || DriverDetails.CompareVersions(version, "0") is null) return null;
        var notes = details is { } u && u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? u : source.ReleaseNotesUrl;
        return notes is null ? null : new NewerDriver(version.Trim(), notes);
    }

    /// <summary>
    /// AMD publishes no version list, but every Adrenalin release has a release notes page. From this month back (up to a
    /// year), the first month with a ".1" release is the newest; then .2, .3… while they exist.
    /// </summary>
    private static async Task<NewerDriver?> AmdAsync(DriverCheckSource source, HttpClient http, DateTime nowUtc, CancellationToken ct)
    {
        string Url(int y, int m, int p) => source.Url.Replace("{version}", $"{y % 100}-{m}-{p}");
        for (var back = 0; back < 12; back++)
        {
            var month = new DateTime(nowUtc.Year, nowUtc.Month, 1).AddMonths(-back);
            if (!await ExistsAsync(http, Url(month.Year, month.Month, 1), ct)) continue;
            var point = 1;
            while (point < 9 && await ExistsAsync(http, Url(month.Year, month.Month, point + 1), ct)) point++;
            return new NewerDriver($"{month.Year % 100}.{month.Month}.{point}", Url(month.Year, month.Month, point));
        }
        return null;
    }

    private static async Task<NewerDriver?> PageAsync(DriverCheckSource source, HttpClient http, CancellationToken ct)
    {
        if (source.VersionPattern is not { Length: > 0 } pattern) return null;
        var html = await http.GetStringAsync(source.Url, ct);
        var best = Regex.Matches(html, pattern).Select(m => m.Value)
            .Where(v => DriverDetails.CompareVersions(v, "0") is not null)
            .OrderByDescending(v => v, Comparer<string>.Create((a, b) => DriverDetails.CompareVersions(a, b) ?? 0))
            .FirstOrDefault();
        return best is null ? null : new NewerDriver(best, source.ReleaseNotesUrl ?? source.Url);
    }

    private static async Task<bool> ExistsAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();   // anything else (blocked, server error) is a failed lookup, not "no driver"
        return true;
    }

    // ---------------- record and log ----------------

    private static DriverCheckRecord? ReadRecord(string dataDir)
    {
        try { return JsonSerializer.Deserialize<DriverCheckRecord>(File.ReadAllText(RecordPath(dataDir))); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static void WriteRecord(string dataDir, DriverCheckRecord record)
    {
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(RecordPath(dataDir), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Log(dataDir, $"couldn't save the record: {ex.Message}"); }
    }

    public static void Log(string dataDir, string line)
    {
        try
        {
            var dir = Path.Combine(dataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "driver-check.log"),
                $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
