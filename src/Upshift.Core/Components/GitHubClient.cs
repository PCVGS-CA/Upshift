using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Upshift.Core.Components;

/// <summary>One file attached to a GitHub release. Sha256 is GitHub's own digest, when it reports one.</summary>
public sealed record ReleaseAsset(string Name, long Size, string Url, string? Sha256);

/// <summary>A GitHub release, as much of it as the app uses.</summary>
public sealed record ReleaseInfo(
    string Tag,
    string Name,
    bool Prerelease,
    DateTimeOffset? Published,
    string Notes,
    string HtmlUrl,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>A file inside a repository at a given tag (for components shipped as a repo file, like NVIDIA's DLSS DLL).</summary>
public sealed record RepoFile(string Name, long Size, string GitBlobSha, string DownloadUrl);

/// <summary>How an answer was obtained: fresh from GitHub, confirmed unchanged (304), or from the cache without asking.</summary>
public enum FetchStatus { Downloaded, NotModified, FromCache }

/// <summary>The releases of one repo, newest first, and how they were obtained.</summary>
public sealed record ReleaseList(IReadOnlyList<ReleaseInfo> Releases, FetchStatus Status, string? Problem, DateTimeOffset? CheckedUtc);

/// <summary>GitHub's hourly limit for requests without an account is used up.</summary>
public sealed class GitHubRateLimitException : ComponentDownloadException
{
    public GitHubRateLimitException(DateTimeOffset? resetsAt) : base(MessageFor(resetsAt)) => ResetsAt = resetsAt;

    public DateTimeOffset? ResetsAt { get; }

    public static string MessageFor(DateTimeOffset? resetsAt) =>
        $"GitHub's limit was reached, try again after {(resetsAt is { } r ? r.ToLocalTime().ToString("t") : "an hour")}.";
}

/// <summary>
/// Reads the GitHub REST API without an account (60 requests an hour). Every answer is cached on disk with its ETag and
/// asked for again with If-None-Match, so an unchanged answer (304) costs nothing against the limit. Once the limit is
/// used up, nothing is sent until it resets; cached answers are used meanwhile.
/// </summary>
public sealed class GitHubClient
{
    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly SemaphoreSlim _rateLock = new(1, 1);

    public GitHubClient(HttpClient http, string cacheDir)
    {
        _http = http;
        _cacheDir = cacheDir;
    }

    private sealed class CachedAnswer
    {
        public string? ETag { get; set; }
        public string? Json { get; set; }
        public DateTimeOffset? CheckedUtc { get; set; }
    }

    private sealed class RateState
    {
        public int? Remaining { get; set; }
        public long? ResetUnix { get; set; }
    }

    /// <summary>When the limit resets, if it's used up right now.</summary>
    public DateTimeOffset? RateLimitedUntil
    {
        get
        {
            var rate = ReadRate();
            return rate.Remaining == 0 && rate.ResetUnix is { } r && DateTimeOffset.FromUnixTimeSeconds(r) > DateTimeOffset.UtcNow
                ? DateTimeOffset.FromUnixTimeSeconds(r)
                : null;
        }
    }

    /// <summary>Requests left this hour, as GitHub last reported it.</summary>
    public int? RateRemaining => ReadRate().Remaining;

    // ---------------- releases ----------------

    /// <summary>GET /repos/{repo}/releases (the newest 30).</summary>
    public async Task<ReleaseList> GetReleasesAsync(string repo, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{repo}/releases?per_page=30";
        try
        {
            var (json, status, checkedUtc) = await GetAsync(url, ct);
            return new ReleaseList(ParseReleases(json), status, null, checkedUtc);
        }
        catch (ComponentDownloadException ex)
        {
            // Offline or out of requests: show what we knew last time, with the reason.
            var cached = ReadCache(url);
            return new ReleaseList(cached?.Json is { } json ? ParseReleases(json) : Array.Empty<ReleaseInfo>(), FetchStatus.FromCache, ex.Message, cached?.CheckedUtc);
        }
    }

    /// <summary>The releases list from the last check, without going online (null if it was never checked).</summary>
    public ReleaseList? GetCachedReleases(string repo)
    {
        var cached = ReadCache($"https://api.github.com/repos/{repo}/releases?per_page=30");
        return cached?.Json is { } json ? new ReleaseList(ParseReleases(json), FetchStatus.FromCache, null, cached.CheckedUtc) : null;
    }

    /// <summary>One release by its exact tag. Answered from the cached list when it's there, otherwise GET releases/tags/{tag}.</summary>
    public async Task<ReleaseInfo> GetReleaseAsync(string repo, string tag, CancellationToken ct)
    {
        if (GetCachedReleases(repo)?.Releases.FirstOrDefault(r => r.Tag == tag) is { } listed) return listed;

        var url = $"https://api.github.com/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}";
        try
        {
            var (json, _, _) = await GetAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            return ParseRelease(doc.RootElement);
        }
        catch (HttpNotFoundException)
        {
            throw new ComponentDownloadException($"{repo} has no release tagged \"{tag}\" on GitHub.");
        }
    }

    /// <summary>GET /repos/{repo}/contents/{path}?ref={tag}: the file's size, git blob hash and raw download address.</summary>
    public async Task<RepoFile> GetRepoFileAsync(string repo, string path, string tag, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{repo}/contents/{path}?ref={Uri.EscapeDataString(tag)}";
        try
        {
            var (json, _, _) = await GetAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            return new RepoFile(e.GetProperty("name").GetString()!, e.GetProperty("size").GetInt64(), e.GetProperty("sha").GetString()!,
                e.GetProperty("download_url").GetString()!);
        }
        catch (HttpNotFoundException)
        {
            throw new ComponentDownloadException($"{repo} has no file {path} at \"{tag}\".");
        }
    }

    // ---------------- HTTP with ETag cache ----------------

    private sealed class HttpNotFoundException : ComponentDownloadException
    {
        public HttpNotFoundException() : base("Not found.") { }
    }

    private async Task<(string Json, FetchStatus Status, DateTimeOffset CheckedUtc)> GetAsync(string url, CancellationToken ct)
    {
        var cache = ReadCache(url) ?? new CachedAnswer();
        if (RateLimitedUntil is { } until) throw new GitHubRateLimitException(until);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (cache.ETag is not null && cache.Json is not null) request.Headers.TryAddWithoutValidation("If-None-Match", cache.ETag);

        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, ct); }
        catch (HttpRequestException ex)
        {
            throw new ComponentDownloadException($"Couldn't reach GitHub ({ex.HttpRequestError}).", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ComponentDownloadException("GitHub didn't answer in time.", ex);
        }

        using (response)
        {
            await SaveRateAsync(response);
            var now = DateTimeOffset.UtcNow;

            if (response.StatusCode == HttpStatusCode.NotModified && cache.Json is not null)
            {
                cache.CheckedUtc = now;
                WriteCache(url, cache);
                return (cache.Json, FetchStatus.NotModified, now);
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests && RateLimitedUntil is { } reset)
                throw new GitHubRateLimitException(reset);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new HttpNotFoundException();
            if (!response.IsSuccessStatusCode)
                throw new ComponentDownloadException($"GitHub answered HTTP {(int)response.StatusCode}.");

            cache.Json = await response.Content.ReadAsStringAsync(ct);
            cache.ETag = response.Headers.ETag?.ToString();
            cache.CheckedUtc = now;
            WriteCache(url, cache);
            return (cache.Json, FetchStatus.Downloaded, now);
        }
    }

    private async Task SaveRateAsync(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) || !int.TryParse(rem.First(), out var remaining)) return;
        var rate = new RateState { Remaining = remaining };
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var rst) && long.TryParse(rst.First(), out var resetAt)) rate.ResetUnix = resetAt;
        await _rateLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(Path.Combine(_cacheDir, "rate-limit.json"), JsonSerializer.Serialize(rate));
        }
        catch (IOException) { }
        finally { _rateLock.Release(); }
    }

    private RateState ReadRate()
    {
        try
        {
            var path = Path.Combine(_cacheDir, "rate-limit.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<RateState>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }

    private string CachePath(string url) =>
        Path.Combine(_cacheDir, Sanitize(url.Replace("https://api.github.com/repos/", "")) + ".json");

    private CachedAnswer? ReadCache(string url)
    {
        try
        {
            var path = CachePath(url);
            return File.Exists(path) ? JsonSerializer.Deserialize<CachedAnswer>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private void WriteCache(string url, CachedAnswer answer)
    {
        Directory.CreateDirectory(_cacheDir);
        var path = CachePath(url);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(answer));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    // ---------------- parsing ----------------

    private static List<ReleaseInfo> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Where(r => !r.TryGetProperty("draft", out var d) || !d.GetBoolean()).Select(ParseRelease).ToList()
            : new List<ReleaseInfo>();
    }

    private static ReleaseInfo ParseRelease(JsonElement r)
    {
        string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var assets = r.TryGetProperty("assets", out var list)
            ? list.EnumerateArray().Select(a => new ReleaseAsset(
                Str(a, "name"),
                a.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                Str(a, "browser_download_url"),
                Str(a, "digest") is { } digest && digest.StartsWith("sha256:") ? digest["sha256:".Length..].ToLowerInvariant() : null)).ToList()
            : new List<ReleaseAsset>();
        return new ReleaseInfo(
            Str(r, "tag_name"),
            Str(r, "name"),
            r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean(),
            DateTimeOffset.TryParse(Str(r, "published_at"), out var published) ? published : null,
            Str(r, "body"),
            Str(r, "html_url"),
            assets);
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '?' or '&' or '=' ? '_' : c));
}
