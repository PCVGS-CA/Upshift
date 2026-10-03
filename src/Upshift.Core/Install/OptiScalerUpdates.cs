using System.Text.Json;

namespace Upshift.Core.Install;

/// <summary>Update, "Undo last update", repair and the scan-time check of installed files.</summary>
public static partial class OptiScalerInstaller
{
    private const string IniName = "OptiScaler.ini";
    private const string SnapshotName = "snapshot.json";

    /// <summary>Files a user supplies through the options (never part of a release). They may be added, removed or swapped.</summary>
    public static readonly string[] UserFileNames = { "amdxcffx64.dll", "nvngx_dlssnr.dll" };

    private sealed class SnapshotFile
    {
        public string Path { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    private static IEnumerable<ManifestFile> AllEntries(InstallManifest manifest) => manifest.Added.Concat(manifest.Replaced);

    private static bool IsIni(string relative) => relative.Equals(IniName, StringComparison.OrdinalIgnoreCase);

    private static bool IsUserFile(string relative) => UserFileNames.Contains(relative, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when the last update can be undone (its saved copy is still there). While a DLSS 5 build is switched in,
    /// the update from before the switch can't be: "Switch back" is the way back.
    /// </summary>
    public static bool CanUndo(string targetDir) =>
        ReadManifest(targetDir) is { Removed: false, LastUpdate.UndoFolder: { } undo } manifest
        && File.Exists(Path.Combine(targetDir, undo, ManifestName))
        && !(manifest.Switch is not null && manifest.LastUpdate!.UpdatedUtc < manifest.Switch.SwitchedUtc);

    private const string IniAfterSwitchName = "ini-after-switch.ini";
    private const string OriginalManifestName = "manifest.original.json";

    // ---------------- check (during scans) ----------------

    /// <summary>
    /// Compares an install with its manifest: files that are missing, or that changed since we copied them. OptiScaler.ini
    /// and user-supplied files only count when missing (changing them is normal). Read-only; empty when all is well.
    /// </summary>
    public static List<string> Verify(string targetDir)
    {
        var problems = new List<string>();
        if (ReadManifest(targetDir) is not { Removed: false } manifest) return problems;
        try
        {
            foreach (var file in AllEntries(manifest))
            {
                var path = Path.Combine(targetDir, file.Path);
                if (!File.Exists(path)) problems.Add($"{file.Path} is missing");
                else if (!IsIni(file.Path) && !IsUserFile(file.Path) && HasChanged(file, path)) problems.Add($"{file.Path} has changed");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The files couldn't be checked: {ex.Message}");
        }
        return problems;
    }

    /// <summary>True when the file is still exactly as this app left it.</summary>
    public static bool IsUnchanged(string targetDir, ManifestFile file)
    {
        var path = Path.Combine(targetDir, file.Path);
        try { return File.Exists(path) && !HasChanged(file, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Size first, then time; only hashes when those don't settle it (older manifests have neither).</summary>
    private static bool HasChanged(ManifestFile file, string path)
    {
        var info = new FileInfo(path);
        if (file.Size is { } size && size != info.Length) return true;
        if (file.Size == info.Length && file.LastWriteUtc == info.LastWriteTimeUtc) return false;
        return Sha256(path) != file.Sha256;
    }

    // ---------------- update ----------------

    /// <summary>
    /// Replaces the installed OptiScaler with another release under the same loading name. First a copy of every file
    /// the manifest lists (OptiScaler.ini and the manifest itself included) goes into .upshift\undo…, for
    /// "Undo last update". Game files the new release overwrites are backed up like on a fresh install. OptiScaler.ini
    /// becomes the new release's file with every value that differed from the old release's default carried over.
    /// If anything fails, the saved copy is put back.
    /// </summary>
    public static InstallResult Update(InstallPlan plan, InstallLog log)
    {
        var target = plan.TargetDir;
        var source = plan.SourceDir ?? "";
        log.Write($"UPDATE {plan.GameName} | to {plan.ComponentId} {plan.Version} | in {target}");
        if (MoveLegacyState(target, log) is { } moveProblem) return Refuse(log, moveProblem);

        // A switch (to a DLSS 5 build) is an update to another component that also saves an exact return point:
        // the previous "Undo last update" copy stays, and "Switch back" restores everything from before the switch.
        var isSwitch = plan.Operation == InstallOperation.SwitchBuild;
        var manifest = ReadManifest(target);
        if (manifest is null or { Removed: true }) return Refuse(log, "OptiScaler isn't installed here by this app, so it wasn't updated.");
        if (isSwitch && manifest.Switch is not null) return Refuse(log, "This game already uses a DLSS 5 build of OptiScaler; switch back first.");
        if (!isSwitch && manifest.Version == plan.Version && manifest.ComponentId == plan.ComponentId)
            return Refuse(log, $"OptiScaler {plan.Version} is already installed here.");
        if (!File.Exists(Path.Combine(source, "OptiScaler.dll"))) return Refuse(log, "The downloaded OptiScaler is missing OptiScaler.dll.");

        var oldManifestJson = JsonSerializer.Serialize(manifest, Json);
        var undoRelative = Path.Combine(StateFolder, (isSwitch ? "switch" : "undo") + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var undoDir = Path.Combine(target, undoRelative);
        var previousUndo = manifest.LastUpdate?.UndoFolder;
        // While switched, the undo copy from before the switch belongs to the return point, so it's never deleted.
        var keepPreviousUndo = isSwitch || manifest.Switch is not null;
        var fromComponent = manifest.ComponentId;
        var proxy = manifest.ProxyName;

        var newFiles = FilesToCopy(source, proxy)
            .Concat(plan.ExtraSourceDir is { } extra && Directory.Exists(extra) ? FilesToCopy(extra, proxy) : Enumerable.Empty<(string From, string Relative)>())
            .ToList();
        var newSet = newFiles.Select(f => f.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var oldSource = plan.OldSourceDir is { } old && File.Exists(Path.Combine(old, "OptiScaler.dll")) ? old : null;
        // The files the old release brought. Without its download, everything but user-supplied files counts.
        var oldRelease = oldSource is not null
            ? FilesToCopy(oldSource, proxy).Select(f => f.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : AllEntries(manifest).Select(f => f.Path).Where(p => !IsUserFile(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var iniPath = Path.Combine(target, IniName);
        var installedIni = File.Exists(iniPath) ? File.ReadAllText(iniPath) : null;
        var oldIniEntry = manifest.Added.FirstOrDefault(f => IsIni(f.Path));
        var oldIniHash = oldIniEntry?.Sha256;
        var iniWasOurs = oldIniEntry is not null && installedIni is not null && Sha256(iniPath) == oldIniEntry.Sha256;

        var record = new UpdateRecord { FromVersion = manifest.Version, ToVersion = plan.Version, UpdatedUtc = DateTime.UtcNow, UndoFolder = undoRelative };
        var snapshotSaved = false;
        try
        {
            // 1. Everything as it is now, for "Undo last update" (and for putting things back if this fails).
            var snapshot = new List<SnapshotFile>();
            Directory.CreateDirectory(undoDir);
            foreach (var file in AllEntries(manifest))
            {
                var from = Path.Combine(target, file.Path);
                if (!File.Exists(from)) continue;
                var to = Path.Combine(undoDir, "files", file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(from, to, overwrite: false);
                var hash = Sha256(from);
                if (Sha256(to) != hash) throw new IOException($"The saved copy of {file.Path} doesn't match the file.");
                snapshot.Add(new SnapshotFile { Path = file.Path, Sha256 = hash });
            }
            var saved = JsonSerializer.Deserialize<InstallManifest>(oldManifestJson, Json)!;
            // Only the latest update can be undone. A switch keeps it: switching back returns to exactly this state.
            if (!isSwitch && saved.LastUpdate is not null) saved.LastUpdate.UndoFolder = null;
            File.WriteAllText(Path.Combine(undoDir, ManifestName), JsonSerializer.Serialize(saved, Json));
            File.WriteAllText(Path.Combine(undoDir, SnapshotName), JsonSerializer.Serialize(snapshot, Json));
            // A switch also keeps the manifest file itself, so switching back restores it byte for byte (time included).
            if (isSwitch) File.Copy(Path.Combine(target, StateFolder, ManifestName), Path.Combine(undoDir, OriginalManifestName), overwrite: true);
            snapshotSaved = true;
            log.Write($"  saved {snapshot.Count} file(s) and the manifest in {undoRelative}");

            // 2. Files of the old release that the new one doesn't have.
            foreach (var file in manifest.Added.Where(f => oldRelease.Contains(f.Path) && !newSet.Contains(f.Path)).ToList())
            {
                var path = Path.Combine(target, file.Path);
                if (File.Exists(path)) File.Delete(path);
                manifest.Added.Remove(file);
                record.RemovedFiles.Add(file.Path);
                log.Write($"  removed {file.Path} (not in {plan.Version})");
            }
            foreach (var file in manifest.Replaced.Where(f => oldRelease.Contains(f.Path) && !newSet.Contains(f.Path)).ToList())
            {
                // The game's own file comes back. Its backup stays, so an undo still has it.
                var path = Path.Combine(target, file.Path);
                File.Copy(Path.Combine(target, file.Backup!), path, overwrite: true);
                if (Sha256(path) != file.OriginalSha256) throw new IOException($"Restoring {file.Path} gave a different file than the original.");
                manifest.Replaced.Remove(file);
                record.RemovedFiles.Add(file.Path);
                log.Write($"  restored the game's {file.Path} (not in {plan.Version})");
            }

            // 3. The new release, under the same loading name.
            foreach (var (from, relative) in newFiles)
            {
                var to = Path.Combine(target, relative);
                var entry = AllEntries(manifest).FirstOrDefault(f => f.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    CreateFolderFor(target, to, manifest);
                    if (File.Exists(to))
                    {
                        // A game file the old version didn't touch: backed up like on a fresh install.
                        var backupRelative = Path.Combine(manifest.BackupFolder, relative);
                        var backup = Path.Combine(target, backupRelative);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(to, backup, overwrite: true);
                        var originalHash = Sha256(to);
                        if (Sha256(backup) != originalHash) throw new IOException($"The backup of {relative} doesn't match the original.");
                        entry = new ManifestFile { Path = relative, Backup = backupRelative, OriginalSha256 = originalHash };
                        manifest.Replaced.Add(entry);
                        log.Write($"  backed up {relative} -> {backupRelative}");
                    }
                    else
                    {
                        entry = new ManifestFile { Path = relative };
                        manifest.Added.Add(entry);
                    }
                    record.AddedFiles.Add(relative);
                }

                File.Copy(from, to, overwrite: true);
                var hash = Sha256(to);
                if (hash != Sha256(from)) throw new IOException($"{relative} didn't copy correctly.");
                Stamp(entry, to, hash);
            }
            log.Write($"  copied {newFiles.Count} file(s) of {plan.Version} ({record.AddedFiles.Count} new, {record.RemovedFiles.Count} dropped)");

            // 4. OptiScaler.ini: the new file, with the values that were changed from the old defaults carried over.
            if (installedIni is not null && File.Exists(iniPath))
            {
                MergeIni(iniPath, installedIni, oldSource is null ? null : Path.Combine(oldSource, IniName), manifest, record, log);
                if (manifest.Added.FirstOrDefault(f => IsIni(f.Path)) is { } iniEntry)
                {
                    if (iniWasOurs) Stamp(iniEntry, iniPath, Sha256(iniPath));
                    // Edited by hand before: stays "changed by you", so Uninstall still leaves it in place.
                    else if (oldIniHash is not null) iniEntry.Sha256 = oldIniHash;
                }
            }

            // 5. A switch: the settings chosen for it (e.g. AMD-NR's runtime) and the user's DLSS 5 file.
            if (isSwitch)
            {
                if (plan.IniSettings.Count > 0 && File.Exists(iniPath))
                {
                    var ours = manifest.Added.FirstOrDefault(f => IsIni(f.Path)) is { } entry && Sha256(iniPath) == entry.Sha256;
                    ApplyIni(target, manifest, plan.IniSettings, log, ours);
                }
                if (plan.AddFileFrom is { } userFile && plan.AddFileAs is { } userName)
                    AddUserFile(target, manifest, userFile, userName, log);
                if (File.Exists(iniPath)) File.Copy(iniPath, Path.Combine(undoDir, IniAfterSwitchName), overwrite: true);
            }

            manifest.Version = plan.Version;
            manifest.ComponentId = plan.ComponentId;
            if (isSwitch)
                manifest.Switch = new SwitchRecord
                {
                    FromComponentId = fromComponent, FromVersion = record.FromVersion, SwitchedUtc = DateTime.UtcNow, ReturnFolder = undoRelative
                };
            else manifest.LastUpdate = record;
            WriteManifest(target, manifest);
            if (previousUndo is not null && !keepPreviousUndo) TryDeleteFolder(Path.Combine(target, previousUndo));

            var message = isSwitch
                ? $"Switched to {plan.ComponentName ?? plan.ComponentId} {plan.Version} (still loading as {proxy}). \"Switch back\" returns to OptiScaler {record.FromVersion} exactly."
                : $"OptiScaler updated from {record.FromVersion} to {plan.Version} (still loading as {proxy}).";
            if (record.KeptIniValues.Count > 0)
                message += $" Kept your settings: {string.Join(", ", record.KeptIniValues.Select(c => $"{c.Key}={c.Current}"))}.";
            if (record.NewIniOptions.Count > 0) message += $" {record.NewIniOptions.Count} new setting(s) start at their defaults.";
            log.Write("  done: " + message);
            return new InstallResult { Success = true, Manifest = manifest, Message = message };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Write($"  FAILED: {ex.Message} - putting the previous version back");
            if (!snapshotSaved)
            {
                TryDeleteFolder(undoDir);
                return new InstallResult { Message = $"The update failed and nothing was changed: {ex.Message}" };
            }
            var (problems, _) = RestoreSnapshot(target, undoDir, manifest, log);
            if (problems.Count == 0) TryDeleteFolder(undoDir);
            return new InstallResult
            {
                Message = problems.Count == 0
                    ? $"The update failed and OptiScaler {record.FromVersion} was put back as it was: {ex.Message}"
                    : $"The update failed: {ex.Message} Some files couldn't be put back ({string.Join(", ", problems)}); the saved copy is in {undoRelative}."
            };
        }
    }

    /// <summary>
    /// The new release's OptiScaler.ini is the base. Every value in the installed file that differs from the old
    /// release's default (so the user or this app set it) is written into it, if the new version still has that
    /// setting. Settings new in this version keep their defaults. Without the old release's file, only the values this
    /// app recorded changing are carried over.
    /// </summary>
    private static void MergeIni(string iniPath, string installedText, string? oldDefaultsPath, InstallManifest manifest, UpdateRecord record, InstallLog log)
    {
        var defaults = IniFile.Load(iniPath);
        var merged = IniFile.Load(iniPath);
        var installed = IniFile.Parse(installedText);
        var oldDefaults = oldDefaultsPath is not null && File.Exists(oldDefaultsPath) ? IniFile.Load(oldDefaultsPath) : null;

        foreach (var (section, key, value) in installed.Entries())
        {
            if (!defaults.Has(section, key))
            {
                record.DroppedIniOptions.Add($"[{section}] {key}");
                continue;
            }
            var changed = oldDefaults is not null
                ? !string.Equals(oldDefaults.Get(section, key), value, StringComparison.Ordinal)
                : manifest.IniChanges.Any(c => c.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && c.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            var newDefault = defaults.Get(section, key);
            if (!changed || string.Equals(newDefault, value, StringComparison.Ordinal)) continue;

            merged.Set(section, key, value);
            record.KeptIniValues.Add(new IniChange { Section = section, Key = key, Original = newDefault, Current = value });
            log.Write($"  ini [{section}] {key}: kept {value} (new default {newDefault})");
        }
        foreach (var (section, key, _) in defaults.Entries().Where(e => !installed.Has(e.Section, e.Key)))
            record.NewIniOptions.Add($"[{section}] {key}");
        merged.Save(iniPath);

        // The app's record of its own changes now measures against the new defaults.
        foreach (var change in manifest.IniChanges.ToList())
        {
            if (!defaults.Has(change.Section, change.Key))
            {
                manifest.IniChanges.Remove(change);
                continue;
            }
            change.Original = defaults.Get(change.Section, change.Key);
            change.Current = merged.Get(change.Section, change.Key);
            if (string.Equals(change.Original, change.Current, StringComparison.Ordinal)) manifest.IniChanges.Remove(change);
        }
        if (record.NewIniOptions.Count > 0) log.Write($"  ini: {record.NewIniOptions.Count} new setting(s) at their defaults");
        if (record.DroppedIniOptions.Count > 0) log.Write($"  ini: dropped {string.Join(", ", record.DroppedIniOptions)} (not in the new version)");
    }

    // ---------------- undo ----------------

    /// <summary>Puts back the files, OptiScaler.ini and manifest exactly as they were before the last update.</summary>
    public static InstallResult UndoUpdate(InstallPlan plan, InstallLog log)
    {
        var target = plan.TargetDir;
        log.Write($"UNDO UPDATE {plan.GameName} | in {target}");
        if (MoveLegacyState(target, log) is { } moveProblem) return Refuse(log, moveProblem);

        var manifest = ReadManifest(target);
        if (manifest is not { Removed: false, LastUpdate.UndoFolder: { } undoRelative }
            || !File.Exists(Path.Combine(target, undoRelative, ManifestName)))
            return Refuse(log, "There's no update to undo here.");
        if (!CanUndo(target)) return Refuse(log, "This game uses a DLSS 5 build of OptiScaler: use \"Switch back\" instead.");

        var undoDir = Path.Combine(target, undoRelative);
        try
        {
            var (problems, restored) = RestoreSnapshot(target, undoDir, manifest, log);
            if (problems.Count > 0)
                return new InstallResult { Message = $"Undo stopped part-way: {string.Join(", ", problems)} couldn't be put back. The saved copy is still in {undoRelative}, so you can try again." };

            TryDeleteFolder(undoDir);
            var message = $"OptiScaler {restored!.Version} is back, with its files and OptiScaler.ini exactly as before the update.";
            log.Write("  done: " + message);
            return new InstallResult { Success = true, Manifest = restored, Message = message };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Write($"  FAILED: {ex.Message}");
            return new InstallResult { Message = $"Undo failed: {ex.Message} The saved copy is still in {undoRelative}." };
        }
    }

    // ---------------- switch back (from a DLSS 5 build) ----------------

    /// <summary>
    /// Returns from a DLSS 5 build to the OptiScaler that was there before the switch: every file, OptiScaler.ini and
    /// the manifest come back byte for byte from the return point (the DLSS 5 build's files and the user's DLSS 5 file
    /// go). Settings the user changed while switched are carried over when the regular build has them too, so with no
    /// changes the folder ends up exactly as it was.
    /// </summary>
    public static InstallResult SwitchBack(InstallPlan plan, InstallLog log)
    {
        var target = plan.TargetDir;
        log.Write($"SWITCH BACK {plan.GameName} | in {target}");
        var manifest = ReadManifest(target);
        if (manifest is not { Removed: false, Switch: { } sw }) return Refuse(log, "This game isn't using a DLSS 5 build of OptiScaler.");
        var returnDir = Path.Combine(target, sw.ReturnFolder);
        if (!File.Exists(Path.Combine(returnDir, ManifestName))) return Refuse(log, $"The return point ({sw.ReturnFolder}) is missing, so nothing was changed.");

        var iniPath = Path.Combine(target, IniName);
        var iniNow = File.Exists(iniPath) ? IniFile.Load(iniPath) : null;
        var afterSwitchPath = Path.Combine(returnDir, IniAfterSwitchName);
        var iniAfterSwitch = File.Exists(afterSwitchPath) ? IniFile.Load(afterSwitchPath) : null;
        var buildUndo = manifest.LastUpdate?.UndoFolder;

        try
        {
            var (problems, restored) = RestoreSnapshot(target, returnDir, manifest, log);
            if (problems.Count > 0)
                return new InstallResult { Message = $"Switching back stopped part-way: {string.Join(", ", problems)} couldn't be put back. The return point is still in {sw.ReturnFolder}, so you can try again." };

            // The manifest file exactly as it was (the snapshot restore rewrote it with the same content).
            var originalManifest = Path.Combine(returnDir, OriginalManifestName);
            if (File.Exists(originalManifest)) File.Copy(originalManifest, Path.Combine(target, StateFolder, ManifestName), overwrite: true);

            // Settings changed while switched (compared with right after the switch), where the regular build has them.
            if (iniNow is not null && iniAfterSwitch is not null && File.Exists(iniPath))
            {
                var regular = IniFile.Load(iniPath);
                var carried = iniNow.Entries()
                    .Where(e => regular.Has(e.Section, e.Key) && !string.Equals(iniAfterSwitch.Get(e.Section, e.Key), e.Value, StringComparison.Ordinal))
                    .Select(e => new IniSetting(e.Section, e.Key, e.Value))
                    .ToList();
                if (carried.Count > 0)
                {
                    var ours = restored!.Added.FirstOrDefault(f => IsIni(f.Path)) is { } entry && Sha256(iniPath) == entry.Sha256;
                    ApplyIni(target, restored, carried, log, ours);
                    WriteManifest(target, restored);
                    log.Write($"  kept {carried.Count} setting(s) changed while switched");
                }
            }

            // An update of the DLSS 5 build made while switched had its own undo copy; the regular build can't use it.
            if (buildUndo is not null && buildUndo != restored!.LastUpdate?.UndoFolder) TryDeleteFolder(Path.Combine(target, buildUndo));
            TryDeleteFolder(returnDir);
            var message = $"OptiScaler {restored!.Version} is back, as it was before the switch to DLSS 5.";
            log.Write("  done: " + message);
            return new InstallResult { Success = true, Manifest = restored, Message = message };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.Write($"  FAILED: {ex.Message}");
            return new InstallResult { Message = $"Switching back failed: {ex.Message} The return point is still in {sw.ReturnFolder}." };
        }
    }

    /// <summary>
    /// Copies a user-supplied file (from the app's user-files folder) into the game under its known name, recorded in
    /// the manifest: Added, or Replaced with the game's copy backed up when the game already had one.
    /// </summary>
    private static void AddUserFile(string target, InstallManifest manifest, string from, string name, InstallLog log)
    {
        var path = Path.Combine(target, name);
        if (AllEntries(manifest).Any(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            // Already Upshift's (e.g. copied in before): refresh it with the user's current copy.
            var existing = AllEntries(manifest).First(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase));
            File.Copy(from, path, overwrite: true);
            Stamp(existing, path, Sha256(path));
        }
        else if (File.Exists(path))
        {
            var backupRelative = Path.Combine(manifest.BackupFolder, name);
            var backup = Path.Combine(target, backupRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(path, backup, overwrite: false);
            var originalHash = Sha256(path);
            File.Copy(from, path, overwrite: true);
            manifest.Replaced.Add(Stamp(new ManifestFile { Path = name, Backup = backupRelative, OriginalSha256 = originalHash }, path, Sha256(path)));
        }
        else
        {
            File.Copy(from, path, overwrite: false);
            manifest.Added.Add(Stamp(new ManifestFile { Path = name }, path, Sha256(path)));
        }
        if (Sha256(path) != Sha256(from)) throw new IOException($"{name} didn't copy correctly.");
        log.Write($"  added your {name}");
    }

    /// <summary>
    /// Makes the folder match the saved copy: removes what the update added, puts back game files it backed up, copies
    /// the saved files back byte for byte, then writes the saved manifest. Returns files that couldn't be put back.
    /// </summary>
    private static (List<string> Problems, InstallManifest? Restored) RestoreSnapshot(string target, string undoDir, InstallManifest current, InstallLog log)
    {
        var problems = new List<string>();
        var old = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(Path.Combine(undoDir, ManifestName)), Json)!;
        var snapshot = JsonSerializer.Deserialize<List<SnapshotFile>>(File.ReadAllText(Path.Combine(undoDir, SnapshotName)), Json) ?? new();
        var oldPaths = AllEntries(old).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var savedPaths = snapshot.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        void Try(string path, Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add(path); log.Write($"  couldn't put back {path}: {ex.Message}"); }
        }

        foreach (var file in current.Added.Where(f => !oldPaths.Contains(f.Path)))
            Try(file.Path, () =>
            {
                var path = Path.Combine(target, file.Path);
                if (File.Exists(path)) File.Delete(path);
                log.Write($"  undo: removed {file.Path}");
            });
        foreach (var file in current.Replaced.Where(f => !oldPaths.Contains(f.Path)))
            Try(file.Path, () =>
            {
                var path = Path.Combine(target, file.Path);
                var backup = Path.Combine(target, file.Backup!);
                if (!File.Exists(backup)) return;
                File.Copy(backup, path, overwrite: true);
                if (Sha256(path) != file.OriginalSha256) throw new IOException("the game's original didn't come back unchanged");
                File.Delete(backup);
                log.Write($"  undo: restored the game's {file.Path}");
            });
        // Listed before the update but missing then: missing again now.
        foreach (var path in oldPaths.Where(p => !savedPaths.Contains(p)))
            Try(path, () => { if (File.Exists(Path.Combine(target, path))) File.Delete(Path.Combine(target, path)); });

        foreach (var file in snapshot)
            Try(file.Path, () =>
            {
                var to = Path.Combine(target, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(Path.Combine(undoDir, "files", file.Path), to, overwrite: true);
                if (Sha256(to) != file.Sha256) throw new IOException("the saved copy didn't come back unchanged");
            });
        log.Write($"  undo: put back {snapshot.Count - problems.Count} saved file(s)");

        foreach (var folder in current.CreatedFolders.Where(f => !old.CreatedFolders.Contains(f, StringComparer.OrdinalIgnoreCase)).Reverse())
            TryRemoveEmptyFolder(Path.Combine(target, folder));

        if (problems.Count == 0)
        {
            WriteManifest(target, old);
            log.Write($"  undo: manifest is back to {old.Version}");
        }
        return (problems, old);
    }

    // ---------------- repair ----------------

    /// <summary>
    /// Copies back every release file that is missing or changed, from the same version, and recreates a missing
    /// OptiScaler.ini with this app's recorded settings. Changed files are kept in .upshift\repair… first. When the game
    /// has put a new copy of one of its own files where we had backed up the old one, that copy becomes the backup.
    /// </summary>
    public static InstallResult Repair(InstallPlan plan, InstallLog log)
    {
        var target = plan.TargetDir;
        var source = plan.SourceDir ?? "";
        log.Write($"REPAIR {plan.GameName} | {plan.ComponentId} {plan.Version} | in {target}");
        if (MoveLegacyState(target, log) is { } moveProblem) return Refuse(log, moveProblem);

        var manifest = ReadManifest(target);
        if (manifest is null or { Removed: true }) return Refuse(log, "OptiScaler isn't installed here by this app, so there's nothing to repair.");
        if (manifest.Version != plan.Version) return Refuse(log, $"The repair was prepared for {plan.Version}, but {manifest.Version} is installed.");
        if (!File.Exists(Path.Combine(source, "OptiScaler.dll"))) return Refuse(log, "The downloaded OptiScaler is missing OptiScaler.dll.");

        var release = FilesToCopy(source, manifest.ProxyName).ToDictionary(f => f.Relative, f => f.From, StringComparer.OrdinalIgnoreCase);
        var manifestBefore = JsonSerializer.Serialize(manifest, Json);
        var safetyRelative = Path.Combine(StateFolder, "repair" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var record = new RepairRecord { RepairedUtc = DateTime.UtcNow };
        var notes = new List<string>();

        try
        {
            foreach (var file in AllEntries(manifest).ToList())
            {
                var path = Path.Combine(target, file.Path);
                var exists = File.Exists(path);
                var broken = !exists || (!IsIni(file.Path) && !IsUserFile(file.Path) && HasChanged(file, path));
                if (!broken) continue;

                if (!release.TryGetValue(file.Path, out var from))
                {
                    // Supplied by the user through the options; it has to be added again from there.
                    if (!exists)
                    {
                        manifest.Added.Remove(file);
                        manifest.Replaced.Remove(file);
                        notes.Add($"{file.Path} is missing; add it again from the options");
                        log.Write($"  {file.Path} is missing and isn't part of the release; forgotten");
                    }
                    continue;
                }

                if (exists)
                {
                    var keep = Path.Combine(target, safetyRelative, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                    File.Copy(path, keep, overwrite: true);
                    record.SafetyFolder = safetyRelative;
                    var current = Sha256(path);
                    if (file.Backup is null && manifest.Added.Contains(file) && !IsOptiScalerDll(path))
                    {
                        // A file the install added has been replaced by something else (a game update or file check that
                        // now ships its own copy, say). It becomes the original: backed up, and put back on uninstall.
                        var backupRelative = Path.Combine(manifest.BackupFolder, file.Path);
                        var backup = Path.Combine(target, backupRelative);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(path, backup, overwrite: true);
                        if (Sha256(backup) != current) throw new IOException($"The backup of {file.Path} doesn't match.");
                        manifest.Added.Remove(file);
                        file.Backup = backupRelative;
                        file.OriginalSha256 = current;
                        manifest.Replaced.Add(file);
                        log.Write($"  {file.Path}: the copy found there is kept as the original (backed up, put back on uninstall)");
                    }
                    else if (file.Backup is not null && current != file.OriginalSha256 && !IsOptiScalerDll(path))
                    {
                        // The game put a new copy of its own file here (a game update, say): that's what Uninstall should put back now.
                        var backup = Path.Combine(target, file.Backup);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(path, backup, overwrite: true);
                        if (Sha256(backup) != current) throw new IOException($"The backup of {file.Path} doesn't match.");
                        file.OriginalSha256 = current;
                        log.Write($"  {file.Path}: the game's newer copy is now the backup");
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(from, path, overwrite: true);
                if (IsIni(file.Path) && manifest.IniChanges.Count > 0)
                {
                    var ini = IniFile.Load(path);
                    foreach (var change in manifest.IniChanges.Where(c => c.Current is not null && ini.Has(c.Section, c.Key)))
                        ini.Set(change.Section, change.Key, change.Current!);
                    ini.Save(path);
                }
                else if (Sha256(path) != Sha256(from)) throw new IOException($"{file.Path} didn't copy correctly.");
                Stamp(file, path, Sha256(path));
                record.Fixed.Add(file.Path);
                log.Write($"  repaired {file.Path} ({(exists ? "changed" : "missing")})");
            }

            if (record.Fixed.Count == 0 && notes.Count == 0)
                return new InstallResult { Success = true, Manifest = manifest, Message = "Nothing needed repairing." };

            manifest.LastRepair = record;
            WriteManifest(target, manifest);
            var message = record.Fixed.Count > 0
                ? $"Repaired {record.Fixed.Count} file(s) of OptiScaler {manifest.Version}: {string.Join(", ", record.Fixed)}. Settings are unchanged."
                : "No OptiScaler files needed repairing.";
            if (notes.Count > 0) message += " " + string.Join(". ", notes) + ".";
            log.Write("  done: " + message);
            return new InstallResult { Success = true, Manifest = manifest, Message = message };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"  FAILED: {ex.Message}");
            try { File.WriteAllText(Path.Combine(target, StateFolder, ManifestName), manifestBefore); }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            return new InstallResult { Message = $"The repair stopped part-way: {ex.Message} Files already repaired are correct copies; you can try again." };
        }
    }

    /// <summary>Creates the file's folder if needed and records each new level in the manifest.</summary>
    private static void CreateFolderFor(string target, string file, InstallManifest manifest)
    {
        var folder = Path.GetDirectoryName(file)!;
        if (Directory.Exists(folder)) return;
        var missing = new Stack<string>();
        for (var f = folder; !Directory.Exists(f); f = Path.GetDirectoryName(f)!) missing.Push(f);
        Directory.CreateDirectory(folder);
        foreach (var created in missing) manifest.CreatedFolders.Add(Path.GetRelativePath(target, created));
    }

    private static void TryDeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
