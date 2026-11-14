using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Upshift.Core.Catalog;
using Upshift.Core.Hardware;
using Upshift.Core.Models;

namespace Upshift.Core.Store;

/// <summary>
/// Loads store.json: the copy built into the app, or a newer one downloaded from the repo (main, like catalog.v2.json), so new
/// entries need no app update. Every entry is checked when loaded (<see cref="Problems"/>); one that breaks a rule is left out
/// and the reason kept in <see cref="Rejected"/>.
/// </summary>
public sealed class StoreCatalog
{
    public const string MainUrl = "https://raw.githubusercontent.com/PCVGS-CA/Upshift/main/src/Upshift.Core/Catalog/store.json";

    public string Updated { get; private init; } = "";
    public IReadOnlyList<StoreEntry> Entries { get; private init; } = Array.Empty<StoreEntry>();
    /// <summary>Entries left out, with why.</summary>
    public IReadOnlyList<(string Id, string Problem)> Rejected { get; private init; } = Array.Empty<(string, string)>();
    public bool FromDownload { get; private init; }

    public StoreEntry? Find(string id) => Entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions Json = new(CatalogLoader.JsonOptions) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static StoreFile ParseFile(string json) => JsonSerializer.Deserialize<StoreFile>(json, Json) ?? new StoreFile();

