using System.Text.Json;
using System.Text.Json.Serialization;
using Upshift.Core.Store;

namespace Upshift.Core.Install;

/// <summary>What a store entry did in one game: every file, folder and ini edit, so taking it out reverses it exactly.</summary>
public sealed class StoreGameRecord
{
    public List<StoreInstalled> Entries { get; set; } = new();
}

public sealed class StoreInstalled
{
    public string EntryId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>The release tag(s) used ("FG-V.B15"; several downloads joined with " + ").</summary>
    public string Version { get; set; } = "";
    public DateTimeOffset? Released { get; set; }
    public DateTime AddedUtc { get; set; }
    /// <summary>The entry as it was installed, so it can be taken out even after store.json changes.</summary>
    public StoreEntry? Entry { get; set; }
    public List<StoreFileRecord> Files { get; set; } = new();
    /// <summary>Folders made for it (relative to the game's exe folder), removed again with everything in them.</summary>
    public List<string> Folders { get; set; } = new();
    public List<StoreIniRecord> Inis { get; set; } = new();
    public string? Message { get; set; }
}

public enum StoreFileKind { Added, Replaced, Override, Removed, DataFile }

public sealed class StoreFileRecord
{
    /// <summary>Relative to the game's exe folder, or to Upshift's store data folder for DataFile.</summary>
    public string Path { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StoreFileKind Kind { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>Replaced/Removed: the game's original, relative to the exe folder; Override: OptiScaler's own copy.</summary>
    public string? Backup { get; set; }
    public string? OriginalSha256 { get; set; }
}

public sealed class StoreIniRecord
{
    /// <summary>Relative to the game's exe folder, or "data:…" for Upshift's store data folder.</summary>
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    /// <summary>The whole file as it was, put back byte for byte when nobody changed it since.</summary>
    public string? Backup { get; set; }
    public string? ResultSha256 { get; set; }
    public List<StoreIniKey> Keys { get; set; } = new();
}

public sealed class StoreIniKey
{
    public string Section { get; set; } = "";
    public string Key { get; set; } = "";
    public string? Original { get; set; }
}

/// <summary>The card and game a requirement is checked against, sent with the plan.</summary>
public sealed class StoreContext
{
    public string? GpuVendor { get; set; }
    public string? GpuGeneration { get; set; }
    public string Api { get; set; } = "";
    public string? TestBuildComponentId { get; set; }
}

public static partial class OptiScalerInstaller
{
    public const string StoreRecordName = "store.json";
    private const string StoreBackupFolder = "store-backup";
    private static readonly string[] NvidiaSigner = { "NVIDIA Corporation" };

    public static string StoreRecordPath(string target) => Path.Combine(target, StateFolder, StoreRecordName);

    public static StoreGameRecord ReadStoreRecord(string target)
    {
        try
        {
            var path = StoreRecordPath(target);
            return File.Exists(path) ? JsonSerializer.Deserialize<StoreGameRecord>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    private static void WriteStoreRecord(string target, StoreGameRecord record)
    {
        var path = StoreRecordPath(target);
        if (record.Entries.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(record, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public static StoreDataDir StoreData(string dataDir) => new(Path.Combine(dataDir, "store"));
    public readonly record struct StoreDataDir(string Root) { public string Entry(string id) => Path.Combine(Root, id); }

    /// <summary>OptiScaler's DLL folder inside the exe folder (OptiDllPath; auto = "OptiScaler" when that folder exists), else the exe folder.</summary>
    public static string OptiScalerFolder(string target)
    {
        var iniPath = Path.Combine(target, IniName);
        if (File.Exists(iniPath))
        {
            try { return Path.Combine(target, Path.GetDirectoryName(Path.GetDirectoryName(Anr.FolderRelative(target, IniFile.Load(iniPath)))!)!); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        }
        return Directory.Exists(Path.Combine(target, "OptiScaler")) ? Path.Combine(target, "OptiScaler") : target;
    }

    /// <summary>Why the entry's requirements aren't met here, or null.</summary>
    public static string? StoreRequirementProblem(StoreEntry entry, string target, StoreContext ctx)
    {
        var manifest = ReadManifest(target);
        foreach (var value in (entry.Recipe ?? new()).Where(s => s.Type == "requires").SelectMany(s => s.Values ?? new()))
        {
            if (value == "OptiScaler from Upshift" && manifest is not { Removed: false }) return "Needs OptiScaler installed by Upshift.";
            if (value == "OptiScaler test build" && (manifest is not { Removed: false } || ctx.TestBuildComponentId is null || manifest.ComponentId != ctx.TestBuildComponentId))
                return "Needs an OptiScaler test build in this game.";
            if (value.StartsWith("GPU:"))
            {
                var parts = value.Split(':');
                if (!string.Equals(ctx.GpuVendor, parts[1], StringComparison.OrdinalIgnoreCase)) return $"Needs a {parts[1].ToUpperInvariant()} graphics card.";
                if (parts.Length > 2 && !parts[2].Split('|').Any(g => ctx.GpuGeneration?.StartsWith(g, StringComparison.OrdinalIgnoreCase) == true))
                    return $"Needs a {parts[2].Replace("|", " or ")} card.";
            }
            else if (value.Contains("DX") || value.Contains("Vulkan"))
            {
                if (!value.Split('|').Any(a => ctx.Api.Contains(a switch { "DX12" => "D3D12", "DX11" => "D3D11", _ => a }, StringComparison.OrdinalIgnoreCase)))
                    return $"Needs a {value.Replace("|", " or ")} game.";
            }
        }
        return null;
    }

    // ---------------- add ----------------

    /// <summary>
    /// Adds a recipe entry (or user-supplied file) to a game, or changes the version there: every step checked first, then done
    /// with an undo step each. A file of OptiScaler's that it replaces is kept (an override, like the user's FSR 4 file), a game
    /// file is backed up, every ini it edits is backed up whole. Anything that fails puts the folder back as it was. On a version
    /// change the files the user may edit (a copy step's "keep") and whatever the add-on wrote into its own folders (logs) stay.
    /// </summary>
    public static InstallResult StoreAdd(InstallPlan plan, string dataDir, InstallLog log)
    {
        var target = plan.TargetDir;
        if (plan.StoreEntryJson is null || JsonSerializer.Deserialize<StoreEntry>(plan.StoreEntryJson, Json) is not { } entry)
            return Refuse(log, "The store entry is missing.");
        log.Write($"STORE {plan.GameName} | add {entry.Id} {plan.Version} | in {target}");
        if (StoreCatalog.Problems(entry).FirstOrDefault() is { } bad) return Refuse(log, $"{entry.Name} can't be used: {bad}.");
        if (entry.Type is not (StoreTypes.Recipe or StoreTypes.UserSupplied)) return Refuse(log, $"{entry.Name} isn't installed by a recipe.");
        if (RunningFrom(target).FirstOrDefault() is { } running) return Refuse(log, $"Close the game first ({running} is running); nothing was changed.");
        if (StoreRequirementProblem(entry, target, plan.StoreContext ?? new()) is { } unmet) return Refuse(log, unmet);
        foreach (var source in plan.StoreSources.Values)
            if (!IsUnder(source, dataDir)) return Refuse(log, "The downloaded files aren't in Upshift's folder.");
        if (entry.Type == StoreTypes.UserSupplied && (plan.AddFileFrom is null || !File.Exists(plan.AddFileFrom)
                || !Path.GetFileName(plan.AddFileFrom).Equals(entry.UserFile!.FileName, StringComparison.OrdinalIgnoreCase)))
            return Refuse(log, $"Choose your {entry.UserFile?.FileName} first.");

        var record = ReadStoreRecord(target);
        var old = record.Entries.FirstOrDefault(e => e.EntryId == entry.Id);
        var recordBefore = JsonSerializer.Serialize(record, Json);
        var manifestBefore = ReadManifest(target) is { } m0 ? JsonSerializer.Serialize(m0, Json) : null;

        // A version change: what the user may keep is read first, then the old version comes out.
        var carry = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (old is not null)
        {
            foreach (var keep in KeepPaths(old, target))
                if (File.Exists(Path.Combine(target, keep.Path)) && Sha256(Path.Combine(target, keep.Path)) != keep.Sha256)
                    carry[keep.Path] = File.ReadAllBytes(Path.Combine(target, keep.Path));
            foreach (var folder in old.Folders)
            {
                var dir = Path.Combine(target, folder);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(target, file);
                    if (!old.Files.Any(f => f.Path.Equals(rel, StringComparison.OrdinalIgnoreCase)) && !carry.ContainsKey(rel))
                        carry[rel] = File.ReadAllBytes(file);   // written by the add-on itself (logs)
                }
            }
            var removed = StoreRemoveEntry(target, dataDir, old, log);
            if (removed is not null) return Refuse(log, $"The installed {entry.Name} couldn't be taken out first: {removed}");
            record = ReadStoreRecord(target);
        }

        var undo = new Stack<Action>();
        var installed = new StoreInstalled
        {
            EntryId = entry.Id, Name = entry.Name, Type = entry.Type, Version = plan.Version ?? "", Released = plan.Released,
            AddedUtc = DateTime.UtcNow, Entry = entry
        };
        try
        {
            var places = new Places(target, dataDir, entry.Id, ReadManifest(target)?.ProxyName);
            if (entry.Type == StoreTypes.UserSupplied)
                PutFile(plan.AddFileFrom!, places.Resolve(entry.UserFile!.To + "\\" + entry.UserFile.FileName), target, dataDir, entry, installed, undo, log, userFile: true);

            foreach (var step in entry.Recipe ?? new())
            {
                switch (step.Type)
                {
                    case "copy":
                    {
                        var (downloadId, inside) = (step.From!.Split(':', 2)[0], step.From.Split(':', 2)[1]);
                        if (!plan.StoreSources.TryGetValue(downloadId, out var root)) throw new IOException($"The download \"{downloadId}\" is missing.");
                        var from = Path.Combine(root, inside);
                        var to = places.Resolve(step.To!);
                        if (File.Exists(from)) PutFile(from, to, target, dataDir, entry, installed, undo, log);
                        else if (Directory.Exists(from))
                            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                                PutFile(file, Path.Combine(to, Path.GetRelativePath(from, file)), target, dataDir, entry, installed, undo, log);
                        else throw new IOException($"{inside} isn't in the download.");
                        break;
                    }
                    case "rename":
                    {
                        var from = places.Resolve(step.File!);
                        var to = Path.Combine(Path.GetDirectoryName(from)!, places.Name(step.Name!));
                        RenameFile(from, to, target, dataDir, entry, installed, undo, log);
                        break;
                    }
                    case "ini-set":
                    {
                        var path = places.Resolve(step.File!);
                        SetIni(path, step.Section!, step.Key!, places.Value(step.Value!), target, dataDir, entry, installed, undo, log);
                        break;
                    }
                    case "message":
                        installed.Message = installed.Message is null ? step.Text : installed.Message + " " + step.Text;
                        break;
                }
            }

            // Version change: the user's edited files and the add-on's own files come back.
            foreach (var (rel, bytes) in carry)
            {
                var path = Path.Combine(target, rel);
                var isOurs = installed.Files.Any(f => f.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));
                if (isOurs || installed.Folders.Any(f => rel.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, bytes);
                    if (isOurs) log.Write($"  kept your {rel}");
                }
            }
            foreach (var ini in installed.Inis) ini.ResultSha256 = File.Exists(IniFull(ini.Path, target, dataDir)) ? Sha256(IniFull(ini.Path, target, dataDir)) : null;

            record.Entries.RemoveAll(e => e.EntryId == entry.Id);
            record.Entries.Add(installed);
            WriteStoreRecord(target, record);
            var entryData = StoreData(dataDir).Entry(entry.Id);
            if (Directory.Exists(entryData))
                foreach (var temp in Directory.EnumerateFiles(entryData, "*.upshift-undo", SearchOption.AllDirectories).ToList())
                    try { File.Delete(temp); } catch (IOException) { }
            log.Write($"  {entry.Name} {plan.Version}: {installed.Files.Count} file(s)" + (old is null ? "" : $" (was {old.Version})"));
            return new InstallResult
            {
                Success = true, Message = (old is null ? $"{entry.Name} added." : $"{entry.Name} is now {plan.Version}.") + (installed.Message is null ? "" : " " + installed.Message)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.Write($"  FAILED: {ex.Message} - putting things back");
            foreach (var step in undo)
                try { step(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Write($"  could not fully put back: {e.Message}"); }
            try
            {
                if (manifestBefore is not null) File.WriteAllText(Path.Combine(target, StateFolder, ManifestName), manifestBefore);
                File.WriteAllText(StoreRecordPath(target), recordBefore);
                if (ReadStoreRecord(target).Entries.Count == 0) File.Delete(StoreRecordPath(target));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.Write($"  could not fully put back the records: {e.Message}"); }
            return new InstallResult { Message = $"{entry.Name} couldn't be added: {ex.Message}" + (old is null ? "" : " The previous version was taken out; add it again from the Store.") };
        }
    }

    private static IEnumerable<StoreFileRecord> KeepPaths(StoreInstalled old, string target)
    {
        if (old.Entry?.Recipe is not { } recipe) yield break;
        var places = new Places(target, "", old.EntryId, null);
        foreach (var step in recipe.Where(s => s.Type == "copy" && s.Keep is { Count: > 0 }))
        {
            string to;
            try { to = places.Resolve(step.To!); } catch (InvalidOperationException) { continue; }
            foreach (var keep in step.Keep!)
            {
                var rel = Path.GetRelativePath(target, Path.Combine(to, keep));
                if (old.Files.FirstOrDefault(f => f.Path.Equals(rel, StringComparison.OrdinalIgnoreCase)) is { } file) yield return file;
            }
        }
    }

    /// <summary>Placeholders resolved to real paths that can only be inside the game's exe folder or Upshift's store data folder.</summary>
    private sealed class Places
    {
        private readonly string _target, _data, _entry, _optiFolder;
        private readonly string? _proxy;
        public Places(string target, string dataDir, string entryId, string? proxy)
        {
            _target = Path.GetFullPath(target);
            _data = dataDir.Length == 0 ? "" : StoreData(dataDir).Root;
            _entry = dataDir.Length == 0 ? "" : StoreData(dataDir).Entry(entryId);
            _optiFolder = OptiScalerFolder(target);
            _proxy = proxy;
        }

        public string Resolve(string path)
        {
            string full;
            if (path.StartsWith("{GameFolder}")) full = Path.GetFullPath(_target + path["{GameFolder}".Length..]);
            else if (path.StartsWith("{OptiScalerFolder}")) full = Path.GetFullPath(_optiFolder + path["{OptiScalerFolder}".Length..]);
            else if (path.StartsWith("{UpshiftData}") && _data.Length > 0) full = Path.GetFullPath(_data + path["{UpshiftData}".Length..]);
            else throw new InvalidOperationException($"{path} isn't in the game folder or Upshift's folder.");
            if (!IsUnder(full, _target) && !(_entry.Length > 0 && IsUnder(full, _entry)))
                throw new InvalidOperationException($"{path} points outside the game folder and Upshift's folder.");
            return full;
        }

        public string Name(string name) => name == "{ProxyName}"
            ? _proxy ?? throw new InvalidOperationException("OptiScaler's loading name isn't known: install OptiScaler with Upshift first.")
            : name;

        public string Value(string value) => value.Replace("{GameFolder}", _target).Replace("{UpshiftData}", _data).Replace("{EntryFolder}", _entry);
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        var r = Path.GetFullPath(root).TrimEnd('\\');
        return full.Equals(r, StringComparison.OrdinalIgnoreCase) || full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string IniFull(string recorded, string target, string dataDir) =>
        recorded.StartsWith("data:") ? Path.Combine(StoreData(dataDir).Root, recorded[5..]) : Path.Combine(target, recorded);

    /// <summary>Copies one file into place, recording how: added, over a game file (backed up), or over OptiScaler's own file (kept).</summary>
    private static void PutFile(string from, string to, string target, string dataDir, StoreEntry entry, StoreInstalled installed,
        Stack<Action> undo, InstallLog log, bool userFile = false)
    {
        var name = Path.GetFileName(to);
        if (!userFile)
        {
            if (StoreCatalog.IsNeverDownloaded(name) || StoreCatalog.IsNeverDownloaded(from))
                throw new InvalidOperationException($"{name} is always supplied by the user, so {entry.Name} wasn't added.");
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && SignatureCheck.Problem(from, NvidiaSigner) is null)
                throw new InvalidOperationException($"{name} is NVIDIA's own file, which Upshift never downloads, so {entry.Name} wasn't added.");
        }

        if (IsUnder(to, StoreData(dataDir).Root))
        {
            // Upshift's own folder for the entry: shared by every game using it, removed when the last one goes.
            var rel = Path.GetRelativePath(StoreData(dataDir).Root, to);
            var existed = File.Exists(to);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (!existed || Sha256(to) != Sha256(from))
            {
                var keep = existed ? to + ".upshift-undo" : null;
                if (keep is not null) File.Copy(to, keep, true);
                File.Copy(from, to, true);
                undo.Push(() => { if (keep is not null) { File.Copy(keep, to, true); File.Delete(keep); } else if (File.Exists(to)) File.Delete(to); });
            }
            installed.Files.Add(new StoreFileRecord { Path = rel, Kind = StoreFileKind.DataFile, Sha256 = Sha256(to) });
            AddUser(dataDir, entry.Id, target, undo);
            return;
        }

        var relative = Path.GetRelativePath(target, to);
        if (installed.Files.Any(f => f.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{entry.Name}'s recipe writes {relative} twice.");
        if (ReadStoreRecord(target).Entries.Any(e => e.EntryId != entry.Id && e.Files.Any(f => f.Path.Equals(relative, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException($"{relative} belongs to another add-on from the Store; take that out first.");

        MakeFolders(Path.GetDirectoryName(to)!, target, installed, undo);
        var manifest = ReadManifest(target);
        var optiFile = manifest is { Removed: false } && AllEntries(manifest).Any(f => f.Path.Equals(relative, StringComparison.OrdinalIgnoreCase));
        if (File.Exists(to) && optiFile)
        {
            if (manifest!.OverrideFor(relative) is not null) throw new InvalidOperationException($"{relative} is already replaced by another file; turn that off first.");
            // OptiScaler's own file: kept in .upshift, put back when the entry comes out (and by OptiScaler's own uninstall).
            var backupRel = Path.Combine(StateFolder, "overrides", "store", entry.Id, relative);
            var backup = Path.Combine(target, backupRel);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(to, backup, true);
            var backupHash = Sha256(backup);
            undo.Push(() => { File.Copy(backup, to, true); File.Delete(backup); });
            File.Copy(from, to, true);
            var hash = Sha256(to);
            manifest.Overrides ??= new List<FileOverride>();
            manifest.Overrides.Add(new FileOverride
            {
                Path = relative, Kind = "store:" + entry.Id, Sha256 = hash, Version = Detection.FileVersions.Read(to), Backup = backupRel, BackupSha256 = backupHash, AddedUtc = DateTime.UtcNow
            });
            WriteManifest(target, manifest);
            installed.Files.Add(new StoreFileRecord { Path = relative, Kind = StoreFileKind.Override, Sha256 = hash, Backup = backupRel, OriginalSha256 = backupHash });
            log.Write($"  {relative}: OptiScaler's copy kept in {backupRel}");
        }
        else if (File.Exists(to))
        {
            var backupRel = Path.Combine(StateFolder, StoreBackupFolder, entry.Id, relative);
            var backup = Path.Combine(target, backupRel);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(to, backup, true);
            var original = Sha256(backup);
            undo.Push(() => { File.Copy(backup, to, true); File.Delete(backup); });
            File.Copy(from, to, true);
            installed.Files.Add(new StoreFileRecord { Path = relative, Kind = StoreFileKind.Replaced, Sha256 = Sha256(to), Backup = backupRel, OriginalSha256 = original });
            log.Write($"  {relative}: the game's copy backed up in {backupRel}");
        }
        else
        {
            File.Copy(from, to, false);
            undo.Push(() => { if (File.Exists(to)) File.Delete(to); });
            installed.Files.Add(new StoreFileRecord { Path = relative, Kind = StoreFileKind.Added, Sha256 = Sha256(to) });
        }
    }

    private static void MakeFolders(string folder, string target, StoreInstalled installed, Stack<Action> undo)
    {
        var made = new List<string>();
        for (var dir = folder; !Directory.Exists(dir) && IsUnder(dir, target) && !string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
             dir = Path.GetDirectoryName(dir)!)
            made.Insert(0, dir);
        foreach (var dir in made)
        {
            Directory.CreateDirectory(dir);
            undo.Push(() => { if (Directory.Exists(dir)) Directory.Delete(dir, true); });
            installed.Folders.Add(Path.GetRelativePath(target, dir));
        }
    }

    private static void RenameFile(string from, string to, string target, string dataDir, StoreEntry entry, StoreInstalled installed, Stack<Action> undo, InstallLog log)
    {
        var fromRel = Path.GetRelativePath(target, from);
        var record = installed.Files.FirstOrDefault(f => f.Path.Equals(fromRel, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException($"{fromRel} wasn't copied by this recipe.");
        if (record.Kind != StoreFileKind.Added) throw new InvalidOperationException($"Only a newly copied file can be renamed ({fromRel}).");
        // The copied file goes to its new name like any other copy, then the temporary name is taken away.
        var temp = from + ".upshift-rename";
        File.Move(from, temp);
        installed.Files.Remove(record);
        undo.Push(() => { if (File.Exists(temp)) File.Move(temp, from, true); });
        PutFile(temp, to, target, dataDir, entry, installed, undo, log);
        File.Delete(temp);
        log.Write($"  {fromRel} -> {Path.GetFileName(to)}");
    }

    private static void SetIni(string path, string section, string key, string value, string target, string dataDir, StoreEntry entry,
        StoreInstalled installed, Stack<Action> undo, InstallLog log)
    {
        var inData = IsUnder(path, StoreData(dataDir).Root);
        var recorded = inData ? "data:" + Path.GetRelativePath(StoreData(dataDir).Root, path) : Path.GetRelativePath(target, path);
        var iniRecord = installed.Inis.FirstOrDefault(i => i.Path.Equals(recorded, StringComparison.OrdinalIgnoreCase));
        if (iniRecord is null)
        {
            iniRecord = new StoreIniRecord { Path = recorded, Existed = File.Exists(path) };
            if (iniRecord.Existed)
            {
                var backupRel = inData ? null : Path.Combine(StateFolder, StoreBackupFolder, entry.Id, "ini", recorded);
                var backup = backupRel is null ? path + ".upshift-store-" + entry.Id : Path.Combine(target, backupRel);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(path, backup, true);
                iniRecord.Backup = backupRel ?? "data:" + Path.GetRelativePath(StoreData(dataDir).Root, backup);
                var bytes = File.ReadAllBytes(path);
                undo.Push(() => { File.WriteAllBytes(path, bytes); if (File.Exists(backup)) File.Delete(backup); });
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "");
                undo.Push(() => { if (File.Exists(path)) File.Delete(path); });
            }
            installed.Inis.Add(iniRecord);
        }
        var ini = IniFile.Load(path);
        if (!iniRecord.Keys.Any(k => k.Section.Equals(section, StringComparison.OrdinalIgnoreCase) && k.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            iniRecord.Keys.Add(new StoreIniKey { Section = section, Key = key, Original = ini.Get(section, key) });
        ini.Set(section, key, value);
        ini.Save(path);
        log.Write($"  {recorded}: [{section}] {key}={value}");
    }

    private static void AddUser(string dataDir, string entryId, string target, Stack<Action> undo)
    {
        var path = Path.Combine(StoreData(dataDir).Entry(entryId), ".games.json");
        var games = ReadUsers(path);
        if (games.Contains(target, StringComparer.OrdinalIgnoreCase)) return;
        games.Add(target);
        File.WriteAllText(path, JsonSerializer.Serialize(games));
        undo.Push(() => { var g = ReadUsers(path); g.RemoveAll(x => x.Equals(target, StringComparison.OrdinalIgnoreCase)); File.WriteAllText(path, JsonSerializer.Serialize(g)); });
    }

    private static List<string> ReadUsers(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    // ---------------- remove ----------------

    /// <summary>Takes a store entry out of a game: exactly as before it was added.</summary>
    public static InstallResult StoreRemove(InstallPlan plan, string dataDir, InstallLog log)
    {
        var target = plan.TargetDir;
        log.Write($"STORE {plan.GameName} | remove {plan.StoreEntryId} | in {target}");
        var record = ReadStoreRecord(target);
        if (record.Entries.FirstOrDefault(e => e.EntryId == plan.StoreEntryId) is not { } installed)
            return Refuse(log, "Upshift didn't add that here, so nothing was removed.");
        if (RunningFrom(target).FirstOrDefault() is { } running) return Refuse(log, $"Close the game first ({running} is running); nothing was changed.");
        var problem = StoreRemoveEntry(target, dataDir, installed, log);
        return problem is null
            ? new InstallResult { Success = true, Message = $"{installed.Name} removed. The game is back as it was before." }
            : new InstallResult { Message = $"{installed.Name} couldn't be removed completely: {problem}" };
    }

    /// <summary>Every store entry out of a game (before OptiScaler's own uninstall). Null when all went.</summary>
    public static string? StoreRemoveAll(string target, string dataDir, InstallLog log)
    {
        var problems = new List<string>();
        foreach (var installed in Enumerable.Reverse(ReadStoreRecord(target).Entries).ToList())
            if (StoreRemoveEntry(target, dataDir, installed, log) is { } p) problems.Add($"{installed.Name}: {p}");
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    /// <summary>Null when done. Each file is tried on its own; the record keeps exactly what's left.</summary>
    private static string? StoreRemoveEntry(string target, string dataDir, StoreInstalled installed, InstallLog log)
    {
        var problems = new List<string>();
        var manifest = ReadManifest(target);

        // Inis first (they may sit in folders that go next): byte for byte when untouched since, else key by key.
        foreach (var ini in Enumerable.Reverse(installed.Inis).ToList())
        {
            var path = IniFull(ini.Path, target, dataDir);
            try
            {
                var backup = ini.Backup is null ? null : IniFull(ini.Backup, target, dataDir);
                if (!ini.Existed) { if (File.Exists(path)) File.Delete(path); }
                else if (File.Exists(path) && ini.ResultSha256 is not null && Sha256(path) == ini.ResultSha256 && backup is not null && File.Exists(backup))
                    File.Copy(backup, path, true);
                else if (File.Exists(path))
                {
                    var file = IniFile.Load(path);
                    foreach (var key in ini.Keys) if (key.Original is not null) file.Set(key.Section, key.Key, key.Original);
                    file.Save(path);
                    log.Write($"  {ini.Path} changed since; the values were put back one by one");
                }
                if (backup is not null && File.Exists(backup)) File.Delete(backup);
                installed.Inis.Remove(ini);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{ini.Path}: {ex.Message}"); }
        }

        foreach (var file in Enumerable.Reverse(installed.Files).ToList())
        {
            try
            {
                switch (file.Kind)
                {
                    case StoreFileKind.Added:
                        var added = Path.Combine(target, file.Path);
                        if (File.Exists(added)) File.Delete(added);
                        break;
                    case StoreFileKind.Replaced:
                    case StoreFileKind.Removed:
                        var path = Path.Combine(target, file.Path);
                        var backup = Path.Combine(target, file.Backup!);
                        if (!File.Exists(backup)) throw new IOException($"the backup of {file.Path} is missing");
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.Copy(backup, path, true);
                        if (Sha256(path) != file.OriginalSha256) throw new IOException($"{file.Path} didn't come back unchanged");
                        File.Delete(backup);
                        break;
                    case StoreFileKind.Override:
                        var live = Path.Combine(target, file.Path);
                        var kept = Path.Combine(target, file.Backup!);
                        if (File.Exists(kept))
                        {
                            File.Copy(kept, live, true);
                            if (Sha256(live) != file.OriginalSha256) throw new IOException($"OptiScaler's {file.Path} didn't come back unchanged");
                            File.Delete(kept);
                        }
                        if (manifest?.Overrides?.FirstOrDefault(o => o.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase) && o.Kind == "store:" + installed.EntryId) is { } over)
                        {
                            manifest.Overrides.Remove(over);
                            if (manifest.Overrides.Count == 0) manifest.Overrides = null;
                        }
                        break;
                    case StoreFileKind.DataFile:
                        break;   // shared: handled below
                }
                installed.Files.Remove(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{file.Path}: {ex.Message}"); }
        }
        if (manifest is not null) WriteManifest(target, manifest);

        // Folders made for it, with whatever the add-on wrote into them (logs).
        foreach (var folder in installed.Folders.OrderByDescending(f => f.Length).ToList())
        {
            var dir = Path.Combine(target, folder);
            try
            {
                if (Directory.Exists(dir) && IsUnder(dir, target)) Directory.Delete(dir, recursive: true);
                installed.Folders.Remove(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{folder}: {ex.Message}"); }
        }
        // Its backup folder in .upshift.
        var storeBackup = Path.Combine(target, StateFolder, StoreBackupFolder, installed.EntryId);
        var overrideBackup = Path.Combine(target, StateFolder, "overrides", "store", installed.EntryId);
        foreach (var dir in new[] { storeBackup, overrideBackup })
            try { if (Directory.Exists(dir) && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any()) Directory.Delete(dir, true); }
            catch (IOException) { }
        foreach (var dir in new[] { Path.Combine(target, StateFolder, StoreBackupFolder), Path.Combine(target, StateFolder, "overrides", "store"), Path.Combine(target, StateFolder, "overrides") })
            TryRemoveEmptyFolder(dir);

        // The shared data folder goes when no game uses the entry any more.
        if (dataDir.Length > 0)
        {
            var entryDir = StoreData(dataDir).Entry(installed.EntryId);
            var users = Path.Combine(entryDir, ".games.json");
            var games = ReadUsers(users);
            games.RemoveAll(g => g.Equals(target, StringComparison.OrdinalIgnoreCase));
            try
            {
                if (games.Count == 0) { if (Directory.Exists(entryDir)) Directory.Delete(entryDir, true); }
                else File.WriteAllText(users, JsonSerializer.Serialize(games));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"Upshift's folder for it: {ex.Message}"); }
            installed.Files.RemoveAll(f => f.Kind == StoreFileKind.DataFile);
        }

        var record = ReadStoreRecord(target);
        record.Entries.RemoveAll(e => e.EntryId == installed.EntryId);
        if (problems.Count > 0) record.Entries.Add(installed);   // what's left, so a retry finishes
        WriteStoreRecord(target, record);
        TryRemoveEmptyFolder(Path.Combine(target, StateFolder));
        log.Write(problems.Count == 0 ? $"  {installed.Name} removed" : $"  {installed.Name}: {string.Join("; ", problems)}");
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }
}
