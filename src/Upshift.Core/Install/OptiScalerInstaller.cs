using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Upshift.Core.Detection;

namespace Upshift.Core.Install;

public enum InstallOperation { Install, Uninstall, Configure, Update, UndoUpdate, Repair, UpdateUpscalerFiles, RestoreUpscalerFiles, SwitchBuild, SwitchBack }

/// <summary>Everything needed to install or uninstall, so an elevated copy of the app can carry it out from a file.</summary>
public sealed class InstallPlan
{
    public InstallOperation Operation { get; set; }
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    /// <summary>The main exe's folder: where OptiScaler goes.</summary>
    public string TargetDir { get; set; } = "";
    /// <summary>The unpacked OptiScaler release (install, update and repair).</summary>
    public string? SourceDir { get; set; }

    /// <summary>Update only: the unpacked release that is installed now, whose OptiScaler.ini holds the old defaults.</summary>
    public string? OldSourceDir { get; set; }

    /// <summary>A second unpacked download copied in with SourceDir (AMD-NR's danielblnc runtime).</summary>
    public string? ExtraSourceDir { get; set; }
    public string ComponentId { get; set; } = "optiscaler";
    /// <summary>The component's display name, e.g. "OptiScaler DLSSNR" (for messages).</summary>
    public string? ComponentName { get; set; }
    public string? Version { get; set; }
    /// <summary>The name OptiScaler.dll is installed as, e.g. dxgi.dll (install only).</summary>
    public string? ProxyName { get; set; }

    /// <summary>OptiScaler.ini values to write (install: after copying; configure: on the installed copy).</summary>
    public List<IniSetting> IniSettings { get; set; } = new();

    /// <summary>
    /// The game's folder in %LocalAppData%\Upshift\saved-settings. Uninstall saves OptiScaler.ini there before removing
    /// it; Install backs up a leftover OptiScaler.ini there before removing it.
    /// </summary>
    public string? SettingsFolder { get; set; }

    /// <summary>Install only: remove a leftover OptiScaler.ini from an earlier install (after backing it up) instead of stopping.</summary>
    public bool RemoveLeftovers { get; set; }

    /// <summary>Install only: a saved OptiScaler.ini whose values go into the fresh one ("Restore my previous OptiScaler settings").</summary>
    public string? RestoreSettingsFrom { get; set; }

    /// <summary>Install only: restore from the leftover OptiScaler.ini this install removes (it's newer than any saved copy).</summary>
    public bool RestoreFromLeftover { get; set; }

    /// <summary>Install only: the OptiScaler.ini of the version the saved settings came from, so its plain defaults aren't carried over.</summary>
    public string? RestoreDefaultsFrom { get; set; }

    /// <summary>Configure only: a user-supplied file to copy next to the game exe (from the app's user-files folder).</summary>
    public string? AddFileFrom { get; set; }
    /// <summary>Configure only: the name AddFileFrom is copied in as, e.g. amdxcffx64.dll.</summary>
    public string? AddFileAs { get; set; }
    /// <summary>Configure only: a file this app added earlier to take out again (the game's own copy comes back if it had one).</summary>
    public string? RemoveFile { get; set; }

    /// <summary>
    /// Upscaler file updates and restores: the files, relative to TargetDir, which for these operations is the game's
    /// install folder (not the exe folder), since DLLs can sit anywhere in the game.
    /// </summary>
    public List<UpscalerFileJob> UpscalerFiles { get; set; } = new();
}

/// <summary>An OptiScaler.ini value this app changed, and what it was before, so it can be put back.</summary>
public sealed class IniChange
{
    public string Section { get; set; } = "";
    public string Key { get; set; } = "";
    public string? Original { get; set; }
    public string? Current { get; set; }

    /// <summary>When this app last set the value (not recorded by versions before 2026-09-28).</summary>
    public DateTime? ChangedUtc { get; set; }
}

public sealed class InstallResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    /// <summary>Files that were changed after we installed them, so they were left in place instead of removed or restored.</summary>
    public List<string> KeptChanged { get; set; } = new();
    /// <summary>Files OptiScaler created while running (not ours to delete), e.g. OptiScaler.log.</summary>
    public List<string> LeftBehind { get; set; } = new();
    public InstallManifest? Manifest { get; set; }
}

/// <summary>What an install would do, for the confirmation dialog.</summary>
public sealed class InstallPreview
{
    public string ProxyName { get; set; } = "dxgi.dll";
    /// <summary>Game files that would be overwritten, so they get backed up first.</summary>
    public List<string> WillBackUp { get; set; } = new();
    public List<string> WillAdd { get; set; } = new();
    /// <summary>Reasons the install can't go ahead (e.g. another OptiScaler already there). Empty when it can.</summary>
    public List<string> Blockers { get; set; } = new();

    /// <summary>
    /// Files an earlier OptiScaler left behind (OptiScaler.ini) that don't have to stop the install: with "Remove it and
    /// continue" they're backed up to the saved settings and removed first.
    /// </summary>
    public List<string> Leftovers { get; set; } = new();
}

public sealed class ManifestFile
{
    /// <summary>Path relative to the target folder.</summary>
    public string Path { get; set; } = "";
    /// <summary>SHA-256 of the file as we installed it.</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>For replaced files: the backup copy, relative to the target folder.</summary>
    public string? Backup { get; set; }
    /// <summary>For replaced files: SHA-256 of the game's original.</summary>
    public string? OriginalSha256 { get; set; }