    /// <summary>The checked store from JSON text: valid entries kept, the rest listed as rejected.</summary>
    public static StoreCatalog FromJson(string json, bool fromDownload = false)
    {
        var file = ParseFile(json);
        var kept = new List<StoreEntry>();
        var rejected = new List<(string, string)>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in file.Entries)
        {
            var problem = Problems(entry).FirstOrDefault() ?? (ids.Add(entry.Id) ? null : "the id is used twice");
            if (problem is null) kept.Add(entry); else rejected.Add((entry.Id, problem));
        }
        return new StoreCatalog { Updated = file.Updated, Entries = kept, Rejected = rejected, FromDownload = fromDownload };
    }

    public static StoreCatalog LoadBuiltIn()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("store.json")
            ?? throw new InvalidOperationException("store.json isn't built into the app.");
        using var reader = new StreamReader(stream);
        return FromJson(reader.ReadToEnd());
    }

    private static string SavedPath(string dataDir) => Path.Combine(dataDir, "catalog", "remote-store.json");

    /// <summary>The downloaded store when it's valid JSON and newer than the built-in one; otherwise the built-in one.</summary>
    public static StoreCatalog Load(string dataDir)
    {
        var builtIn = LoadBuiltIn();
        try
        {
            var path = SavedPath(dataDir);
            if (!File.Exists(path)) return builtIn;
            var saved = FromJson(File.ReadAllText(path), fromDownload: true);
            return string.CompareOrdinal(saved.Updated, builtIn.Updated) > 0 ? saved : builtIn;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return builtIn; }
    }

    /// <summary>Downloads store.json from the repo and keeps it when it parses and is newer. Returns a line for the log.</summary>
    public static async Task<string> RefreshAsync(string dataDir, string userAgent, CancellationToken ct, string url = MainUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            using var response = await http.GetAsync(url, ct);
            if (response.StatusCode != HttpStatusCode.OK) return $"The online store answered HTTP {(int)response.StatusCode}; the built-in one is used.";
            var json = await response.Content.ReadAsStringAsync(ct);
            var store = FromJson(json, fromDownload: true);
            if (string.CompareOrdinal(store.Updated, LoadBuiltIn().Updated) <= 0) return "The online store isn't newer than the built-in one.";
            var path = SavedPath(dataDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path + ".tmp", json, ct);
            File.Move(path + ".tmp", path, overwrite: true);
            return $"A newer store ({store.Updated}) was downloaded; {store.Rejected.Count} entr{(store.Rejected.Count == 1 ? "y" : "ies")} rejected.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or UnauthorizedAccessException)
        {
            return $"The online store couldn't be read ({ex.GetType().Name}); the built-in one is used.";
        }
    }

    // ---------------- rules ----------------

    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9-]{1,40}$");
    private static readonly Regex GitHubRepo = new(@"^https://github\.com/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/?$");
    private static readonly string[] GameRoots = { "{GameFolder}", "{OptiScalerFolder}" };

    /// <summary>
    /// Files never downloaded or copied by a recipe or setup: NVIDIA's own and modified AMD/NVIDIA files are always supplied by the
    /// user. (Copied files signed by NVIDIA are refused at install time too.)
    /// </summary>
    public static readonly string[] NeverDownloaded =
    {
        "nvngx_dlss.dll", "nvngx_dlssd.dll", "nvngx_dlssnr.dll", "nvngx.dll", "amdxcffx64.dll", "amdxcffx.dll", "dlssg_for_sm86.dll"
    };

    public static bool IsNeverDownloaded(string fileName) =>
        NeverDownloaded.Contains(Path.GetFileName(fileName), StringComparer.OrdinalIgnoreCase)
        || Path.GetFileName(fileName).StartsWith("dlssg_for_sm86", StringComparison.OrdinalIgnoreCase);

    /// <summary>Everything wrong with an entry (empty when it's fine).</summary>
    public static List<string> Problems(StoreEntry e)
    {
        var p = new List<string>();
        if (!IdPattern.IsMatch(e.Id ?? "")) p.Add("the id must be lower-case letters, digits and dashes");
        if (string.IsNullOrWhiteSpace(e.Name)) p.Add("it has no name");
        if (string.IsNullOrWhiteSpace(e.Description) || e.Description.Contains('\n')) p.Add("the description must be one line");
        if (!StoreTypes.All.Contains(e.Type)) p.Add($"unknown type \"{e.Type}\"");
        if (!StoreTypes.Statuses.Contains(e.Status)) p.Add($"unknown status \"{e.Status}\"");
        if (!Uri.TryCreate(e.Repo, UriKind.Absolute, out var repoUri) || repoUri.Scheme != Uri.UriSchemeHttps) p.Add("the project link must be https");
        if (string.IsNullOrWhiteSpace(e.License)) p.Add("the licence is missing (write \"not stated\")");
        foreach (var api in e.Apis) if (!StoreTypes.Apis.Contains(api)) p.Add($"unknown API \"{api}\"");
        foreach (var gpu in e.Gpus) if (gpu.Vendor is not ("nvidia" or "amd" or "intel")) p.Add($"unknown GPU vendor \"{gpu.Vendor}\"");

        // Only the type's own part may be filled in.
        if (e.Type != StoreTypes.Recipe && e.Recipe is not null) p.Add("only recipe entries have a recipe");
        if (e.Type != StoreTypes.AuthorSetup && e.Setup is not null) p.Add("only author-setup entries have a setup");
        if (e.Type != StoreTypes.UserSupplied && e.UserFile is not null) p.Add("only user-supplied entries have a user file");
        if (e.Type != StoreTypes.Builtin && e.Builtin is not null) p.Add("only builtin entries name a built-in page");

        switch (e.Type)
        {
            case StoreTypes.Builtin:
                if (!StoreTypes.Builtins.Contains(e.Builtin)) p.Add("a builtin entry must name helixsr, dlss5 or testbuilds");
                break;
            case StoreTypes.Recipe:
                if (!GitHubRepo.IsMatch(e.Repo)) p.Add("a recipe downloads only from the author's own GitHub repo (https://github.com/owner/repo)");
                if (e.Recipe is not { Count: > 0 }) p.Add("the recipe has no steps");
                else p.AddRange(RecipeProblems(e));
                break;
            case StoreTypes.AuthorSetup:
                if (e.Status != "Experimental") p.Add("an author-setup entry must be Experimental");
                if (!GitHubRepo.IsMatch(e.Repo)) p.Add("a setup downloads only from the author's own GitHub repo");
                if (e.Setup is null) p.Add("the setup is missing");
                else
                {
                    if (!ValidRegex(e.Setup.Asset)) p.Add("the setup's asset isn't a valid pattern");
                    if (string.IsNullOrWhiteSpace(e.Setup.Tag)) p.Add("the setup has no tag");
                    if (!SafeRelative(e.Setup.Run) || !e.Setup.Run.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) p.Add("the setup must run an .exe inside the download");
                    foreach (var arg in e.Setup.Args) if (!OnlyPlaceholders(arg, "{GameFolder}", "{EntryFolder}")) p.Add($"the argument \"{arg}\" uses an unknown placeholder");
                }
                break;
            case StoreTypes.UserSupplied:
                if (e.UserFile is null) p.Add("the user file is missing");
                else
                {
                    if (string.IsNullOrWhiteSpace(e.UserFile.FileName) || e.UserFile.FileName.IndexOfAny(new[] { '\\', '/' }) >= 0) p.Add("the user file needs a plain file name");
                    if (!UnderRoot(e.UserFile.To, e.Id ?? "", gameOnly: true)) p.Add("the user file must go into the game folder");
                    if (e.UserFile.Link is { } link && (!Uri.TryCreate(link, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)) p.Add("the link must be https");
                }
                break;
        }
        return p;
    }

    private static IEnumerable<string> RecipeProblems(StoreEntry e)
    {
        var downloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copied = new List<string>();
        foreach (var (s, i) in e.Recipe!.Select((s, i) => (s, i + 1)))
        {
            var at = $"step {i} ({s.Type})";
            if (!StoreTypes.Steps.Contains(s.Type)) { yield return $"{at}: not one of {string.Join(", ", StoreTypes.Steps)}"; continue; }
            switch (s.Type)
            {
                case "download":
                    if (string.IsNullOrWhiteSpace(s.Id) || !downloads.Add(s.Id)) yield return $"{at}: needs a new id";
                    if (!ValidRegex(s.Asset)) yield return $"{at}: the asset isn't a valid pattern";
                    if (string.IsNullOrWhiteSpace(s.Tag)) yield return $"{at}: needs a tag (\"latest\" or a tag)";
                    break;
                case "extract":
                    if (s.From is null || !downloads.Contains(s.From)) yield return $"{at}: extracts an unknown download";
                    else extracted.Add(s.From);
                    break;
                case "copy":
                    var parts = s.From?.Split(':', 2);
                    if (parts is not { Length: 2 } || !extracted.Contains(parts[0]) || !SafeRelative(parts[1], allowEmpty: true))
                        yield return $"{at}: copies from \"downloadId:path\" of an extracted download, without \"..\"";
                    else if (IsNeverDownloaded(parts[1])) yield return $"{at}: {Path.GetFileName(parts[1])} is always supplied by the user";
                    if (!UnderRoot(s.To, e.Id, gameOnly: false)) yield return $"{at}: copies only into {{GameFolder}}, {{OptiScalerFolder}} or {{UpshiftData}}\\{e.Id}";
                    else copied.Add(s.To!);
                    foreach (var keep in s.Keep ?? new()) if (!SafeRelative(keep)) yield return $"{at}: \"{keep}\" isn't a path inside the copy";
                    break;
                case "rename":
                    if (s.File is null || !UnderRoot(s.File, e.Id, gameOnly: false) || !copied.Any(c => s.File.Equals(c, StringComparison.OrdinalIgnoreCase)
                            || s.File.StartsWith(c.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                        yield return $"{at}: renames only a file the recipe copied";
                    if (string.IsNullOrWhiteSpace(s.Name) || (s.Name != "{ProxyName}" && (s.Name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || s.Name.Contains("..") || s.Name.Contains('{'))))
                        yield return $"{at}: the new name must be a plain file name or {{ProxyName}}";
                    else if (IsNeverDownloaded(s.Name)) yield return $"{at}: {s.Name} is always supplied by the user";
                    break;
                case "ini-set":
                    if (s.File is null || !UnderRoot(s.File, e.Id, gameOnly: false) || !s.File.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                        yield return $"{at}: edits only an .ini under {{GameFolder}} or {{UpshiftData}}\\{e.Id}";
                    if (!IniName(s.Section) || !IniName(s.Key)) yield return $"{at}: needs a section and key without brackets, '=' or line breaks";
                    if (s.Value is null || s.Value.Contains('\n') || s.Value.Contains('\r') || !OnlyPlaceholders(s.Value, "{GameFolder}", "{UpshiftData}", "{EntryFolder}"))
                        yield return $"{at}: the value must be one line, using only {{GameFolder}}, {{UpshiftData}} or {{EntryFolder}}";
                    break;
                case "requires":
                    if (s.Values is not { Count: > 0 }) yield return $"{at}: lists nothing";
                    else foreach (var v in s.Values) if (!ValidRequirement(v)) yield return $"{at}: unknown requirement \"{v}\"";
                    break;
                case "message":
                    if (string.IsNullOrWhiteSpace(s.Text)) yield return $"{at}: has no text";
                    break;
            }
        }
        if (!copied.Any()) yield return "the recipe copies nothing";
    }

    public static bool ValidRequirement(string v) =>
        v is "OptiScaler from Upshift" or "OptiScaler test build" or "DX12" or "Vulkan" or "DX11"
        || Regex.IsMatch(v, @"^(DX12|Vulkan|DX11)(\|(DX12|Vulkan|DX11))+$")
        || Regex.IsMatch(v, @"^GPU:(nvidia|amd|intel)(:[A-Za-z0-9 .]+(\|[A-Za-z0-9 .]+)*)?$");

    private static bool IniName(string? s) => !string.IsNullOrWhiteSpace(s) && s.IndexOfAny(new[] { '[', ']', '=', '\n', '\r', ';' }) < 0;

    private static bool ValidRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        try { _ = new Regex(pattern); return true; } catch (ArgumentException) { return false; }
    }

    private static bool SafeRelative(string? path, bool allowEmpty = false)
    {
        if (path is null) return false;
        if (path.Length == 0) return allowEmpty;
        return !Path.IsPathRooted(path) && !path.Split('\\', '/').Any(s => s == "..") && path.IndexOfAny(Path.GetInvalidPathChars()) < 0
               && !path.Contains(':') && !path.Contains('{');
    }

    private static bool OnlyPlaceholders(string text, params string[] allowed) =>
        Regex.Matches(text, @"\{[^}]*\}").All(m => allowed.Contains(m.Value));

    /// <summary>A path that starts with an allowed place and stays inside it.</summary>
    public static bool UnderRoot(string? path, string entryId, bool gameOnly)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string rest;
        var root = GameRoots.FirstOrDefault(r => path.Equals(r, StringComparison.Ordinal) || path.StartsWith(r + "\\", StringComparison.Ordinal));
        if (root is not null) rest = path[root.Length..].TrimStart('\\');
        else if (!gameOnly && (path.Equals($"{{UpshiftData}}\\{entryId}", StringComparison.Ordinal) || path.StartsWith($"{{UpshiftData}}\\{entryId}\\", StringComparison.Ordinal)))
            rest = path[$"{{UpshiftData}}\\{entryId}".Length..].TrimStart('\\');
        else return false;
        return SafeRelative(rest, allowEmpty: true);
    }

    // ---------------- works on this PC ----------------

    public static bool FitsGpu(StoreEntry e, GpuInfo? gpu)
    {
        if (e.Gpus.Count == 0) return true;
        if (gpu is null) return false;
        var vendor = gpu.Vendor.ToString().ToLowerInvariant();
        return e.Gpus.Any(g => g.Vendor == vendor && (g.Generations.Count == 0 || g.Generations.Any(x => gpu.Generation.StartsWith(x, StringComparison.OrdinalIgnoreCase))));
    }

    public static bool FitsApi(StoreEntry e, GraphicsApi api) =>
        e.Apis.Count == 0 || e.Apis.Any(a => a switch
        {
            "DX12" => api.HasFlag(GraphicsApi.D3D12),
            "Vulkan" => api.HasFlag(GraphicsApi.Vulkan),
            "DX11" => api.HasFlag(GraphicsApi.D3D11),
            _ => false
        });
}
