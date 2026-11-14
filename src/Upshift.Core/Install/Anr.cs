using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Upshift.Core.Models;

namespace Upshift.Core.Install;

/// <summary>
/// ANR FG (Apex Neural Rendering frame generation, github.com/apex-compute/ANR), an experimental opt-in add-on. Its
/// "addon" package goes on top of the OptiScaler Upshift installed, as OptiScaler's custom frame generation provider. What
/// it needs is taken from that package's own install.ps1 (FG V.B15, 2026-11-12), which Upshift never runs:
/// <list type="bullet">
/// <item>the package's "Apex Neural Rendering FG" folder goes to <c>&lt;OptiScaler's DLL folder&gt;\custom\Apex Neural Rendering FG</c>
/// (OptiDllPath: auto → the exe folder's "OptiScaler" folder when it exists, else the exe folder);</item>
/// <item>OptiScaler.ini gets <c>[FrameGen] FGInput=nvngxfg</c> and <c>FGNvngxReplacement=Apex Neural Rendering FG</c>;</item>
/// <item>an existing nr_fg.ini (the user's ANR settings) is kept when ANR is updated.</item>
/// </list>
/// It needs an OptiScaler with custom frame generation providers: OptiScaler.ini has <c>[FrameGen] FGNvngxReplacement</c>
/// (the v10 test builds; 0.9.4 doesn't). In the game, DLSS frame generation is turned on and ANR takes its place.
/// No licence is stated and releases come several times a day: the user allowed it as an opt-in experiment, verified
/// against GitHub's SHA-256 digest of the release asset.
/// </summary>
public static class Anr
{
    public const string ComponentId = "anr";
    public const string Repo = "apex-compute/ANR";
    public const string FolderName = "Apex Neural Rendering FG";
    public const string ChoiceId = "anr";
    public const string ChoiceLabel = "ANR (beta)";
    public const string InGameHint = "Turn on frame generation in the game's own menu";
    public const string NeedsNewer = "Needs a newer OptiScaler";
    public const string IssuesUrl = "https://github.com/apex-compute/ANR/issues";
    public const string ConfigName = "nr_fg.ini";

    public const string Notice =
        "ANR frame generation is a beta from a third party (Apex Compute). It changes often, sometimes several times a day, " +
        "and may crash or freeze games, especially on AMD cards. Upshift downloads it from github.com/apex-compute/ANR and " +
        "checks each download against GitHub's checksum. Turn it on per game under Frame generation.";

    /// <summary>The add-on's settings: on = ANR as OptiScaler's frame generation (the other frame generation values back to the
    /// file's own, as on a fresh ini); off = all four put back to what they were before Upshift changed them.</summary>
    public static List<IniSetting> Settings(bool on) => on
        ? new() { new("FrameGen", "Enabled", null), new("FrameGen", "FGOutput", null),
                  new("FrameGen", "FGInput", "nvngxfg"), new("FrameGen", "FGNvngxReplacement", FolderName) }
        : new() { new("FrameGen", "Enabled", null), new("FrameGen", "FGOutput", null),
                  new("FrameGen", "FGInput", null), new("FrameGen", "FGNvngxReplacement", null) };

    /// <summary>True when this OptiScaler can load a custom frame generation provider.</summary>
    public static bool Supported(IniFile ini) => ini.Has("FrameGen", "FGNvngxReplacement");

    /// <summary>DirectX 12 and Vulkan games only.</summary>
    public static bool ApiFits(GraphicsApi api) => api.HasFlag(GraphicsApi.D3D12) || api.HasFlag(GraphicsApi.Vulkan);