    /// <summary>Size and time of the file as we left it, so a scan can skip hashing files that are clearly unchanged.</summary>
    public long? Size { get; set; }
    public DateTime? LastWriteUtc { get; set; }
}

/// <summary>What the last update did, and where the copy for "Undo last update" is.</summary>
public sealed class UpdateRecord
{
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// <summary>Relative to the target folder. Holds the previous files, OptiScaler.ini and manifest. Null once used or replaced.</summary>
    public string? UndoFolder { get; set; }
    /// <summary>OptiScaler.ini values carried over from before the update (Original = the new version's default).</summary>
    public List<IniChange> KeptIniValues { get; set; } = new();
    /// <summary>Settings the new version added, left at their defaults ("[Section] Key").</summary>
    public List<string> NewIniOptions { get; set; } = new();
    /// <summary>Settings the new version no longer has, so they were dropped.</summary>
    public List<string> DroppedIniOptions { get; set; } = new();
    public List<string> AddedFiles { get; set; } = new();
    public List<string> RemovedFiles { get; set; } = new();
}

/// <summary>What the last repair put back.</summary>
public sealed class RepairRecord
{
    public DateTime RepairedUtc { get; set; }
    public List<string> Fixed { get; set; } = new();
    /// <summary>Relative to the target folder: the changed files that were replaced, kept just in case.</summary>
    public string? SafetyFolder { get; set; }
}

/// <summary>.upshift\manifest.json (.pcvgs\ in older installs): the record uninstall works from.</summary>
public sealed class InstallManifest
{
    public int Schema { get; set; } = 1;
    public string ComponentId { get; set; } = "";
    public string? Version { get; set; }
    public string ProxyName { get; set; } = "";
    public string GameId { get; set; } = "";
    public DateTime InstalledUtc { get; set; }

    /// <summary>The version first installed (later updates change Version). Not recorded before 2026-09-28.</summary>
    public string? FirstVersion { get; set; }

    /// <summary>When Uninstall left changed files behind and marked this record Removed.</summary>
    public DateTime? RemovedUtc { get; set; }
    public string BackupFolder { get; set; } = "";
    public List<ManifestFile> Added { get; set; } = new();
    public List<ManifestFile> Replaced { get; set; } = new();
    /// <summary>Folders we created, deepest last; removed on uninstall when empty.</summary>
    public List<string> CreatedFolders { get; set; } = new();

    /// <summary>
    /// Set by uninstall when some files had changed and were left in place. OptiScaler is no longer installed;
    /// the manifest only remembers which leftovers came from us.
    /// </summary>
    public bool Removed { get; set; }

    /// <summary>Every OptiScaler.ini value this app changed after copying it, with the value it replaced.</summary>
    public List<IniChange> IniChanges { get; set; } = new();

    public UpdateRecord? LastUpdate { get; set; }
    public RepairRecord? LastRepair { get; set; }

    /// <summary>Set while a DLSS 5 build replaces the regular OptiScaler: where "Switch back" returns to.</summary>
    /// <remarks>Left out of the file when empty, so manifests written before DLSS 5 support stay byte-identical.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SwitchRecord? Switch { get; set; }
}

/// <summary>A switch to another build of OptiScaler (the DLSS 5 forks), and the exact state it came from.</summary>
public sealed class SwitchRecord
{
    public string FromComponentId { get; set; } = "";
    public string? FromVersion { get; set; }
    public DateTime SwitchedUtc { get; set; }
    /// <summary>Relative to the target folder: the files, OptiScaler.ini and manifest from before the switch.</summary>
    public string ReturnFolder { get; set; } = "";
}

/// <summary>
/// Installs the official OptiScaler release the way its own setup_windows.bat does: every file from the release goes
/// into the game's exe folder (keeping D3D12_Optiscaler\ and Licenses\), OptiScaler.dll is renamed to the loading name,
/// and the setup's own readme and scripts are left out (the official setup deletes them when it succeeds).
/// Unlike the official setup, every overwritten game file is backed up first, everything is recorded in
/// .upshift\manifest.json, and a failure part-way through puts the folder back exactly as it was.
/// </summary>
public static partial class OptiScalerInstaller
{
    public const string StateFolder = ".upshift";

    /// <summary>The folder name installs used before the app was renamed. Still read; moved to StateFolder on the next change.</summary>
    public const string LegacyStateFolder = ".pcvgs";
    public const string ManifestName = "manifest.json";

    /// <summary>Names OptiScaler can load as, in the order the official setup offers them.</summary>
    public static readonly string[] ProxyNames =
        { "dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll" };

    /// <summary>Tried in this order when the user hasn't picked a name.</summary>
    private static readonly string[] AutoProxyNames = { "dxgi.dll", "winmm.dll", "version.dll" };

