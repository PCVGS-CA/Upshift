using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Upshift.Core.Catalog;
using Upshift.Core.Detection;
using Upshift.Core.Install;

namespace Upshift.Core.Components;

/// <summary>A downloaded, checked upscaler DLL (e.g. nvngx_dlss.dll 310.9.1) in components\upscaler-files.</summary>
public sealed record CachedUpscalerFile(string Path, string Version, string Sha256, DateTime DownloadedUtc);

public sealed partial class ComponentStore
{
    private const string UpscalerFolder = "upscaler-files";

    /// <summary>Raised after a new upscaler DLL has been downloaded and checked (on the downloading thread).</summary>
    public event Action<UpscalerFileSource>? UpscalerFileDownloaded;

    private string UpscalerFileFolder(UpscalerFileSource source) =>
        Path.Combine(_root, UpscalerFolder, $"{source.ComponentId}-{Sanitize(source.Tag)}");

    /// <summary>The file if it has been downloaded and checked before, without going online.</summary>
    public CachedUpscalerFile? TryGetUpscalerFile(UpscalerFileSource source)
    {
        var path = Path.Combine(UpscalerFileFolder(source), source.File);
        var marker = path + ".json";
        if (!File.Exists(path) || !File.Exists(marker)) return null;
        try { return JsonSerializer.Deserialize<CachedUpscalerFile>(File.ReadAllText(marker)) is { } c ? c with { Path = path } : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    /// <summary>
    /// Returns the cached copy, or downloads the file: straight from the repository at the tag (checked against
    /// GitHub's git blob hash) or out of the release's zip (checked against GitHub's size and SHA-256). Either way the
    /// DLL must have the catalog's version and a valid signature from its vendor, or nothing is kept.
    /// </summary>
    public async Task<CachedUpscalerFile> EnsureUpscalerFileAsync(UpscalerCatalog catalog, UpscalerFileSource source,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (TryGetUpscalerFile(source) is { } cached) return cached;
        var component = catalog.Components.FirstOrDefault(c => c.Id == source.ComponentId)
            ?? throw new ComponentDownloadException($"The catalog has no component {source.ComponentId}.");
        var signers = catalog.UpscalerFiles.Signers.GetValueOrDefault(source.Family) ?? new List<string>();
        if (signers.Count == 0) throw new ComponentDownloadException($"The catalog names no signer for {source.Family} files.");

        var folder = UpscalerFileFolder(source);
        Directory.CreateDirectory(folder);
        var staging = Path.Combine(folder, source.File + ".download");
        try
        {
            if (source.RepoPath is not null) await DownloadRepoPathAsync(component, source, staging, progress, ct);
            else if (source.ArchivePath is not null) await DownloadFromArchiveAsync(component, source, staging, progress, ct);
            else throw new ComponentDownloadException($"The catalog doesn't say where {source.File} {source.Version} comes from.");

            // The DLL itself: the promised version, signed by its vendor.
            var version = FileVersions.Read(staging);
            if (!SameVersion(version, source.Version))
                throw new ComponentDownloadException($"{source.File} from {component.Name} {source.Tag} is version {version ?? "unknown"}, not {source.Version}. Nothing was kept.");
            if (SignatureCheck.Problem(staging, signers) is { } problem)
                throw new ComponentDownloadException($"{problem} Nothing was kept.");

            var path = Path.Combine(folder, source.File);
            File.Move(staging, path, overwrite: true);
            var result = new CachedUpscalerFile(path, source.Version, OptiScalerInstaller.Sha256(path), DateTime.UtcNow);
            await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(result), ct);
            UpscalerFileDownloaded?.Invoke(source);
            return result;
        }
        finally
        {
            try { File.Delete(staging); } catch (IOException) { }
        }
    }

    private async Task DownloadRepoPathAsync(CatalogComponent component, UpscalerFileSource source, string target,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report($"Looking up {source.File} {source.Version} on GitHub…");
        var file = await GitHub.GetRepoFileAsync(component.Repo, source.RepoPath!, source.Tag, ct);
        progress?.Report($"Downloading {source.File} {source.Version}…");
        await DownloadAsync(file.DownloadUrl, target, progress, ct);
        if (new FileInfo(target).Length != file.Size)
            throw new ComponentDownloadException($"{source.File} is {new FileInfo(target).Length:N0} bytes but GitHub says {file.Size:N0}. Nothing was kept.");
        if (!(await GitBlobShaAsync(target, ct)).Equals(file.GitBlobSha, StringComparison.OrdinalIgnoreCase))
            throw new ComponentDownloadException($"{source.File} doesn't match GitHub's hash for it. Nothing was kept.");
    }

    /// <summary>Downloads the release zip, checks it, takes out the one file, and deletes the zip.</summary>
    private async Task DownloadFromArchiveAsync(CatalogComponent component, UpscalerFileSource source, string target,
        IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report($"Looking up {component.Name} {source.Tag} on GitHub…");
        var release = await GitHub.GetReleaseAsync(component.Repo, source.Tag, ct);
        var asset = PickAsset(component, release)
            ?? throw new ComponentDownloadException($"{component.Repo} {source.Tag} has no file matching {component.AssetPattern}.");

        var zip = target + ".zip";
        try
        {
            progress?.Report($"Downloading {asset.Name} for {source.File}…");
            await DownloadAsync(asset.Url, zip, progress, ct);
            var size = new FileInfo(zip).Length;
            if (asset.Size > 0 && size != asset.Size)
                throw new ComponentDownloadException($"{asset.Name} is {size:N0} bytes but GitHub says {asset.Size:N0}. Nothing was kept.");
            if (asset.Sha256 is not null)
            {
                var actual = Convert.ToHexString(await HashFileAsync(zip, SHA256.Create(), ct)).ToLowerInvariant();
                if (actual != asset.Sha256)
                    throw new ComponentDownloadException($"{asset.Name} doesn't match GitHub's checksum. Nothing was kept.");
            }

            using var archive = ZipFile.OpenRead(zip);
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(source.ArchivePath, StringComparison.OrdinalIgnoreCase))
                ?? throw new ComponentDownloadException($"{asset.Name} has no {source.ArchivePath}.");
            entry.ExtractToFile(target, overwrite: true);
        }
        finally
        {
            try { File.Delete(zip); } catch (IOException) { }
        }
    }

    /// <summary>"310.9.1" and "310.9.1.0" are the same version.</summary>
    public static bool SameVersion(string? a, string? b) =>
        a is not null && b is not null && Upshift.Core.Install.UpscalerFiles.ParseVersion(a) == Upshift.Core.Install.UpscalerFiles.ParseVersion(b);
}
