using System.Text.Json;
using System.Text.Json.Serialization;
using Upshift.Core.Catalog;
using Upshift.Core.Detection;
using Upshift.Core.Models;

namespace Upshift.Core.Install;

/// <summary>One game file Upshift replaced with a newer upscaler DLL, and the original it backed up.</summary>
public sealed class UpscalerFileChange
{
    /// <summary>Relative to the game's install folder.</summary>
    public string Path { get; set; } = "";
    /// <summary>The game's original, relative to the install folder (.upshift\originals\…).</summary>
    public string Backup { get; set; } = "";
    public string OriginalSha256 { get; set; } = "";
    public string? OriginalVersion { get; set; }
    public long OriginalSize { get; set; }

    /// <summary>The file as Upshift left it.</summary>
    public string Sha256 { get; set; } = "";
    public string? Version { get; set; }
    public long? Size { get; set; }
    public DateTime? LastWriteUtc { get; set; }

    /// <summary>The version it had just before the last update (the original, or an earlier Upshift update).</summary>
    public string? PreviousVersion { get; set; }
    public DateTime FirstUpdatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>.upshift\upscaler-files.json in the game's install folder.</summary>
public sealed class UpscalerFilesRecord
{
    public int Schema { get; set; } = 1;
    public string GameId { get; set; } = "";
    public List<UpscalerFileChange> Files { get; set; } = new();
}

/// <summary>One file for an update or restore plan (also carried to the elevated helper).</summary>
public sealed class UpscalerFileJob
{
    /// <summary>The game file, relative to the install folder.</summary>
    public string Path { get; set; } = "";
    /// <summary>Update only: the downloaded DLL in the app's components\upscaler-files folder.</summary>
    public string? SourceFile { get; set; }
    public string? SourceSha256 { get; set; }
    public string? Version { get; set; }
    public UpscalerFamily Family { get; set; }
}

public enum UpscalerFileState
{
    /// <summary>The game's own file, and nothing newer is offered for it.</summary>
    GameFile,
    /// <summary>The game's own file; a newer one is offered.</summary>
    UpdateAvailable,
    /// <summary>Upshift updated it and it's still as Upshift left it.</summary>
    UpdatedByUpshift,
    /// <summary>Upshift updated it, but the game (an update or "verify files") put its old file back.</summary>
    GameRestoredOld,
    /// <summary>Upshift updated it, but something has since replaced it with a different file.</summary>
    ChangedSince,
    /// <summary>OptiScaler's install put its own copy here (replacing the game's, or adding it).</summary>
    OptiScalerCopy
}

/// <summary>One row of "Upscaler files".</summary>
public sealed class UpscalerFileItem
{
    public string RelativePath { get; init; } = "";
    public string FileName => System.IO.Path.GetFileName(RelativePath);
    public UpscalerFamily Family { get; init; }
    public string Feature { get; init; } = "";
    public string? CurrentVersion { get; init; }
    public UpscalerFileState State { get; init; }
    /// <summary>The newer file this one can be updated to (null when there's none or it's not allowed).</summary>
    public UpscalerFileSource? Target { get; init; }
    /// <summary>Why there's no update, when that's worth saying ("32-bit file", "DLSS 1.x can't be swapped"…).</summary>
    public string? Note { get; init; }
    /// <summary>The backed-up original's version (Upshift's or OptiScaler's backup).</summary>
    public string? OriginalVersion { get; init; }
    /// <summary>For OptiScaler's copies: the OptiScaler version installed.</summary>
    public string? OptiScalerVersion { get; init; }
    /// <summary>For OptiScaler's copies: true when it replaced a game file (false: OptiScaler added it).</summary>
    public bool OptiScalerReplaced { get; init; }
    public UpscalerFileChange? Record { get; init; }