    /// <summary>True when OptiScaler.ini says ANR is the frame generation provider.</summary>
    public static bool Selected(IniFile ini) =>
        string.Equals(ini.Get("FrameGen", "FGNvngxReplacement")?.Trim(), FolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where ANR goes, relative to the exe folder, as the add-on's install.ps1 works it out.</summary>
    public static string FolderRelative(string targetDir, IniFile ini)
    {
        // [Libraries] OptiDllPath in the v10 builds; install.ps1 takes the first OptiDllPath line in any section.
        var path = ini.Entries().FirstOrDefault(e => e.Key.Equals("OptiDllPath", StringComparison.OrdinalIgnoreCase)).Value?.Trim();
        string main;
        if (string.IsNullOrEmpty(path) || path.Equals("auto", StringComparison.OrdinalIgnoreCase))
            main = Directory.Exists(Path.Combine(targetDir, "OptiScaler")) ? "OptiScaler" : "";
        else if (Path.IsPathRooted(path)) throw new InvalidOperationException("OptiScaler.ini points OptiDllPath outside the game folder, so ANR wasn't added.");
        else main = path.TrimStart('.', '\\', '/');
        return Path.Combine(main, "custom", FolderName);
    }

    /// <summary>ANR's own settings file (kept on updates, may be edited by the user, so it never counts as damaged).</summary>
    public static bool IsConfig(string relative) =>
        Path.GetFileName(relative).Equals(ConfigName, StringComparison.OrdinalIgnoreCase)
        && relative.Contains(FolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The add-on package's provider folder inside an unpacked download.</summary>
    public static string? PackageFolder(string downloadFolder)
    {
        var direct = Path.Combine(downloadFolder, FolderName);
        if (Directory.Exists(direct)) return direct;
        return Directory.Exists(downloadFolder)
            ? Directory.EnumerateDirectories(downloadFolder, FolderName, SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    /// <summary>Release tags are "FG-V.B15": the build number, for ordering (0 when it can't be read).</summary>
    public static int BuildNumber(string? tag) =>
        tag is not null && Regex.Match(tag, @"B(\d+)\s*$") is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;

    /// <summary>"V.B15" from "FG-V.B15".</summary>
    public static string ShortVersion(string? tag) => tag is null ? "" : Regex.Replace(tag, @"^FG-", "");

    /// <summary>
    /// The first change listed in a release's notes, for "ANR updated to V.B15: …": the first bullet under "Changes" (or
    /// the first text line), without Markdown links or code marks, cut at the first sentence end or ~160 characters.
    /// </summary>
    public static string NotesLine(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "";
        var lines = notes.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
        var start = lines.FindIndex(l => l.StartsWith("#") && l.Contains("Change", StringComparison.OrdinalIgnoreCase));
        var line = lines.Skip(start + 1).FirstOrDefault(l => l.StartsWith("- ") || l.StartsWith("* "))
                   ?? lines.FirstOrDefault(l => l.Length > 0 && !l.StartsWith("#")) ?? "";
        line = line.TrimStart('-', '*', ' ');
        line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1").Replace("`", "");
        line = Regex.Replace(line, @"\s*\(#\d+\)", "");
        var dot = line.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 20) line = line[..(dot + 1)];
        return line.Length > 160 ? line[..157].TrimEnd() + "…" : line;
    }

    /// <summary>ANR's newest log (nr_fg-*.log) for a game, or null.</summary>
    public static string? NewestLog(string targetDir, InstallManifest? manifest)
    {
        var folder = manifest?.Anr?.Folder is { } f ? Path.Combine(targetDir, f) : Path.Combine(OptiScalerInstaller.OptiScalerFolder(targetDir), "custom", FolderName);
        if (folder is null || !Directory.Exists(folder)) return null;
        try
        {
            return Directory.EnumerateFiles(folder, "nr_fg-*.log", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>ANR in a game: which release, from when, where, and the files Upshift copied (also in the manifest's Added list).</summary>
public sealed class AnrRecord
{
    public string Version { get; set; } = "";
    public DateTimeOffset? Released { get; set; }
    public string Folder { get; set; } = "";
    public DateTime AddedUtc { get; set; }
    public List<string> Files { get; set; } = new();
    /// <summary>The folders Upshift made for ANR (custom, and ANR's own), removed again with it.</summary>
    public List<string> Folders { get; set; } = new();
}

public static partial class OptiScalerInstaller
{
    /// <summary>
    /// Adds ANR to a game with OptiScaler from this app, or replaces the ANR there with another release (an update or going
    /// back). Every file is recorded in the manifest, the two OptiScaler.ini values are recorded like any other change, a
    /// user-edited nr_fg.ini is kept on a version change, and anything that fails puts the folder back as it was.
    /// </summary>
    public static InstallResult AddAnr(InstallPlan plan, InstallLog log)
    {
        var target = plan.TargetDir;
        log.Write($"ANR {plan.GameName} | add {plan.Version} | in {target}");
        if (MoveLegacyState(target, log) is { } moveProblem) return Refuse(log, moveProblem);
        var manifest = ReadManifest(target);
        if (manifest is null or { Removed: true }) return Refuse(log, "OptiScaler isn't installed here by this app, so ANR wasn't added.");
        var iniPath = Path.Combine(target, IniName);
        if (!File.Exists(iniPath)) return Refuse(log, "OptiScaler.ini is missing from the game folder.");
        var ini = IniFile.Load(iniPath);
        if (!Anr.Supported(ini)) return Refuse(log, $"{Anr.NeedsNewer}: this OptiScaler can't load ANR (no FGNvngxReplacement setting).");
        if (plan.SourceDir is not { } source || !Directory.Exists(source) || !File.Exists(Path.Combine(source, "dlssg", "nvngx_dlssg.dll")))
            return Refuse(log, "The downloaded ANR package is incomplete.");
        if (RunningFrom(target).FirstOrDefault() is { } running) return Refuse(log, $"Close the game first ({running} is running); nothing was changed.");

        string folderRelative;
        try { folderRelative = manifest.Anr?.Folder ?? Anr.FolderRelative(target, ini); }
        catch (InvalidOperationException ex) { return Refuse(log, ex.Message); }
        var dest = Path.Combine(target, folderRelative);
        if (manifest.Anr is null && Directory.Exists(dest))
            return Refuse(log, $"{folderRelative} is already there and isn't Upshift's, so ANR wasn't added. Remove it first.");
        var old = manifest.Anr;

        // The user's ANR settings, kept across versions when they were changed.
        string? keptConfig = null;
        if (old is not null)
        {
            var configRel = old.Files.FirstOrDefault(Anr.IsConfig);
            var entry = configRel is null ? null : manifest.Added.FirstOrDefault(f => f.Path.Equals(configRel, StringComparison.OrdinalIgnoreCase));
            if (configRel is not null && File.Exists(Path.Combine(target, configRel)) && entry is not null && Sha256(Path.Combine(target, configRel)) != entry.Sha256)
                keptConfig = File.ReadAllText(Path.Combine(target, configRel));
        }

        var iniBefore = File.ReadAllBytes(iniPath);
        var manifestBefore = JsonSerializer.Serialize(manifest, Json);
        var aside = Path.Combine(target, StateFolder, "anr-previous" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var madeFolders = new List<string>();
        try
        {
            // 1. The ANR that's there now (a version change) moves aside whole, logs included, so a failure can put it back.
            if (old is not null && Directory.Exists(dest))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
                Directory.Move(dest, aside);
                manifest.Added.RemoveAll(f => old.Files.Contains(f.Path, StringComparer.OrdinalIgnoreCase));
            }

            // 2. Folders: the ones Upshift makes are recorded, so they go again with ANR.
            var folders = old?.Folders.ToList() ?? new List<string>();
            var made = new List<string>();
            for (var dir = dest; !Directory.Exists(dir) && !string.Equals(dir, target, StringComparison.OrdinalIgnoreCase); dir = Path.GetDirectoryName(dir)!)
                made.Insert(0, dir);
            foreach (var dir in made)
            {
                Directory.CreateDirectory(dir);
                madeFolders.Add(dir);
                var relative = Path.GetRelativePath(target, dir);
                if (!folders.Contains(relative, StringComparer.OrdinalIgnoreCase)) folders.Add(relative);
                if (!manifest.CreatedFolders.Contains(relative, StringComparer.OrdinalIgnoreCase)) manifest.CreatedFolders.Add(relative);
            }
            Directory.CreateDirectory(dest);

            // 3. The package's files, each recorded.
            var files = new List<string>();
            foreach (var from in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var inside = Path.GetRelativePath(source, from);
                var relative = Path.Combine(folderRelative, inside);
                var to = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: false);
                if (Sha256(to) != Sha256(from)) throw new IOException($"{inside} didn't copy correctly.");
                manifest.Added.Add(Stamp(new ManifestFile { Path = relative }, to, Sha256(to)));
                files.Add(relative);
            }
            if (keptConfig is not null && files.FirstOrDefault(Anr.IsConfig) is { } configPath)
            {
                File.WriteAllText(Path.Combine(target, configPath), keptConfig);
                log.Write("  kept your nr_fg.ini");
            }
            // ANR's logs from the version before come along (not recorded: written by ANR, removed with its folder).
            if (Directory.Exists(aside))
                foreach (var logFile in Directory.EnumerateFiles(aside, "nr_fg-*.log", SearchOption.AllDirectories))
                {
                    var to = Path.Combine(dest, Path.GetRelativePath(aside, logFile));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    if (!File.Exists(to)) File.Copy(logFile, to);
                }

            // 4. OptiScaler.ini, recorded like every other change (so turning ANR off or uninstalling puts it back).
            var ours = manifest.Added.FirstOrDefault(f => IsIni(f.Path)) is { } iniEntry && Sha256(iniPath) == iniEntry.Sha256;
            ApplyIni(target, manifest, Anr.Settings(true), log, ours);

            manifest.Anr = new AnrRecord
            {
                Version = plan.Version ?? "", Released = plan.Released, Folder = folderRelative, AddedUtc = DateTime.UtcNow, Files = files, Folders = folders
            };
            WriteManifest(target, manifest);
            if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
            log.Write($"  ANR {plan.Version}: {files.Count} file(s) in {folderRelative}" + (old is null ? "" : $" (was {old.Version})"));
            return new InstallResult { Success = true, Manifest = manifest, Message = old is null ? $"ANR {Anr.ShortVersion(plan.Version)} added." : $"ANR is now {Anr.ShortVersion(plan.Version)}." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"  FAILED: {ex.Message} - putting things back");
            void Try(Action step) { try { step(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Write($"  could not fully put back: {e.Message}"); } }
            Try(() => { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); });
            Try(() => { if (Directory.Exists(aside)) Directory.Move(aside, dest); });
            foreach (var dir in Enumerable.Reverse(madeFolders)) Try(() => TryRemoveEmptyFolder(dir));
            Try(() => File.WriteAllBytes(iniPath, iniBefore));
            Try(() => File.WriteAllText(Path.Combine(target, StateFolder, ManifestName), manifestBefore));
            return new InstallResult { Message = $"ANR couldn't be added: {ex.Message}" };
        }
    }

    /// <summary>
    /// Takes ANR out again: its files, whatever it wrote into its folder (logs), the folders made for it, and the OptiScaler.ini
    /// values put back, so the game is exactly plain OptiScaler again.
    /// </summary>
    public static InstallResult RemoveAnr(InstallPlan plan, InstallLog log)
    {
        var target = plan.TargetDir;
        log.Write($"ANR {plan.GameName} | remove | in {target}");
        if (MoveLegacyState(target, log) is { } moveProblem) return Refuse(log, moveProblem);
        var manifest = ReadManifest(target);
        if (manifest is null or { Removed: true } || manifest.Anr is not { } anr) return Refuse(log, "Upshift didn't add ANR here, so nothing was removed.");
        if (RunningFrom(target).FirstOrDefault() is { } running) return Refuse(log, $"Close the game first ({running} is running); nothing was changed.");

        var iniPath = Path.Combine(target, IniName);
        var iniBefore = File.Exists(iniPath) ? File.ReadAllBytes(iniPath) : null;
        var manifestBefore = JsonSerializer.Serialize(manifest, Json);
        var dest = Path.Combine(target, anr.Folder);
        var aside = Path.Combine(target, StateFolder, "anr-removed" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        try
        {
            if (Directory.Exists(dest))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
                Directory.Move(dest, aside);
            }
            manifest.Added.RemoveAll(f => anr.Files.Contains(f.Path, StringComparer.OrdinalIgnoreCase));
            if (iniBefore is not null)
            {
                var ours = manifest.Added.FirstOrDefault(f => IsIni(f.Path)) is { } iniEntry && Sha256(iniPath) == iniEntry.Sha256;
                ApplyIni(target, manifest, Anr.Settings(false), log, ours);
            }
            foreach (var folder in anr.Folders.OrderByDescending(f => f.Length))
            {
                var dir = Path.Combine(target, folder);
                if (Directory.Exists(dir)) TryRemoveEmptyFolder(dir);
                if (!Directory.Exists(dir)) manifest.CreatedFolders.RemoveAll(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
            }
            manifest.Anr = null;
            WriteManifest(target, manifest);
            if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
            log.Write($"  ANR {anr.Version} removed ({anr.Files.Count} file(s) and {anr.Folder})");
            return new InstallResult { Success = true, Manifest = manifest, Message = "ANR removed. The game is back on plain OptiScaler." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"  FAILED: {ex.Message} - putting things back");
            void Try(Action step) { try { step(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Write($"  could not fully put back: {e.Message}"); } }
            Try(() => { if (Directory.Exists(aside) && !Directory.Exists(dest)) { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); Directory.Move(aside, dest); } });
            Try(() => { if (iniBefore is not null) File.WriteAllBytes(iniPath, iniBefore); });
            Try(() => File.WriteAllText(Path.Combine(target, StateFolder, ManifestName), manifestBefore));
            return new InstallResult { Message = $"ANR couldn't be removed: {ex.Message}" };
        }
    }
}
