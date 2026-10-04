using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Upshift.Core.Catalog;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Upshift.Core.Components;

/// <summary>A catalog component downloaded and unpacked into the local component cache.</summary>
public sealed record CachedComponent(string Id, string Version, string Folder, string AssetName, string Sha256, DateTime? DownloadedUtc = null);

public class ComponentDownloadException : Exception
{
    public ComponentDownloadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Downloads releases of catalog components from their GitHub repos and keeps them unpacked under
/// components\{id}-{tag}. Each download is checked against the size GitHub reports and its SHA-256 digest when it
/// gives one (or the git blob hash for a file read straight from a repo). The two most recently downloaded versions
/// of each component are kept; older ones are removed.
/// </summary>
public sealed partial class ComponentStore : IDisposable
{
    private const string CompleteMarker = ".upshift-complete.json";
    /// <summary>The marker's name before the app was renamed; caches made then are still used.</summary>
    private const string LegacyCompleteMarker = ".pcvgs-complete.json";

    /// <summary>How many versions of each component stay on disk.</summary>
    public const int KeepVersions = 2;

    private readonly string _root;
    private readonly HttpClient _http;

    public ComponentStore(string dataDir, string userAgent)
    {
        _root = Path.Combine(dataDir, "components");
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        GitHub = new GitHubClient(_http, Path.Combine(_root, "github-cache"));
    }

    public GitHubClient GitHub { get; }

    public string Root => _root;

    public void Dispose() => _http.Dispose();

    public string FolderFor(CatalogComponent component, string version) =>
        Path.Combine(_root, $"{component.Id}-{Sanitize(version)}");

    public string FolderFor(CatalogComponent component) => FolderFor(component, component.PinnedVersion ?? "latest");

    /// <summary>The pinned (Stable) version if it's already in the cache, without going online.</summary>
    public CachedComponent? TryGetCached(CatalogComponent component) =>
        component.PinnedVersion is { } pinned ? TryGetCached(component, pinned) : null;

    /// <summary>That version if it's already in the cache, without going online.</summary>
    public CachedComponent? TryGetCached(CatalogComponent component, string version) => ReadMarker(FolderFor(component, version), component.Id);

    /// <summary>Every downloaded version of the component, most recently downloaded first.</summary>
    public List<CachedComponent> Downloaded(CatalogComponent component)
    {
        if (!Directory.Exists(_root)) return new();
        return Directory.EnumerateDirectories(_root, component.Id + "-*")
            .Where(d => !d.EndsWith(".staging", StringComparison.OrdinalIgnoreCase))
            .Select(d => ReadMarker(d, component.Id))
            .OfType<CachedComponent>()
            .OrderByDescending(c => c.DownloadedUtc)
            .ToList();
    }