    public bool CanUpdate => Target is not null;
    /// <summary>Upshift's backup of the original is there, so Restore can bring it back.</summary>
    public bool CanRestore => Record is not null && State is UpscalerFileState.UpdatedByUpshift;
}

/// <summary>
/// Updates the DLSS, FSR and XeSS files a game ships with to newer, vendor-signed copies from the catalog, and puts
/// the originals back. Only files the game already has are replaced (never added), 64-bit for 64-bit, same file name.
/// Every original is backed up in .upshift\originals and recorded in .upshift\upscaler-files.json in the game's install
/// folder, so "Restore original files" gives back the exact originals (checked by SHA-256).
/// </summary>
public static class UpscalerFiles
{
    public const string RecordName = "upscaler-files.json";
    public const string OriginalsFolder = "originals";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string RecordPath(string installDir) => Path.Combine(installDir, OptiScalerInstaller.StateFolder, RecordName);

    public static UpscalerFilesRecord? ReadRecord(string installDir)
    {
        var path = RecordPath(installDir);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<UpscalerFilesRecord>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    // ---------------- What each file is and what it can become ----------------

    /// <summary>Every upscaler DLL the game has that this feature deals with, with its state and any update.</summary>
    public static List<UpscalerFileItem> Items(GameInfo game, UpscalerCatalog catalog)
    {
        var record = ReadRecord(game.InstallDir);
        var opti = game.TargetDir is null ? null : OptiScalerInstaller.ReadManifest(game.TargetDir);
        var items = new List<UpscalerFileItem>();

        foreach (var dll in game.Upscalers)
        {
            var sources = catalog.UpscalerFiles.Sources.Where(s => s.File.Equals(dll.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sources.Count == 0) continue; // Streamline, FSR 2, DLSS FG and the like: not updated by Upshift

            var full = Path.GetFullPath(dll.FullPath);
            var feature = Fingerprints.UpscalerFiles.TryGetValue(dll.FileName, out var fp) ? fp.Feature : "";

            // OptiScaler's own copy: say so, and leave it to OptiScaler's updates.
            if (opti is { Removed: false } && game.TargetDir is not null)
            {
                var added = opti.Added.FirstOrDefault(f => SamePath(game.TargetDir, f.Path, full));
                var replaced = opti.Replaced.FirstOrDefault(f => SamePath(game.TargetDir, f.Path, full));
                if (added is not null || replaced is not null)
                {
                    var backup = replaced?.Backup is { } b ? Path.Combine(game.TargetDir, b) : null;
                    items.Add(new UpscalerFileItem
                    {
                        RelativePath = dll.RelativePath, Family = dll.Family, Feature = feature, CurrentVersion = dll.Version,
                        State = UpscalerFileState.OptiScalerCopy, OptiScalerVersion = opti.Version, OptiScalerReplaced = replaced is not null,
                        OriginalVersion = backup is not null && File.Exists(backup) ? FileVersions.Read(backup) : null
                    });
                    continue;
                }
            }

            var entry = record?.Files.FirstOrDefault(f => SamePath(game.InstallDir, f.Path, full));
            var state = entry is null ? UpscalerFileState.GameFile : StateOf(game.InstallDir, entry);
            var (target, note) = game.HasAntiCheat
                ? (null, "Blocked: this game uses anti-cheat.")
                : TargetFor(full, dll.Version, sources);

            items.Add(new UpscalerFileItem
            {
                RelativePath = dll.RelativePath, Family = dll.Family, Feature = feature, CurrentVersion = dll.Version,
                State = state == UpscalerFileState.GameFile && target is not null ? UpscalerFileState.UpdateAvailable : state,
                Target = target, Note = note, Record = entry, OriginalVersion = entry?.OriginalVersion
            });
        }
        return items.OrderBy(i => i.Family).ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The newest allowed source for a file of this version, or null with the reason.</summary>
    public static (UpscalerFileSource? Target, string? Note) TargetFor(string fullPath, string? version, IReadOnlyList<UpscalerFileSource> sources)
    {
        if (version is null) return (null, "Its version can't be read.");
        var current = ParseVersion(version);
        var fitting = sources.Where(s => (s.MinVersion is null || current >= ParseVersion(s.MinVersion))
                                         && (s.BelowVersion is null || current < ParseVersion(s.BelowVersion)))
                             .OrderByDescending(s => ParseVersion(s.Version))
                             .FirstOrDefault();
        if (fitting is null)
        {
            var lowest = sources.Where(s => s.MinVersion is not null).Select(s => ParseVersion(s.MinVersion!)).DefaultIfEmpty().Min();
            return (null, lowest is not null && current < lowest
                ? $"Version {version} is too old to be swapped safely."
                : $"No tested update for version {version}.");
        }
        if (current >= ParseVersion(fitting.Version)) return (null, null); // already the newest
        if (PeReader.TryRead(fullPath)?.Is64Bit != true) return (null, "This is a 32-bit file; the updates are 64-bit only.");
        return (fitting, null);
    }

    private static UpscalerFileState StateOf(string installDir, UpscalerFileChange entry)
    {
        var path = Path.Combine(installDir, entry.Path);
        if (!File.Exists(path)) return UpscalerFileState.ChangedSince;
        var info = new FileInfo(path);
        if (info.Length == entry.Size && entry.LastWriteUtc is { } t && info.LastWriteTimeUtc == t) return UpscalerFileState.UpdatedByUpshift;
        var hash = OptiScalerInstaller.Sha256(path);
        return hash == entry.Sha256 ? UpscalerFileState.UpdatedByUpshift
            : hash == entry.OriginalSha256 ? UpscalerFileState.GameRestoredOld
            : UpscalerFileState.ChangedSince;
    }

    /// <summary>
    /// For scans: the files Upshift updated that are no longer its copy, because the game put its old one back.
    /// Uses size and time first, so unchanged files aren't hashed.
    /// </summary>
    public static List<string> RestoredByGame(string installDir)
    {
        var record = ReadRecord(installDir);
        if (record is null) return new();
        return record.Files.Where(f => StateOf(installDir, f) == UpscalerFileState.GameRestoredOld).Select(f => f.Path).ToList();
    }

    // ---------------- Update ----------------

    /// <summary>
    /// Replaces each game file in the plan with its downloaded copy. Checks everything first (paths inside the game,
    /// same name, file already there, 64-bit, newer, vendor signature, hash), backs up each original once, and puts
    /// everything back if any step fails.
    /// </summary>
    public static InstallResult Update(InstallPlan plan, IReadOnlyDictionary<UpscalerFamily, List<string>> signers, InstallLog log)
    {
        var root = plan.TargetDir;
        log.Write($"UPDATE UPSCALER FILES {plan.GameName} | {plan.UpscalerFiles.Count} file(s) | in {root}");
        if (plan.UpscalerFiles.Count == 0) return Refuse(log, "No files were chosen.");

        var recordPath = RecordPath(root);
        var recordBefore = File.Exists(recordPath) ? File.ReadAllBytes(recordPath) : null;
        var record = ReadRecord(root) ?? new UpscalerFilesRecord { GameId = plan.GameId };
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var rollbackDir = Path.Combine(root, OptiScalerInstaller.StateFolder, "rollback" + stamp);
        var done = new List<(string Full, string Rollback)>();
        var newBackups = new List<string>();
        var stateDirExisted = Directory.Exists(Path.Combine(root, OptiScalerInstaller.StateFolder));

        // 1. Check every file before touching any.
        var checkedJobs = new List<(UpscalerFileJob Job, string Full, string CurrentHash, string? CurrentVersion)>();
        foreach (var job in plan.UpscalerFiles)
        {
            if (CheckJob(root, job, signers) is { } problem) return Refuse(log, problem);
            var full = Path.GetFullPath(Path.Combine(root, job.Path));
            checkedJobs.Add((job, full, OptiScalerInstaller.Sha256(full), FileVersions.Read(full)));
        }

        try
        {
            foreach (var (job, full, currentHash, currentVersion) in checkedJobs)
            {
                var entry = record.Files.FirstOrDefault(f => SamePath(root, f.Path, full));
                var backupRelative = Path.Combine(OptiScalerInstaller.StateFolder, OriginalsFolder, job.Path);
                var backup = Path.Combine(root, backupRelative);

                // A copy of the file as it is now, only for putting things back if this update fails.
                var rollback = Path.Combine(rollbackDir, job.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(rollback)!);
                File.Copy(full, rollback, overwrite: true);
                done.Add((full, rollback));

                // 2. Back up the game's original once. If the game has since shipped a different file of its own,
                //    that one becomes the original.
                var isOurs = entry is not null && currentHash == entry.Sha256;
                var isOriginal = entry is not null && currentHash == entry.OriginalSha256;
                if (entry is null || (!isOurs && !isOriginal))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    if (File.Exists(backup)) File.Copy(backup, backup + ".replaced-" + stamp, overwrite: true);
                    File.Copy(full, backup, overwrite: true);
                    if (OptiScalerInstaller.Sha256(backup) != currentHash) throw new IOException($"The backup of {job.Path} doesn't match the original.");
                    newBackups.Add(backup);
                    if (entry is null)
                    {
                        entry = new UpscalerFileChange { Path = job.Path, FirstUpdatedUtc = DateTime.UtcNow };
                        record.Files.Add(entry);
                    }
                    entry.Backup = backupRelative;
                    entry.OriginalSha256 = currentHash;
                    entry.OriginalVersion = currentVersion;
                    entry.OriginalSize = new FileInfo(backup).Length;
                    log.Write($"  backed up {job.Path} ({currentVersion}) -> {backupRelative}");
                }

                // 3. Copy the new file in and check it.
                File.Copy(job.SourceFile!, full, overwrite: true);
                var hash = OptiScalerInstaller.Sha256(full);
                if (hash != job.SourceSha256) throw new IOException($"{job.Path} didn't copy correctly.");
                var info = new FileInfo(full);
                entry.PreviousVersion = currentVersion;
                entry.Sha256 = hash;
                entry.Version = job.Version;
                entry.Size = info.Length;
                entry.LastWriteUtc = info.LastWriteTimeUtc;
                entry.UpdatedUtc = DateTime.UtcNow;
                log.Write($"  updated {job.Path}: {currentVersion} -> {job.Version} ({hash[..12]})");
            }

            WriteRecord(root, record);
            // From here the update has happened; tidying up can't undo it.
            try
            {
                var originals = Path.Combine(root, OptiScalerInstaller.StateFolder, OriginalsFolder);
                if (Directory.Exists(originals))
                    foreach (var old in Directory.EnumerateFiles(originals, "*.replaced-" + stamp, SearchOption.AllDirectories)) File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Write($"  (tidy-up: {ex.Message})"); }
            TryDeleteFolder(rollbackDir);
            var names = string.Join(", ", checkedJobs.Select(j => Path.GetFileName(j.Job.Path)));
            log.Write($"  done: {checkedJobs.Count} file(s) updated");
            return new InstallResult { Success = true, Message = checkedJobs.Count == 1 ? $"Updated {names}." : $"Updated {checkedJobs.Count} files: {names}." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"  FAILED: {ex.Message} - putting things back");
            var problems = new List<string>();
            foreach (var (full, rollback) in done)
            {
                try { File.Copy(rollback, full, overwrite: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problems.Add(Path.GetRelativePath(root, full)); }
            }
            foreach (var backup in newBackups)
            {
                try
                {
                    var replaced = backup + ".replaced-" + stamp;
                    if (File.Exists(replaced)) File.Move(replaced, backup, overwrite: true);
                    else File.Delete(backup);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            try
            {
                if (recordBefore is not null) File.WriteAllBytes(recordPath, recordBefore);
                else if (File.Exists(recordPath)) File.Delete(recordPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problems.Add(RecordName); }
            if (problems.Count == 0)
            {
                TryDeleteFolder(rollbackDir);
                RemoveEmptyFolders(root, stateDirExisted);
            }
            log.Write(problems.Count == 0 ? "  put back" : "  could not put back: " + string.Join(", ", problems));
            return new InstallResult
            {
                Message = problems.Count == 0
                    ? $"The update failed and nothing was changed: {ex.Message}"
                    : $"The update failed: {ex.Message} Some files couldn't be put back ({string.Join(", ", problems)}); copies are in {rollbackDir}."
            };
        }
    }

    /// <summary>Null when the job is safe to carry out.</summary>
    private static string? CheckJob(string root, UpscalerFileJob job, IReadOnlyDictionary<UpscalerFamily, List<string>> signers)
    {
        if (Path.IsPathRooted(job.Path) || job.Path.Contains(".."))
            return $"{job.Path} isn't a path inside the game.";
        var full = Path.GetFullPath(Path.Combine(root, job.Path));
        var rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return $"{job.Path} isn't inside the game folder.";
        if (full.Contains("\\" + OptiScalerInstaller.StateFolder + "\\", StringComparison.OrdinalIgnoreCase)) return $"{job.Path} is one of Upshift's own files.";
        if (!File.Exists(full)) return $"{job.Path} isn't in the game folder, and Upshift never adds upscaler files.";
        if (job.SourceFile is null || !File.Exists(job.SourceFile)) return $"The downloaded copy of {Path.GetFileName(job.Path)} is missing.";
        if (!Path.GetFileName(job.SourceFile).Equals(Path.GetFileName(full), StringComparison.OrdinalIgnoreCase))
            return $"{Path.GetFileName(job.SourceFile)} can't replace {Path.GetFileName(full)}: only a file of the same name can.";
        if (OptiScalerInstaller.Sha256(job.SourceFile) != job.SourceSha256) return $"The downloaded {Path.GetFileName(job.SourceFile)} has changed since it was checked.";
        if (!signers.TryGetValue(job.Family, out var names) || names.Count == 0) return $"No signer is known for {job.Family} files.";
        if (SignatureCheck.Problem(job.SourceFile, names) is { } signature) return signature + " It wasn't used.";
        if (PeReader.TryRead(full)?.Is64Bit != true || PeReader.TryRead(job.SourceFile)?.Is64Bit != true)
            return $"{job.Path} and its update aren't both 64-bit.";
        var sourceVersion = FileVersions.Read(job.SourceFile);
        if (!Components.ComponentStore.SameVersion(sourceVersion, job.Version)) return $"The downloaded {Path.GetFileName(job.SourceFile)} isn't version {job.Version}.";
        var currentVersion = FileVersions.Read(full);
        if (currentVersion is null || ParseVersion(currentVersion) >= ParseVersion(sourceVersion!))
            return $"{job.Path} is already version {currentVersion}; the update ({sourceVersion}) isn't newer.";
        return null;
    }

    // ---------------- Restore ----------------

    /// <summary>
    /// Puts back the originals of the files in the plan (all of Upshift's updates when the plan lists none). A file
    /// that changed after Upshift updated it is left alone, and so is its backup.
    /// </summary>
    public static InstallResult Restore(InstallPlan plan, InstallLog log)
    {
        var root = plan.TargetDir;
        log.Write($"RESTORE UPSCALER FILES {plan.GameName} | in {root}");
        var record = ReadRecord(root);
        if (record is null || record.Files.Count == 0) return Refuse(log, "Upshift hasn't updated any upscaler files in this game.");

        var wanted = plan.UpscalerFiles.Count == 0
            ? record.Files.ToList()
            : record.Files.Where(f => plan.UpscalerFiles.Any(j => j.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase))).ToList();
        var restored = new List<string>();
        var kept = new List<string>();
        try
        {
            foreach (var entry in wanted)
            {
                var full = Path.Combine(root, entry.Path);
                var backup = Path.Combine(root, entry.Backup);
                var current = File.Exists(full) ? OptiScalerInstaller.Sha256(full) : null;
                if (current == entry.OriginalSha256)
                {
                    // The game already has its original back.
                    if (File.Exists(backup)) File.Delete(backup);
                    record.Files.Remove(entry);
                    restored.Add(entry.Path);
                    log.Write($"  {entry.Path} was already the original");
                    continue;
                }
                if (current is not null && current != entry.Sha256)
                {
                    kept.Add(entry.Path);
                    log.Write($"  KEPT (changed since Upshift updated it), backup kept: {entry.Path}");
                    continue;
                }
                if (!File.Exists(backup)) throw new IOException($"The backup of {entry.Path} is missing.");
                if (OptiScalerInstaller.Sha256(backup) != entry.OriginalSha256) throw new IOException($"The backup of {entry.Path} doesn't match the original.");
                File.Copy(backup, full, overwrite: true);
                if (OptiScalerInstaller.Sha256(full) != entry.OriginalSha256) throw new IOException($"Restoring {entry.Path} gave a different file than the original.");
                File.Delete(backup);
                record.Files.Remove(entry);
                restored.Add(entry.Path);
                log.Write($"  restored {entry.Path} ({entry.OriginalVersion})");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveOrRemove(root, record);
            log.Write($"  FAILED: {ex.Message}");
            return new InstallResult { Message = $"Restoring stopped part-way: {ex.Message} {restored.Count} file(s) were put back; the rest are recorded, so you can try again." };
        }

        SaveOrRemove(root, record);
        log.Write($"  done: {restored.Count} restored, {kept.Count} kept");
        return new InstallResult
        {
            Success = true,
            KeptChanged = kept,
            Message = kept.Count == 0
                ? restored.Count == 1 ? $"The game's original {Path.GetFileName(restored[0])} is back." : $"The game's {restored.Count} original files are back."
                : $"{restored.Count} original file(s) are back. {string.Join(", ", kept)} changed after Upshift updated them, so they were left in place."
        };
    }

    // ---------------- "Changes Upshift made to this game" ----------------

    /// <summary>One dated line per file Upshift updated. <paramref name="versionLabel"/> adds names like "(DLSS 4.5)".</summary>
    public static List<ChangeEntry> Changes(string installDir, Func<string, string, string> versionLabel)
    {
        var record = ReadRecord(installDir);
        if (record is null) return new();
        string Label(string? version, string name) => version is null ? "unknown version" : versionLabel(version, name);
        return record.Files.Select(f =>
        {
            var name = Path.GetFileName(f.Path);
            var state = StateOf(installDir, f);
            var from = f.PreviousVersion is not null && f.PreviousVersion != f.OriginalVersion
                ? $"{Label(f.PreviousVersion, name)} (original {Label(f.OriginalVersion, name)})"
                : Label(f.OriginalVersion, name);
            return new ChangeEntry(f.UpdatedUtc, $"Updated {f.Path}: {from} → {Label(f.Version, name)}",
                $"The original is backed up in {Path.GetDirectoryName(f.Backup)} and comes back with Restore original files."
                + state switch
                {
                    UpscalerFileState.GameRestoredOld => " The game has since put its old file back.",
                    UpscalerFileState.ChangedSince => " The file has changed since (a game update?).",
                    _ => ""
                });
        }).ToList();
    }

    // ---------------- helpers ----------------

    private static void SaveOrRemove(string root, UpscalerFilesRecord record)
    {
        var state = Path.Combine(root, OptiScalerInstaller.StateFolder);
        if (record.Files.Count > 0)
        {
            WriteRecord(root, record);
            return;
        }
        var path = RecordPath(root);
        if (File.Exists(path)) File.Delete(path);
        RemoveEmptyFolders(root, stateDirExisted: true);
        // .upshift itself goes only when nothing else (OptiScaler's record) is in it.
        try { if (Directory.Exists(state) && !Directory.EnumerateFileSystemEntries(state).Any()) Directory.Delete(state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void WriteRecord(string root, UpscalerFilesRecord record)
    {
        var path = RecordPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(record, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Removes empty folders under .upshift\originals (and .upshift itself when this run created it).</summary>
    private static void RemoveEmptyFolders(string root, bool stateDirExisted)
    {
        var state = Path.Combine(root, OptiScalerInstaller.StateFolder);
        var originals = Path.Combine(state, OriginalsFolder);
        try
        {
            if (Directory.Exists(originals))
            {
                foreach (var dir in Directory.EnumerateDirectories(originals, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                if (!Directory.EnumerateFileSystemEntries(originals).Any()) Directory.Delete(originals);
            }
            if (!stateDirExisted && Directory.Exists(state) && !Directory.EnumerateFileSystemEntries(state).Any()) Directory.Delete(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFolder(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool SamePath(string baseDir, string relative, string full) =>
        Path.GetFullPath(Path.Combine(baseDir, relative)).Equals(full, StringComparison.OrdinalIgnoreCase);

    private static InstallResult Refuse(InstallLog log, string message)
    {
        log.Write("  refused: " + message);
        return new InstallResult { Message = message };
    }

    /// <summary>A version with four parts (missing ones are 0), so "310.9.1" equals "310.9.1.0".</summary>
    public static Version ParseVersion(string v)
    {
        var parts = v.Split(new[] { '.', ',', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var n) ? n : 0).Take(4).ToList();
        while (parts.Count < 4) parts.Add(0);
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }
}