    /// <summary>Files in the release that the official setup removes, so they never end up in the game folder.</summary>
    private static readonly HashSet<string> SetupOnlyFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "!! README_EXTRACT ALL FILES TO GAME FOLDER !!.txt", "setup_windows.bat", "setup_linux.sh", ".upshift-complete.json", ".pcvgs-complete.json",
        // The DLSS 5 forks' notes and checksums (their Licenses folders still go in).
        "!! EXTRACT ALL FILES TO GAME FOLDER !!", "READ ME - DLSS Neural Rendering.txt", "CHANGELOG.md", "SHA256SUMS.txt"
    };

    /// <summary>Readmes in any language (README.md, README.zh-CN.md…) stay out of the game folder too.</summary>
    private static bool IsSetupOnly(string relative) =>
        SetupOnlyFiles.Contains(relative)
        || (!relative.Contains('\\') && relative.StartsWith("README", StringComparison.OrdinalIgnoreCase) && relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

    /// <summary>Leftovers of an OptiScaler not installed by us; the official setup warns about the same files.</summary>
    private static readonly string[] ForeignOptiScalerFiles =
        { "nvapi64.dll", "nvngx.dll", "OptiScaler.asi", "Remove OptiScaler.bat", "Remove_OptiScaler.bat" };

    /// <summary>A leftover settings file from an earlier OptiScaler: backed up and removed on request, never a blocker.</summary>
    private const string LeftoverIni = "OptiScaler.ini";

    /// <summary>Files OptiScaler and its bundled tools write next to the game while it runs; Uninstall removes them.</summary>
    private static readonly string[] RuntimeFiles = { "OptiScaler.log", "fakenvapi.log", "dlssg_to_fsr3.log" };

    /// <summary>Folders OptiScaler's releases bring; Uninstall removes them when nothing else is left in them.</summary>
    private static readonly string[] OptiScalerFolders = { "D3D12_Optiscaler", "Licenses", "OptiScaler" };

    /// <summary>True for logs OptiScaler and its tools write while running (OptiScaler.log, fakenvapi.log, OptiScaler.log.1…).</summary>
    private static bool IsRuntimeFile(string name) =>
        RuntimeFiles.Contains(name, StringComparer.OrdinalIgnoreCase)
        || (name.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase) && name.Contains(".log", StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static InstallManifest? ReadManifest(string targetDir)
    {
        var path = Path.Combine(targetDir, StateFolder, ManifestName);
        if (!File.Exists(path)) path = Path.Combine(targetDir, LegacyStateFolder, ManifestName);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static string SerializePlan(InstallPlan plan) => JsonSerializer.Serialize(plan, Json);
    public static InstallPlan? DeserializePlan(string json) => JsonSerializer.Deserialize<InstallPlan>(json, Json);
    public static string SerializeResult(InstallResult result) => JsonSerializer.Serialize(result, Json);
    public static InstallResult? DeserializeResult(string json) => JsonSerializer.Deserialize<InstallResult>(json, Json);

    /// <summary>True when this process can create and delete files in the folder (false under Program Files without admin).</summary>
    public static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".upshift-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>The loading name to use when the user hasn't chosen: dxgi.dll, or winmm/version.dll if dxgi.dll is taken.</summary>
    public static string? AutoProxyName(string targetDir) =>
        AutoProxyNames.FirstOrDefault(n => !File.Exists(Path.Combine(targetDir, n)));

    public static InstallPreview Preview(string sourceDir, string targetDir, string? proxyName)
    {
        var preview = new InstallPreview();

        if (ReadManifest(targetDir) is { Removed: false } existing)
            preview.Blockers.Add($"OptiScaler {existing.Version} is already installed here as {existing.ProxyName}.");

        foreach (var name in ForeignOptiScalerFiles.Where(n => File.Exists(Path.Combine(targetDir, n))))
            preview.Blockers.Add($"{name} is left over from another OptiScaler install. Remove it first.");
        // A settings file from an earlier install (ours or another) doesn't stop anything: it can be backed up and removed.
        if (ReadManifest(targetDir) is not { Removed: false } && File.Exists(Path.Combine(targetDir, LeftoverIni)))
            preview.Leftovers.Add(LeftoverIni);
        foreach (var name in ProxyNames.Where(n => IsOptiScalerDll(Path.Combine(targetDir, n))))
            preview.Blockers.Add($"{name} is an OptiScaler copy that this app didn't install. Remove it first.");

        var proxy = proxyName ?? AutoProxyName(targetDir);
        if (proxy is null)
        {
            preview.Blockers.Add("dxgi.dll, winmm.dll and version.dll are all taken by the game. Choose another name under Advanced.");
            proxy = "dxgi.dll";
        }
        preview.ProxyName = proxy;

        if (!Directory.Exists(Path.Combine(sourceDir)) || !File.Exists(Path.Combine(sourceDir, "OptiScaler.dll")))
        {
            preview.Blockers.Add("The downloaded OptiScaler is missing OptiScaler.dll.");
            return preview;
        }

        foreach (var (_, relative) in FilesToCopy(sourceDir, proxy))
        {
            if (File.Exists(Path.Combine(targetDir, relative))) preview.WillBackUp.Add(relative);
            else preview.WillAdd.Add(relative);
        }
        return preview;
    }

    /// <summary>Copies everything or nothing. Any failure rolls the folder back to how it was.</summary>
    public static InstallResult Install(InstallPlan plan, InstallLog log)
    {
        var target = plan.TargetDir;
        var source = plan.SourceDir ?? "";
        log.Write($"INSTALL {plan.GameName} | {plan.ComponentId} {plan.Version} as {plan.ProxyName} | into {target}");
        if (MoveLegacyState(target, log) is { } moveProblem) return Refuse(log, moveProblem);

        var preview = Preview(source, target, plan.ProxyName);
        if (preview.Blockers.Count > 0)
        {
            log.Write("  refused: " + string.Join(" / ", preview.Blockers));
            return new InstallResult { Message = string.Join(" ", preview.Blockers) };
        }
        if (preview.Leftovers.Count > 0 && (!plan.RemoveLeftovers || plan.SettingsFolder is null))
            return Refuse(log, "OptiScaler.ini from an earlier OptiScaler install is still in the game folder. Choose \"Remove it and continue\" to back it up and remove it.");

        // A leftover OptiScaler.ini is backed up to the saved settings first, then removed, so the fresh one goes in.
        string? leftoverCopy = null;
        foreach (var leftover in preview.Leftovers)
        {
            var path = Path.Combine(target, leftover);
            try
            {
                leftoverCopy = SavedSettings.Save(plan.SettingsFolder!, path, plan.GameName, null, null, wasLeftover: true);
                File.Delete(path);
                log.Write($"  leftover {leftover} backed up to {leftoverCopy} and removed");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refuse(log, $"The leftover {leftover} couldn't be backed up, so nothing was changed: {ex.Message}");
            }
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var manifest = new InstallManifest
        {
            ComponentId = plan.ComponentId,
            Version = plan.Version,
            FirstVersion = plan.Version,
            ProxyName = preview.ProxyName,
            GameId = plan.GameId,
            InstalledUtc = DateTime.UtcNow,
            BackupFolder = Path.Combine(StateFolder, "backup" + stamp)
        };
        var stateDir = Path.Combine(target, StateFolder);
        var stateDirExisted = Directory.Exists(stateDir);

        try
        {
            var files = FilesToCopy(source, preview.ProxyName).ToList();

            // 1. Back up every game file that will be overwritten, and check each copy.
            foreach (var (_, relative) in files.Where(f => File.Exists(Path.Combine(target, f.Relative))))
            {
                var original = Path.Combine(target, relative);
                var backupRelative = Path.Combine(manifest.BackupFolder, relative);
                var backup = Path.Combine(target, backupRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(original, backup, overwrite: false);
                var originalHash = Sha256(original);
                if (Sha256(backup) != originalHash) throw new IOException($"The backup of {relative} doesn't match the original.");
                manifest.Replaced.Add(new ManifestFile { Path = relative, Backup = backupRelative, OriginalSha256 = originalHash });
                log.Write($"  backed up {relative} -> {backupRelative}");
            }

            // 2. Copy the release in.
            foreach (var (from, relative) in files)
            {
                var to = Path.Combine(target, relative);
                var folder = Path.GetDirectoryName(to)!;
                if (!Directory.Exists(folder))
                {
                    // Record each new folder level so uninstall (and rollback) can remove exactly those.
                    var missing = new Stack<string>();
                    for (var f = folder; !Directory.Exists(f); f = Path.GetDirectoryName(f)!) missing.Push(f);
                    Directory.CreateDirectory(folder);
                    foreach (var created in missing) manifest.CreatedFolders.Add(Path.GetRelativePath(target, created));
                }

                var replaced = manifest.Replaced.FirstOrDefault(r => r.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
                File.Copy(from, to, overwrite: replaced is not null);
                var hash = Sha256(to);
                if (hash != Sha256(from)) throw new IOException($"{relative} didn't copy correctly.");

                if (replaced is not null) Stamp(replaced, to, hash);
                else manifest.Added.Add(Stamp(new ManifestFile { Path = relative }, to, hash));
                log.Write($"  {(replaced is null ? "added" : "replaced")} {relative} ({hash[..12]})");
            }

            // 3. Settings chosen before installing (e.g. the suggested upscaler) go into the fresh OptiScaler.ini, then
            //    the user's previous settings when they asked for them back (those win).
            if (plan.IniSettings.Count > 0) ApplyIni(target, manifest, plan.IniSettings, log, fileWasOurs: true);
            var restoreFrom = plan.RestoreFromLeftover ? leftoverCopy ?? plan.RestoreSettingsFrom : plan.RestoreSettingsFrom;
            var iniPath = Path.Combine(target, LeftoverIni);
            if (restoreFrom is not null && File.Exists(restoreFrom) && File.Exists(iniPath))
            {
                var restore = SavedSettings.ValuesToRestore(restoreFrom, iniPath, plan.RestoreFromLeftover ? null : plan.RestoreDefaultsFrom);
                log.Write($"  restoring {restore.Count} previous setting(s) from {restoreFrom}");
                if (restore.Count > 0) ApplyIni(target, manifest, restore, log, fileWasOurs: true);
            }

            // 4. The manifest goes last: without it, the install didn't happen.
            Directory.CreateDirectory(stateDir);
            var manifestPath = Path.Combine(stateDir, ManifestName);
            File.WriteAllText(manifestPath + ".tmp", JsonSerializer.Serialize(manifest, Json));
            File.Move(manifestPath + ".tmp", manifestPath, overwrite: true);

            log.Write($"  done: {manifest.Added.Count} added, {manifest.Replaced.Count} replaced (backed up)");
            return new InstallResult
            {
                Success = true,
                Manifest = manifest,
                Message = $"OptiScaler {plan.Version} installed as {manifest.ProxyName}."
                          + (restoreFrom is not null ? " Your previous OptiScaler settings were restored." : "")
                          + (leftoverCopy is not null ? " The leftover OptiScaler.ini was backed up to Upshift's saved settings." : "")
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"  FAILED: {ex.Message} - rolling back");
            var rollbackProblems = Rollback(target, manifest, stateDirExisted, log);
            // The leftover OptiScaler.ini removed before copying goes back too.
            if (leftoverCopy is not null && !File.Exists(Path.Combine(target, LeftoverIni)))
            {
                try { File.Copy(leftoverCopy, Path.Combine(target, LeftoverIni)); log.Write("  rollback: put the leftover OptiScaler.ini back"); }
                catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException) { rollbackProblems.Add(LeftoverIni); }
            }
            return new InstallResult
            {
                Message = rollbackProblems.Count == 0
                    ? $"Install failed and nothing was changed: {ex.Message}"
                    : $"Install failed: {ex.Message} Some files couldn't be put back: {string.Join(", ", rollbackProblems)}. The backups are in {manifest.BackupFolder}."
            };
        }
    }

    /// <summary>
    /// Removes only files the manifest says we added, and only if they're unchanged; puts back the game's own files
    /// from the backup. Anything changed since the install is left in place and listed.
    /// </summary>
    public static InstallResult Uninstall(InstallPlan plan, InstallLog log)
    {
        var target = plan.TargetDir;
        log.Write($"UNINSTALL {plan.GameName} | from {target}");
        if (MoveLegacyState(target, log) is { } moveProblem) return Refuse(log, moveProblem);

        var manifest = ReadManifest(target);
        if (manifest is null or { Removed: true })
        {
            log.Write("  refused: no manifest");
            return new InstallResult { Message = "This app has no record of installing anything in this folder, so it won't remove anything." };
        }

        var result = new InstallResult { Manifest = manifest };
        var keepBackups = false;

        // The user's OptiScaler.ini (whatever the in-game menu changed) is saved before anything is removed, so a later
        // install can restore it. If it can't be saved, nothing is removed.
        var iniPath = Path.Combine(target, LeftoverIni);
        if (plan.SettingsFolder is not null && File.Exists(iniPath))
        {
            try
            {
                var copy = SavedSettings.Save(plan.SettingsFolder, iniPath, plan.GameName, manifest.ComponentId, manifest.Version, wasLeftover: false);
                log.Write($"  saved OptiScaler.ini to {copy}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refuse(log, $"Your OptiScaler settings couldn't be saved, so nothing was removed: {ex.Message}");
            }
        }

        // Folders the install created, including the ones from before a switch to a DLSS 5 build.
        var createdFolders = manifest.CreatedFolders.ToList();
        if (manifest.Switch is { } sw && ReadSwitchOriginal(target, sw) is { } original)
            createdFolders.AddRange(original.CreatedFolders.Where(f => !createdFolders.Contains(f, StringComparer.OrdinalIgnoreCase)));

        try
        {
            foreach (var file in manifest.Added)
            {
                var path = Path.Combine(target, file.Path);
                if (!File.Exists(path)) { log.Write($"  already gone: {file.Path}"); continue; }
                // OptiScaler.ini goes even when the in-game menu changed it: it was saved above.
                if (IsIni(file.Path) && (plan.SettingsFolder is not null || Sha256(path) == file.Sha256))
                {
                    File.Delete(path);
                    log.Write($"  removed {file.Path}");
                    continue;
                }
                if (Sha256(path) != file.Sha256)
                {
                    result.KeptChanged.Add(file.Path);
                    log.Write($"  KEPT (changed since install): {file.Path}");
                    continue;
                }
                File.Delete(path);
                log.Write($"  removed {file.Path}");
            }

            foreach (var file in manifest.Replaced)
            {
                var path = Path.Combine(target, file.Path);
                var backup = Path.Combine(target, file.Backup!);
                if (File.Exists(path) && Sha256(path) != file.Sha256)
                {
                    // Something (a game update, say) replaced our copy; don't overwrite it, and keep the backup.
                    result.KeptChanged.Add(file.Path);
                    keepBackups = true;
                    log.Write($"  KEPT (changed since install), backup kept: {file.Path}");
                    continue;
                }
                File.Copy(backup, path, overwrite: true);
                if (Sha256(path) != file.OriginalSha256) throw new IOException($"Restoring {file.Path} gave a different file than the original.");
                File.Delete(backup);
                log.Write($"  restored {file.Path}");
            }

            // Logs OptiScaler and its tools wrote next to the game while it ran.
            foreach (var file in Directory.EnumerateFiles(target).Where(f => IsRuntimeFile(Path.GetFileName(f))).ToList())
            {
                try { File.Delete(file); log.Write($"  removed {Path.GetFileName(file)} (written by OptiScaler while running)"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.LeftBehind.Add(Path.GetFileName(file)); }
            }

            // Folders the install made: whatever OptiScaler wrote into them while running goes too (files that changed
            // since the install, and so are kept, stay with their folder).
            foreach (var folder in createdFolders.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(f => f.Length))
            {
                var dir = Path.Combine(target, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList())
                {
                    var relative = Path.GetRelativePath(target, file);
                    if (result.KeptChanged.Contains(relative, StringComparer.OrdinalIgnoreCase)) continue;
                    try { File.Delete(file); log.Write($"  removed {relative} (written by OptiScaler while running)"); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.LeftBehind.Add(relative); }
                }
                RemoveEmptyFolders(dir);
            }
            // OptiScaler's own folder names, when an earlier record didn't list them but they're empty now.
            foreach (var folder in OptiScalerFolders) RemoveEmptyFolders(Path.Combine(target, folder));

            var stateDir = Path.Combine(target, StateFolder);
            if (!keepBackups && result.KeptChanged.Count == 0)
            {
                // Everything in .upshift except the record of upscaler files Upshift updated (when the exe folder is
                // also the install folder, that record and its originals live here too, and outlast OptiScaler).
                foreach (var entry in Directory.EnumerateFileSystemEntries(stateDir))
                {
                    var name = Path.GetFileName(entry);
                    if (name.Equals(UpscalerFiles.RecordName, StringComparison.OrdinalIgnoreCase)
                        || name.Equals(UpscalerFiles.OriginalsFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                    else File.Delete(entry);
                }
                TryRemoveEmptyFolder(stateDir);
                log.Write(Directory.Exists(stateDir) ? $"  removed OptiScaler's records from {StateFolder} (upscaler file records kept)" : $"  removed {StateFolder}");
            }
            else
            {
                // Keep the record of what's still ours, and any backup that wasn't restored.
                manifest.Added = manifest.Added.Where(f => result.KeptChanged.Contains(f.Path)).ToList();
                manifest.Replaced = manifest.Replaced.Where(f => result.KeptChanged.Contains(f.Path)).ToList();
                manifest.Removed = true;
                manifest.RemovedUtc = DateTime.UtcNow;
                TryRemoveEmptyFolder(Path.Combine(target, manifest.BackupFolder));
                File.WriteAllText(Path.Combine(stateDir, ManifestName), JsonSerializer.Serialize(manifest, Json));
                log.Write($"  kept {StateFolder} for {result.KeptChanged.Count} changed file(s)");
            }

            result.Success = true;
            result.Message = (result.KeptChanged.Count == 0
                ? "OptiScaler was removed and the game's own files were put back."
                : $"OptiScaler was removed, but {result.KeptChanged.Count} file(s) changed after install and were left in place: {string.Join(", ", result.KeptChanged)}.")
                + (plan.SettingsFolder is not null ? " Your OptiScaler settings were saved, so a reinstall can restore them." : "");
            log.Write("  done: " + result.Message);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"  FAILED: {ex.Message}");
            result.Message = $"Uninstall stopped part-way: {ex.Message} The record in {StateFolder} was kept, so you can try again.";
            return result;
        }
    }

    /// <summary>
    /// Changes an existing install: OptiScaler.ini values and/or a user-supplied file. Every change is recorded in the
    /// manifest (the ini's hash, the first value we replaced, backups of any game file we overwrite), so Uninstall
    /// still puts the folder back exactly. If anything fails, the ini and files are left as they were.
    /// </summary>
    public static InstallResult Configure(InstallPlan plan, InstallLog log)
    {
        var target = plan.TargetDir;
        log.Write($"CONFIGURE {plan.GameName} | {plan.IniSettings.Count} ini value(s){(plan.AddFileAs is null ? "" : $", add {plan.AddFileAs}")}{(plan.RemoveFile is null ? "" : $", remove {plan.RemoveFile}")}");

        if (MoveLegacyState(target, log) is { } moveProblem) return Refuse(log, moveProblem);
        var manifest = ReadManifest(target);
        if (manifest is null or { Removed: true })
            return Refuse(log, "OptiScaler isn't installed here by this app, so its settings weren't changed.");

        var iniPath = Path.Combine(target, "OptiScaler.ini");
        var iniEntry = manifest.Added.FirstOrDefault(f => f.Path.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase));
        if (plan.IniSettings.Count > 0 && !File.Exists(iniPath))
            return Refuse(log, "OptiScaler.ini is missing from the game folder.");

        // Only keys that exist in OptiScaler's own ini can be written, so a plan can't add arbitrary settings.
        if (plan.IniSettings.Count > 0)
        {
            var current = IniFile.Load(iniPath);
            var unknown = plan.IniSettings.Where(s => !current.Has(s.Section, s.Key) && !IsAddableIniKey(s.Section, s.Key))
                .Select(s => $"[{s.Section}] {s.Key}").ToList();
            if (unknown.Count > 0) return Refuse(log, $"OptiScaler.ini has no setting called {string.Join(", ", unknown)}.");
        }

        var iniBefore = File.Exists(iniPath) ? File.ReadAllBytes(iniPath) : null;
        var manifestBefore = JsonSerializer.Serialize(manifest, Json);
        string? copiedTo = null;
        string? backupMade = null;

        try
        {
            if (plan.IniSettings.Count > 0)
            {
                // If the user edited OptiScaler.ini by hand, it stays theirs: Uninstall will leave it in place.
                var ours = iniEntry is not null && Sha256(iniPath) == iniEntry.Sha256;
                ApplyIni(target, manifest, plan.IniSettings, log, ours);
            }

            if (plan.RemoveFile is { } remove)
            {
                var path = Path.Combine(target, remove);
                var added = manifest.Added.FirstOrDefault(f => f.Path.Equals(remove, StringComparison.OrdinalIgnoreCase));
                var replaced = manifest.Replaced.FirstOrDefault(f => f.Path.Equals(remove, StringComparison.OrdinalIgnoreCase));
                if (added is null && replaced is null) return Refuse(log, $"{remove} wasn't added by this app, so it wasn't removed.");
                if (File.Exists(path) && Sha256(path) != (added ?? replaced)!.Sha256)
                    return Refuse(log, $"{remove} has changed since it was copied in, so it was left in place.");

                if (added is not null)
                {
                    if (File.Exists(path)) File.Delete(path);
                    manifest.Added.Remove(added);
                }
                else
                {
                    var backup = Path.Combine(target, replaced!.Backup!);
                    File.Copy(backup, path, overwrite: true);
                    if (Sha256(path) != replaced.OriginalSha256) throw new IOException($"Restoring {remove} gave a different file than the original.");
                    File.Delete(backup);
                    manifest.Replaced.Remove(replaced);
                }
                log.Write($"  removed {remove}");
            }

            if (plan.AddFileFrom is { } from && plan.AddFileAs is { } name)
            {
                var path = Path.Combine(target, name);
                if (manifest.Added.Any(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase))
                    || manifest.Replaced.Any(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    return Refuse(log, $"{name} is already managed by this app; remove it first.");

                if (File.Exists(path))
                {
                    // The game (or something else) already has this file: back it up so it comes back on uninstall.
                    var backupRelative = Path.Combine(manifest.BackupFolder, name);
                    var backup = Path.Combine(target, backupRelative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(path, backup, overwrite: false);
                    backupMade = backup;
                    var originalHash = Sha256(path);
                    File.Copy(from, path, overwrite: true);
                    copiedTo = path;
                    manifest.Replaced.Add(Stamp(new ManifestFile { Path = name, Backup = backupRelative, OriginalSha256 = originalHash }, path, Sha256(path)));
                    log.Write($"  replaced {name} (the game's copy is backed up in {backupRelative})");
                }
                else
                {
                    File.Copy(from, path, overwrite: false);
                    copiedTo = path;
                    manifest.Added.Add(Stamp(new ManifestFile { Path = name }, path, Sha256(path)));
                    log.Write($"  added {name}");
                }
            }

            WriteManifest(target, manifest);
            log.Write("  done");
            return new InstallResult { Success = true, Manifest = manifest, Message = "OptiScaler settings saved." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"  FAILED: {ex.Message} - putting things back");
            try
            {
                if (iniBefore is not null) File.WriteAllBytes(iniPath, iniBefore);
                if (copiedTo is not null && backupMade is null) File.Delete(copiedTo);
                if (copiedTo is not null && backupMade is not null) { File.Copy(backupMade, copiedTo, overwrite: true); File.Delete(backupMade); }
                File.WriteAllText(Path.Combine(target, StateFolder, ManifestName), manifestBefore);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                log.Write($"  could not fully put back: {rollback.Message}");
            }
            return new InstallResult { Message = $"The settings couldn't be saved: {ex.Message}" };
        }
    }

    /// <summary>
    /// Settings a build ships commented out, which may be added: AMD-NR's runtime choice (";NrBackend=daniel") is only
    /// written once chosen, and the game asks on first launch while it's missing.
    /// </summary>
    private static bool IsAddableIniKey(string section, string key) =>
        section.Equals("DlssNr", StringComparison.OrdinalIgnoreCase) && key.Equals("NrBackend", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes ini values and records each first change with the value it replaced. A null value puts the original back
    /// and forgets the change. When the ini is ours, the manifest's hash follows, so Uninstall still removes it cleanly.
    /// </summary>
    private static void ApplyIni(string target, InstallManifest manifest, IReadOnlyList<IniSetting> settings, InstallLog log, bool fileWasOurs)
    {
        var iniPath = Path.Combine(target, "OptiScaler.ini");
        var ini = IniFile.Load(iniPath);
        foreach (var setting in settings)
        {
            var change = manifest.IniChanges.FirstOrDefault(c =>
                c.Section.Equals(setting.Section, StringComparison.OrdinalIgnoreCase) && c.Key.Equals(setting.Key, StringComparison.OrdinalIgnoreCase));
            var before = ini.Get(setting.Section, setting.Key);

            if (setting.Value is null)
            {
                if (change is null) continue;
                if (change.Original is not null) ini.Set(setting.Section, setting.Key, change.Original);
                manifest.IniChanges.Remove(change);
                log.Write($"  ini [{setting.Section}] {setting.Key}: {before} -> {change.Original} (put back)");
                continue;
            }

            if (string.Equals(before, setting.Value, StringComparison.Ordinal) && change is null) continue;
            if (change is null)
            {
                change = new IniChange { Section = setting.Section, Key = setting.Key, Original = before };
                manifest.IniChanges.Add(change);
            }
            ini.Set(setting.Section, setting.Key, setting.Value);
            change.Current = setting.Value;
            change.ChangedUtc = DateTime.UtcNow;
            if (string.Equals(change.Original, change.Current, StringComparison.Ordinal)) manifest.IniChanges.Remove(change);
            log.Write($"  ini [{setting.Section}] {setting.Key}: {before} -> {setting.Value}");
        }
        ini.Save(iniPath);

        var entry = manifest.Added.FirstOrDefault(f => f.Path.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase));
        if (entry is not null && fileWasOurs) Stamp(entry, iniPath, Sha256(iniPath));
    }

    /// <summary>
    /// Installs made before the rename keep their record in .pcvgs. The first time the app changes such a game, the folder
    /// becomes .upshift and the backup paths in its manifest follow. Returns a message when that couldn't be done.
    /// </summary>
    public static string? MoveLegacyState(string target, InstallLog log)
    {
        var legacy = Path.Combine(target, LegacyStateFolder);
        var current = Path.Combine(target, StateFolder);
        if (!Directory.Exists(legacy)) return null;
        if (Directory.Exists(current))
            return $"This game folder has both {LegacyStateFolder} and {StateFolder}, so nothing was changed. Please report this.";

        try
        {
            Directory.Move(legacy, current);
            var manifestPath = Path.Combine(current, ManifestName);
            if (File.Exists(manifestPath) && JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(manifestPath), Json) is { } m)
            {
                string Fix(string p) => p.StartsWith(LegacyStateFolder + "\\", StringComparison.OrdinalIgnoreCase) ? StateFolder + p[LegacyStateFolder.Length..] : p;
                m.BackupFolder = Fix(m.BackupFolder);
                foreach (var f in m.Replaced.Where(f => f.Backup is not null)) f.Backup = Fix(f.Backup!);
                WriteManifest(target, m);
            }
            log.Write($"  moved {LegacyStateFolder} to {StateFolder} (older install record)");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Put the folder back under its old name so the install stays recognised.
            if (Directory.Exists(current) && !Directory.Exists(legacy))
            {
                try { Directory.Move(current, legacy); } catch (Exception) { }
            }
            return $"The install record in {LegacyStateFolder} couldn't be moved to {StateFolder}: {ex.Message}";
        }
    }

    private static ManifestFile Stamp(ManifestFile file, string path, string sha256)
    {
        var info = new FileInfo(path);
        file.Sha256 = sha256;
        file.Size = info.Length;
        file.LastWriteUtc = info.LastWriteTimeUtc;
        return file;
    }

    private static void WriteManifest(string target, InstallManifest manifest)
    {
        var path = Path.Combine(target, StateFolder, ManifestName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(manifest, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static InstallResult Refuse(InstallLog log, string message)
    {
        log.Write("  refused: " + message);
        return new InstallResult { Message = message };
    }

    /// <summary>Every release file and where it goes: OptiScaler.dll under its loading name, the rest as they are.</summary>
    private static IEnumerable<(string From, string Relative)> FilesToCopy(string sourceDir, string proxyName)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (IsSetupOnly(relative)) continue;
            yield return (file, relative.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ? proxyName : relative);
        }
    }

    private static List<string> Rollback(string target, InstallManifest manifest, bool stateDirExisted, InstallLog log)
    {
        var problems = new List<string>();
        foreach (var file in manifest.Added)
        {
            try { File.Delete(Path.Combine(target, file.Path)); log.Write($"  rollback: removed {file.Path}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add(file.Path); }
        }
        foreach (var file in manifest.Replaced)
        {
            try
            {
                // The failure may have happened before this file was overwritten; then there's nothing to put back.
                var path = Path.Combine(target, file.Path);
                if (File.Exists(path) && Sha256(path) == file.OriginalSha256)
                {
                    log.Write($"  rollback: {file.Path} was never changed");
                    continue;
                }
                File.Copy(Path.Combine(target, file.Backup!), path, overwrite: true);
                log.Write($"  rollback: restored {file.Path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add(file.Path); }
        }
        foreach (var folder in manifest.CreatedFolders.AsEnumerable().Reverse())
            TryRemoveEmptyFolder(Path.Combine(target, folder));

        if (problems.Count == 0)
        {
            try
            {
                var backup = Path.Combine(target, manifest.BackupFolder);
                if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
                var stateDir = Path.Combine(target, StateFolder);
                if (!stateDirExisted) TryRemoveEmptyFolder(stateDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        log.Write(problems.Count == 0 ? "  rollback complete" : "  rollback incomplete: " + string.Join(", ", problems));
        return problems;
    }

    /// <summary>Removes the folder and any empty folders inside it, keeping every folder that still holds a file.</summary>
    private static void RemoveEmptyFolders(string folder)
    {
        if (!Directory.Exists(folder)) return;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(folder).ToList()) RemoveEmptyFolders(sub);
            TryRemoveEmptyFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The manifest from before a switch to a DLSS 5 build (kept in its return folder), or null.</summary>
    private static InstallManifest? ReadSwitchOriginal(string target, SwitchRecord sw)
    {
        var path = Path.Combine(target, sw.ReturnFolder, "manifest.original.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private static void TryRemoveEmptyFolder(string folder)
    {
        try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsOptiScalerDll(string path) =>
        File.Exists(path) && string.Equals(FileVersions.TryGetInfo(path)?.OriginalFilename, "OptiScaler.dll", StringComparison.OrdinalIgnoreCase);

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>Appends to logs\install-yyyy-MM-dd.log. Every install and uninstall is written here, including failures.</summary>
public sealed class InstallLog
{
    private readonly string _path;
    private readonly object _sync = new();

    public InstallLog(string dataDir)
    {
        var dir = Path.Combine(dataDir, "logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"install-{DateTime.Now:yyyy-MM-dd}.log");
    }

    public string FilePath => _path;

    public void Write(string line)
    {
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [pid {Environment.ProcessId}{(IsElevated ? ", admin" : "")}] {line}{Environment.NewLine}";
        lock (_sync)
        {
            try { File.AppendAllText(_path, text); }
            catch (IOException) { Debug.WriteLine(text); }
        }
    }

    private static bool IsElevated =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}