    private static CachedComponent? ReadMarker(string folder, string id)
    {
        var marker = Path.Combine(folder, CompleteMarker);
        if (!File.Exists(marker)) marker = Path.Combine(folder, LegacyCompleteMarker);
        if (!File.Exists(marker)) return null;
        try
        {
            // The stored folder path goes stale when the data folder moves; the folder we found it in is the real one.
            // (components\optiscaler-* also matches optiscaler-dlssnr-*, so the id is checked too.)
            return JsonSerializer.Deserialize<CachedComponent>(File.ReadAllText(marker)) is { } cached && cached.Id == id
                ? cached with { Folder = folder, DownloadedUtc = cached.DownloadedUtc ?? File.GetLastWriteTimeUtc(marker) }
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    /// <summary>Returns the cached copy, or downloads, verifies and unpacks the pinned release.</summary>
    public Task<CachedComponent> EnsureAsync(CatalogComponent component, IProgress<string>? progress, CancellationToken ct) =>
        component.PinnedVersion is { } pinned
            ? EnsureAsync(component, pinned, progress, ct)
            : throw new ComponentDownloadException($"{component.Name} has no pinned version in the catalog.");

    /// <summary>
    /// Returns the cached copy of that release, or downloads, verifies and unpacks it. Versions listed in
    /// <paramref name="keep"/> are never removed to make room (e.g. the one installed in a game being updated).
    /// </summary>
    public async Task<CachedComponent> EnsureAsync(CatalogComponent component, string version, IProgress<string>? progress, CancellationToken ct,
        IReadOnlyCollection<string>? keep = null)
    {
        if (TryGetCached(component, version) is { } cached) return cached;

        progress?.Report($"Looking up {component.Name} {version} on GitHub…");
        var result = component.RepoFilePath is not null
            ? await DownloadRepoFileAsync(component, version, progress, ct)
            : await DownloadReleaseAsync(component, version, progress, ct);
        Prune(component, keep?.Append(version).ToList() ?? new List<string> { version });
        return result;
    }

    /// <summary>
    /// The file this app uses from a release: the only one matching the catalog's pattern, or, when several do
    /// (older nightlies carried a file per build), the one with the highest version number in its name.
    /// </summary>
    public static ReleaseAsset? PickAsset(CatalogComponent component, ReleaseInfo release)
    {
        if (component.AssetPattern is null) return null;
        var pattern = new Regex(component.AssetPattern);
        return release.Assets.Where(a => pattern.IsMatch(a.Name))
            .OrderByDescending(a => VersionIn(a.Name), VersionComparer.Instance)
            .FirstOrDefault();
    }

    /// <summary>True when the release has something this app can download for the component.</summary>
    public static bool IsDownloadable(CatalogComponent component, ReleaseInfo release) =>
        component.RepoFilePath is not null || PickAsset(component, release) is not null;

    private async Task<CachedComponent> DownloadReleaseAsync(CatalogComponent component, string version, IProgress<string>? progress, CancellationToken ct)
    {
        var release = await GitHub.GetReleaseAsync(component.Repo, version, ct);
        var asset = PickAsset(component, release)
            ?? throw new ComponentDownloadException($"{component.Repo} {version} has no file matching {component.AssetPattern}.");

        Directory.CreateDirectory(_root);
        var download = Path.Combine(_root, asset.Name + ".download");
        try
        {
            progress?.Report($"Downloading {asset.Name}…");
            await DownloadAsync(asset.Url, download, progress, ct);

            var size = new FileInfo(download).Length;
            if (asset.Size > 0 && size != asset.Size)
                throw new ComponentDownloadException($"{asset.Name} is {size:N0} bytes but GitHub says {asset.Size:N0}. Nothing was kept.");
            var actual = Convert.ToHexString(await HashFileAsync(download, SHA256.Create(), ct)).ToLowerInvariant();
            if (asset.Sha256 is not null && actual != asset.Sha256)
                throw new ComponentDownloadException($"{asset.Name} doesn't match GitHub's checksum (expected {asset.Sha256}, got {actual}). Nothing was kept.");

            // A release that is one program (PresentMon's console app) is kept as it is; archives are unpacked.
            if (asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return await StoreAsync(component, version, asset.Name, actual, staging =>
                {
                    File.Copy(download, Path.Combine(staging, asset.Name));
                    return Task.CompletedTask;
                }, ct);
            progress?.Report($"Unpacking {asset.Name}…");
            return await StoreAsync(component, version, asset.Name, actual, staging => Task.Run(() => Extract(download, staging), ct), ct);
        }
        finally
        {
            try { File.Delete(download); } catch (IOException) { }
        }
    }

    /// <summary>A component that is a single file in its repo (NVIDIA's DLSS DLL): read at the release tag, checked against the git blob hash.</summary>
    private async Task<CachedComponent> DownloadRepoFileAsync(CatalogComponent component, string version, IProgress<string>? progress, CancellationToken ct)
    {
        var file = await GitHub.GetRepoFileAsync(component.Repo, component.RepoFilePath!, version, ct);
        Directory.CreateDirectory(_root);
        var download = Path.Combine(_root, $"{component.Id}-{Sanitize(version)}-{file.Name}.download");
        try
        {
            progress?.Report($"Downloading {file.Name}…");
            await DownloadAsync(file.DownloadUrl, download, progress, ct);

            var size = new FileInfo(download).Length;
            if (size != file.Size)
                throw new ComponentDownloadException($"{file.Name} is {size:N0} bytes but GitHub says {file.Size:N0}. Nothing was kept.");
            var blob = await GitBlobShaAsync(download, ct);
            if (!blob.Equals(file.GitBlobSha, StringComparison.OrdinalIgnoreCase))
                throw new ComponentDownloadException($"{file.Name} doesn't match GitHub's hash for it. Nothing was kept.");

            var sha256 = Convert.ToHexString(await HashFileAsync(download, SHA256.Create(), ct)).ToLowerInvariant();
            return await StoreAsync(component, version, file.Name, sha256, staging =>
            {
                File.Copy(download, Path.Combine(staging, file.Name));
                return Task.CompletedTask;
            }, ct);
        }
        finally
        {
            try { File.Delete(download); } catch (IOException) { }
        }
    }

    private async Task<CachedComponent> StoreAsync(CatalogComponent component, string version, string assetName, string sha256,
        Func<string, Task> fill, CancellationToken ct)
    {
        var folder = FolderFor(component, version);
        var staging = folder + ".staging";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        await fill(staging);

        var result = new CachedComponent(component.Id, version, folder, assetName, sha256, DateTime.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(staging, CompleteMarker), JsonSerializer.Serialize(result), ct);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.Move(staging, folder);
        return result;
    }

    /// <summary>Deletes the downloaded copy of one version. Games it was installed into are not affected.</summary>
    public void Remove(CatalogComponent component, string version)
    {
        var folder = FolderFor(component, version);
        if (ReadMarker(folder, component.Id) is not null) Directory.Delete(folder, recursive: true);
    }

    /// <summary>Keeps the most recently downloaded versions (and any in <paramref name="keep"/>), removes the rest.</summary>
    private void Prune(CatalogComponent component, IReadOnlyCollection<string> keep)
    {
        var all = Downloaded(component);
        var slots = Math.Max(0, KeepVersions - all.Count(c => keep.Contains(c.Version)));
        foreach (var cached in all)
        {
            if (keep.Contains(cached.Version)) continue;
            if (slots > 0)
            {
                slots--;
                continue;
            }
            try { Directory.Delete(cached.Folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task DownloadAsync(string url, string target, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new ComponentDownloadException($"Download failed: HTTP {(int)response.StatusCode}.");

            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(target);
            var buffer = new byte[1 << 16];
            long done = 0, lastReport = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total is > 0 && done - lastReport > total / 20)
                {
                    lastReport = done;
                    progress?.Report($"Downloading… {done * 100 / total}%");
                }
            }
        }
        catch (HttpRequestException ex)
        {
            throw new ComponentDownloadException($"Download failed ({ex.HttpRequestError}).", ex);
        }
    }

    private static void Extract(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var archive = ArchiveFactory.Open(archivePath);
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory && e.Key is not null))
        {
            // Refuse entries that would land outside the destination ("zip slip").
            var path = Path.GetFullPath(Path.Combine(destination, entry.Key!));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new ComponentDownloadException($"The archive contains an unsafe path: {entry.Key}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.WriteToFile(path, new ExtractionOptions { Overwrite = true });
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, HashAlgorithm algorithm, CancellationToken ct)
    {
        using (algorithm)
        {
            await using var stream = File.OpenRead(path);
            return await algorithm.ComputeHashAsync(stream, ct);
        }
    }

    /// <summary>The hash git (and GitHub's contents API) gives a file: SHA-1 of "blob {size}\0" followed by the bytes.</summary>
    private static async Task<string> GitBlobShaAsync(string path, CancellationToken ct)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(Encoding.ASCII.GetBytes($"blob {new FileInfo(path).Length}\0"));
        await using var stream = File.OpenRead(path);
        var buffer = new byte[1 << 16];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) sha1.AppendData(buffer, 0, read);
        return Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>The first version-looking number in a name ("OptiScaler_0.7.0-pre9_20250101.7z" → 0.7.0), or null.</summary>
    public static Version? VersionIn(string name)
    {
        var match = Regex.Match(name, @"\d+(?:\.\d+){1,3}");
        return match.Success && Version.TryParse(match.Value, out var v) ? v : null;
    }

    private sealed class VersionComparer : IComparer<Version?>
    {
        public static readonly VersionComparer Instance = new();
        public int Compare(Version? x, Version? y) => x is null ? (y is null ? 0 : -1) : y is null ? 1 : x.CompareTo(y);
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '_' : c));
}
