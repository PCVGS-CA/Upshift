using Upshift.Core.Catalog;
using Upshift.Core.Services;

namespace Upshift.Core.Components;

/// <summary>What the last check found for one component.</summary>
public sealed class ComponentStatus
{
    public ComponentStatus(CatalogComponent component) => Component = component;

    public CatalogComponent Component { get; }

    /// <summary>Newest first, by GitHub publish date.</summary>
    public IReadOnlyList<ReleaseInfo> Releases { get; set; } = Array.Empty<ReleaseInfo>();
    public FetchStatus? Status { get; set; }
    /// <summary>Why the last check didn't reach GitHub (offline, rate limit), if it didn't.</summary>
    public string? Problem { get; set; }
    public DateTimeOffset? CheckedUtc { get; set; }

    /// <summary>The catalog's tested version, or the newest full release when none is pinned.</summary>
    public ReleaseInfo? Stable => UpdateChecker.StableOf(Component, Releases);

    /// <summary>The newest release, pre-releases included.</summary>
    public ReleaseInfo? Beta => UpdateChecker.BetaOf(Component, Releases);

    public ReleaseInfo? For(UpdateChannel channel) => channel == UpdateChannel.Beta ? Beta : Stable;
}

/// <summary>
/// Checks each catalog component's GitHub releases (one request per repo, answered with 304 when nothing changed),
/// works out the Stable and Beta versions, and downloads new releases for components set to "Keep updated".
/// </summary>
public sealed class UpdateChecker
{
    /// <summary>Automatic checks at start-up happen at most this often.</summary>
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);

    private readonly ComponentStore _store;
    private readonly SettingsStore _settings;
    private readonly IReadOnlyList<CatalogComponent> _components;
    private readonly Dictionary<string, ComponentStatus> _statuses;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    public UpdateChecker(ComponentStore store, SettingsStore settings, IReadOnlyList<CatalogComponent> components)
    {
        _store = store;
        _settings = settings;
        _components = components;
        _statuses = components.ToDictionary(c => c.Id, c => new ComponentStatus(c));
        LoadCached();
    }

    /// <summary>Raised (on a background thread) when statuses or downloads change.</summary>
    public event Action? Changed;

    public bool IsChecking { get; private set; }

    /// <summary>One line about the last check, e.g. "Checked 8 GitHub projects at 14:05: 8 not modified."</summary>
    public string? LastSummary { get; private set; }

    public ComponentStatus Status(string componentId) => _statuses[componentId];

    public bool AutoCheckDue =>
        _settings.Current.AutoCheckUpdates
        && (_settings.Current.LastUpdateCheckUtc is not { } last || DateTime.UtcNow - last >= AutoCheckInterval);

    /// <summary>The release the user's channel points at for this component.</summary>
    public ReleaseInfo? Target(CatalogComponent component) =>
        Status(component.Id).For(_settings.Current.For(PreferenceId(component)).Channel);

    /// <summary>Components shown on another's card (AMD-NR's runtime) share that card's channel and "Keep updated".</summary>
    public static string PreferenceId(CatalogComponent component) => component.GroupWith ?? component.Id;

    /// <summary>What the previous check saw, from the cache, so versions show without going online.</summary>
    private void LoadCached()
    {
        foreach (var status in _statuses.Values)
        {
            if (_store.GitHub.GetCachedReleases(status.Component.Repo) is not { } list) continue;
            status.Releases = Sorted(list.Releases);
            status.CheckedUtc = list.CheckedUtc;
        }
    }

    /// <summary>Asks GitHub about every component. Repos shared by two components are asked once.</summary>
    public async Task<string> CheckAsync(CancellationToken ct)
    {
        await _checkLock.WaitAsync(ct);
        IsChecking = true;
        Changed?.Invoke();
        try
        {
            int downloaded = 0, notModified = 0, failed = 0;
            string? problem = null;
            foreach (var repo in _components.Select(c => c.Repo).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var list = await _store.GitHub.GetReleasesAsync(repo, ct);
                switch (list.Status)
                {
                    case FetchStatus.Downloaded: downloaded++; break;
                    case FetchStatus.NotModified: notModified++; break;
                    default: failed++; problem ??= list.Problem; break;
                }
                foreach (var status in _statuses.Values.Where(s => s.Component.Repo.Equals(repo, StringComparison.OrdinalIgnoreCase)))
                {
                    if (list.Releases.Count > 0 || list.Problem is null) status.Releases = Sorted(list.Releases);
                    status.Status = list.Status;
                    status.Problem = list.Problem;
                    if (list.Problem is null) status.CheckedUtc = list.CheckedUtc;
                }
                Changed?.Invoke();
            }

            var settings = _settings.Current;
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _settings.Save(settings);

            var total = downloaded + notModified + failed;
            var parts = new List<string>();
            if (notModified > 0) parts.Add($"{notModified} not modified");
            if (downloaded > 0) parts.Add($"{downloaded} with new or changed releases");
            if (failed > 0) parts.Add($"{failed} not reached");
            LastSummary = $"Checked {total} GitHub projects at {DateTime.Now:t}: {string.Join(", ", parts)}."
                          + (_store.GitHub.RateLimitedUntil is { } until ? " " + GitHubRateLimitException.MessageFor(until)
                              : problem is not null ? " " + problem : "");
            return LastSummary;
        }
        finally
        {
            IsChecking = false;
            _checkLock.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Downloads the channel's newest release of every component set to "Keep updated". Returns what happened.</summary>
    public async Task<List<string>> DownloadKeptUpdatedAsync(CancellationToken ct)
    {
        var messages = new List<string>();
        foreach (var component in _components)
        {
            if (component.BundledIn is not null || !_settings.Current.For(PreferenceId(component)).KeepUpdated) continue;
            if (Target(component) is not { } target || !ComponentStore.IsDownloadable(component, target)) continue;
            if (_store.TryGetCached(component, target.Tag) is not null) continue;
            try
            {
                await _store.EnsureAsync(component, target.Tag, null, ct);
                messages.Add($"Downloaded {component.Name} {target.Tag}.");
            }
            catch (ComponentDownloadException ex)
            {
                messages.Add($"{component.Name} {target.Tag}: {ex.Message}");
            }
            Changed?.Invoke();
        }
        return messages;
    }

    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>True when <paramref name="candidate"/> is a later release than <paramref name="installed"/>.</summary>
    public bool IsNewer(CatalogComponent component, string candidate, string? installed)
    {
        if (installed is null) return true;
        if (candidate == installed) return false;
        var releases = Status(component.Id).Releases;
        var a = releases.FirstOrDefault(r => r.Tag == candidate)?.Published;
        var b = releases.FirstOrDefault(r => r.Tag == installed)?.Published;
        if (a is not null && b is not null) return a > b;
        var va = ComponentStore.VersionIn(candidate);
        var vb = ComponentStore.VersionIn(installed);
        return va is not null && vb is not null && va > vb;
    }

    public static ReleaseInfo? StableOf(CatalogComponent component, IReadOnlyList<ReleaseInfo> releases)
    {
        if (component.PinnedVersion is { } pinned)
            return releases.FirstOrDefault(r => r.Tag == pinned)
                   // Pinned releases older than the newest 30 still download by tag.
                   ?? new ReleaseInfo(pinned, pinned, false, null, "", $"https://github.com/{component.Repo}/releases/tag/{pinned}", Array.Empty<ReleaseAsset>());
        return releases.FirstOrDefault(r => !r.Prerelease);
    }

    /// <summary>The most recently published release, pre-releases included.</summary>
    public static ReleaseInfo? BetaOf(CatalogComponent component, IReadOnlyList<ReleaseInfo> releases) => releases.FirstOrDefault();

    /// <summary>Newest first by publish date (not by version number: tags like 0.3 and 0.130 don't compare as numbers).</summary>
    private static List<ReleaseInfo> Sorted(IEnumerable<ReleaseInfo> releases) =>
        releases.OrderByDescending(r => r.Published ?? DateTimeOffset.MinValue).ToList();

    /// <summary>
    /// The version of a bundled component (fakenvapi, dlssg-to-fsr3) as the host's release notes state it, e.g.
    /// "Fakenvapi 1.4.1" in OptiScaler v0.9.4's notes. Null when the notes aren't known or don't mention it.
    /// </summary>
    public string? BundledVersion(CatalogComponent component, string hostVersion)
    {
        if (component.BundledIn is not { } host || component.BundledVersionPattern is not { } pattern) return null;
        if (!_statuses.TryGetValue(host, out var status)) return null;
        var notes = status.Releases.FirstOrDefault(r => r.Tag == hostVersion)?.Notes;
        if (string.IsNullOrEmpty(notes)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(notes, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }
}
