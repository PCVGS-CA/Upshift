using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using Upshift.Core.Store;

namespace Upshift.Core.Install;

/// <summary>
/// "author-setup" store entries (Experimental only): the author's own setup program, run unelevated after the user confirms,
/// between two snapshots of the game's exe folder and Upshift's folder for the entry. Every file it added, changed or removed
/// there is recorded (changed and removed ones with backups), so taking it out reverses it.
///
/// Anything outside those two folders is watched too, as far as that can be seen without a driver: new top-level folders and
/// files in the user's AppData, LocalAppData, ProgramData, profile, Desktop, Start menu, Program Files and System32, and new
/// keys and values in the registry's HKCU\Software (two levels), HKLM\Software, Run and Environment. When the setup made any
/// of those, everything it did in the two folders is undone, the new things it made outside are removed where a user can
/// remove them, and the entry is marked "can't be managed safely" on this PC. Changes to things that already existed outside
/// can't be seen this way; the notice before running says so.
/// </summary>
public static class AuthorSetup
{
    public const long SnapshotLimit = 4L * 1024 * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string BlockedPath(string dataDir) => Path.Combine(OptiScalerInstaller.StoreData(dataDir).Root, "blocked.json");

    /// <summary>Entries marked "can't be managed safely" on this PC, with why.</summary>
    public static Dictionary<string, string> Blocked(string dataDir)
    {
        try
        {
            var path = BlockedPath(dataDir);
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }

    private static void Block(string dataDir, string id, string why)
    {
        var all = Blocked(dataDir);
        all[id] = why;
        Directory.CreateDirectory(Path.GetDirectoryName(BlockedPath(dataDir))!);
        File.WriteAllText(BlockedPath(dataDir), JsonSerializer.Serialize(all, Json));
    }

    /// <summary>The exact program and arguments that would run, for the confirmation.</summary>
    public static (string Program, string Arguments) Command(StoreEntry entry, string sourceFolder, string target, string dataDir)
    {
        var entryFolder = OptiScalerInstaller.StoreData(dataDir).Entry(entry.Id);
        var exe = Path.GetFullPath(Path.Combine(sourceFolder, entry.Setup!.Run));
        var args = string.Join(" ", entry.Setup.Args.Select(a => Quote(a.Replace("{GameFolder}", target).Replace("{EntryFolder}", entryFolder))));
        return (exe, args);
    }

    private static string Quote(string arg) => arg.Contains(' ') || arg.Contains('"') ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;

    public static InstallResult Run(InstallPlan plan, string dataDir, InstallLog log)
    {
        var target = plan.TargetDir;
        if (plan.StoreEntryJson is null || JsonSerializer.Deserialize<StoreEntry>(plan.StoreEntryJson, new JsonSerializerOptions(Catalog.CatalogLoader.JsonOptions)) is not { } entry)
            return Fail(log, "The store entry is missing.");
        log.Write($"STORE {plan.GameName} | run {entry.Id}'s setup {plan.Version} | in {target}");
        if (StoreCatalog.Problems(entry).FirstOrDefault() is { } bad) return Fail(log, $"{entry.Name} can't be used: {bad}.");
        if (entry.Type != StoreTypes.AuthorSetup) return Fail(log, $"{entry.Name} has no setup to run.");
        if (Blocked(dataDir).TryGetValue(entry.Id, out var why)) return Fail(log, $"{entry.Name} can't be managed safely on this PC: {why}");
        if (OptiScalerInstaller.RunningFrom(target).FirstOrDefault() is { } running) return Fail(log, $"Close the game first ({running} is running); nothing was changed.");
        if (!OptiScalerInstaller.CanWrite(target)) return Fail(log, "This game's folder needs administrator rights, and setups are never run as administrator.");
        if (OptiScalerInstaller.ReadStoreRecord(target).Entries.Any(e => e.EntryId == entry.Id)) return Fail(log, $"{entry.Name} is already set up here; take it out first.");
        if (!plan.StoreSources.TryGetValue("setup", out var source) || !IsUnder(source, dataDir)) return Fail(log, "The downloaded setup isn't in Upshift's folder.");
        var (exe, args) = Command(entry, source, target, dataDir);
        if (!File.Exists(exe) || !IsUnder(exe, source)) return Fail(log, $"{entry.Setup!.Run} isn't in the download.");

        var entryFolder = OptiScalerInstaller.StoreData(dataDir).Entry(entry.Id);
        Directory.CreateDirectory(entryFolder);
        var work = Path.Combine(OptiScalerInstaller.StoreData(dataDir).Root, "_snapshot-" + entry.Id + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        try
        {
            // 1. Before: both folders copied whole (they're put back from these), and what can be seen outside.
            long total = 0;
            foreach (var f in Files(target)) total += new FileInfo(f).Length;
            if (total > SnapshotLimit) return Fail(log, $"This game's folder is over {SnapshotLimit / 1024 / 1024 / 1024} GB, too big to snapshot, so the setup wasn't run.");
            var gameBefore = Copy(target, Path.Combine(work, "game"));
            var dataBefore = Copy(entryFolder, Path.Combine(work, "data"));
            var outsideBefore = Outside.Take(dataDir);
            log.Write($"  snapshot: {gameBefore.Count} game file(s), {dataBefore.Count} in Upshift's folder; running {exe} {args}");

            // 2. The author's setup, unelevated.
            int exitCode;
            using (var process = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = entryFolder })!)
            {
                if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    Undo(target, entryFolder, work, gameBefore, dataBefore, log);
                    return Fail(log, $"{entry.Name}'s setup didn't finish in {Timeout.TotalMinutes:0} minutes; it was stopped and its changes undone.");
                }
                exitCode = process.ExitCode;
            }
            Thread.Sleep(1500);

            // 3. After: anything outside the two folders → undo everything, remove what it made, block the entry.
            var outside = Outside.Diff(outsideBefore, Outside.Take(dataDir));
            if (outside.Count > 0)
            {
                Undo(target, entryFolder, work, gameBefore, dataBefore, log);
                var left = Outside.RemoveNew(outside, log);
                var reason = "its setup changed things outside the game folder and Upshift's folder: " + string.Join(", ", outside.Select(o => o.Describe).Take(6))
                             + (outside.Count > 6 ? $" and {outside.Count - 6} more" : "");
                Block(dataDir, entry.Id, reason);
                return Fail(log, $"{entry.Name} can't be managed safely on this PC: {reason}. Upshift undid what it did in the game folder"
                                 + (left.Count == 0 ? " and removed what it added outside." : $"; these couldn't be removed: {string.Join(", ", left)}."));
            }
            if (exitCode != 0)
            {
                Undo(target, entryFolder, work, gameBefore, dataBefore, log);
                return Fail(log, $"{entry.Name}'s setup ended with code {exitCode}; its changes were undone.");
            }

            // 4. Record every change in the game folder (backups for changed and removed files) and Upshift's folder.
            var installed = new StoreInstalled
            {
                EntryId = entry.Id, Name = entry.Name, Type = entry.Type, Version = plan.Version ?? "", Released = plan.Released, AddedUtc = DateTime.UtcNow, Entry = entry,
                Message = "Its setup ran; Upshift recorded every file it changed."
            };
            var gameAfter = Hashes(target);
            foreach (var (rel, hash) in gameAfter)
            {
                if (!gameBefore.TryGetValue(rel, out var old)) installed.Files.Add(new StoreFileRecord { Path = rel, Kind = StoreFileKind.Added, Sha256 = hash });
                else if (old != hash) installed.Files.Add(Backed(rel, hash, old, StoreFileKind.Replaced, target, work, entry.Id));
            }
            foreach (var (rel, old) in gameBefore.Where(b => !gameAfter.ContainsKey(b.Key)))
                installed.Files.Add(Backed(rel, null, old, StoreFileKind.Removed, target, work, entry.Id));
            var foldersBefore = Directory.EnumerateDirectories(Path.Combine(work, "game"), "*", SearchOption.AllDirectories)
                .Select(d => Path.GetRelativePath(Path.Combine(work, "game"), d)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(target, d))
                         .Where(d => !d.StartsWith(OptiScalerInstaller.StateFolder, StringComparison.OrdinalIgnoreCase) && !foldersBefore.Contains(d)))
                if (!installed.Folders.Any(f => dir.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase))) installed.Folders.Add(dir);
            foreach (var (rel, hash) in Hashes(entryFolder))
                installed.Files.Add(new StoreFileRecord { Path = Path.Combine(entry.Id, rel), Kind = StoreFileKind.DataFile, Sha256 = hash });
            var usersPath = Path.Combine(entryFolder, ".games.json");
            var users = File.Exists(usersPath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(usersPath)) ?? new() : new List<string>();
            if (!users.Contains(target, StringComparer.OrdinalIgnoreCase)) users.Add(target);
            File.WriteAllText(usersPath, JsonSerializer.Serialize(users));

            var record = OptiScalerInstaller.ReadStoreRecord(target);
            record.Entries.Add(installed);
            Directory.CreateDirectory(Path.Combine(target, OptiScalerInstaller.StateFolder));
            File.WriteAllText(OptiScalerInstaller.StoreRecordPath(target), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            log.Write($"  recorded {installed.Files.Count(f => f.Kind != StoreFileKind.DataFile)} change(s) in the game folder, {installed.Folders.Count} new folder(s)");
            return new InstallResult { Success = true, Message = $"{entry.Name}'s setup ran. Upshift recorded every change it made, so Remove takes it out again." };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Fail(log, $"{entry.Name}'s setup couldn't be run: {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    private static StoreFileRecord Backed(string rel, string? hash, string original, StoreFileKind kind, string target, string work, string id)
    {
        var backupRel = Path.Combine(OptiScalerInstaller.StateFolder, "store-backup", id, rel);
        var backup = Path.Combine(target, backupRel);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(Path.Combine(work, "game", rel), backup, true);
        return new StoreFileRecord { Path = rel, Kind = kind, Sha256 = hash, Backup = backupRel, OriginalSha256 = original };
    }

    /// <summary>Both folders back exactly as the snapshot.</summary>
    private static void Undo(string target, string entryFolder, string work, Dictionary<string, string> gameBefore, Dictionary<string, string> dataBefore, InstallLog log)
    {
        Restore(target, Path.Combine(work, "game"), gameBefore);
        Restore(entryFolder, Path.Combine(work, "data"), dataBefore);
        log.Write("  undone: both folders are back as before the setup");
    }

    private static void Restore(string folder, string copy, Dictionary<string, string> before)
    {
        foreach (var file in Files(folder).ToList())
        {
            var rel = Path.GetRelativePath(folder, file);
            if (!before.ContainsKey(rel)) File.Delete(file);
        }
        foreach (var (rel, hash) in before)
        {
            var path = Path.Combine(folder, rel);
            if (!File.Exists(path) || OptiScalerInstaller.Sha256(path) != hash)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(Path.Combine(copy, rel), path, true);
            }
        }
        var keepDirs = Directory.Exists(copy)
            ? Directory.EnumerateDirectories(copy, "*", SearchOption.AllDirectories).Select(d => Path.GetRelativePath(copy, d)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
        {
            var rel = Path.GetRelativePath(folder, dir);
            if (!rel.StartsWith(OptiScalerInstaller.StateFolder, StringComparison.OrdinalIgnoreCase) && !keepDirs.Contains(rel) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
    }

    /// <summary>Files of a folder, without Upshift's own .upshift state.</summary>
    private static IEnumerable<string> Files(string folder) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(folder, f).StartsWith(OptiScalerInstaller.StateFolder + "\\", StringComparison.OrdinalIgnoreCase))
            : Enumerable.Empty<string>();

    private static Dictionary<string, string> Hashes(string folder) =>
        Files(folder).ToDictionary(f => Path.GetRelativePath(folder, f), OptiScalerInstaller.Sha256, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Copy(string folder, string to)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(to);
        if (Directory.Exists(folder))
            foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(folder, dir);
                if (!rel.StartsWith(OptiScalerInstaller.StateFolder, StringComparison.OrdinalIgnoreCase)) Directory.CreateDirectory(Path.Combine(to, rel));
            }
        foreach (var file in Files(folder))
        {
            var rel = Path.GetRelativePath(folder, file);
            var dest = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
            result[rel] = OptiScalerInstaller.Sha256(dest);
        }
        return result;
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        var r = Path.GetFullPath(root).TrimEnd('\\');
        return full.Equals(r, StringComparison.OrdinalIgnoreCase) || full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static InstallResult Fail(InstallLog log, string message)
    {
        log.Write("  refused: " + message);
        return new InstallResult { Message = message };
    }

    /// <summary>What can be seen outside the two folders without a driver: names of top-level items and registry keys/values.</summary>
    public static class Outside
    {
        public sealed record Item(string Kind, string Where, string Name, bool UserRemovable)
        {
            public string Describe => Kind switch { "key" => $"registry key {Where}\\{Name}", "value" => $"registry value {Where}\\{Name}", _ => System.IO.Path.Combine(Where, Name) };
        }

        public static HashSet<Item> Take(string dataDir)
        {
            var items = new HashSet<Item>();
            var data = System.IO.Path.GetFullPath(dataDir).TrimEnd('\\');
            void Dir(string? root, bool removable, bool filesToo = true)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(root))
                    {
                        if (System.IO.Path.GetFullPath(d).TrimEnd('\\').Equals(data, StringComparison.OrdinalIgnoreCase)) continue;
                        if (System.IO.Path.GetFileName(d).Equals("Temp", StringComparison.OrdinalIgnoreCase)) continue;
                        items.Add(new Item("folder", root, System.IO.Path.GetFileName(d), removable));
                    }
                    if (filesToo) foreach (var f in Directory.EnumerateFiles(root)) items.Add(new Item("file", root, System.IO.Path.GetFileName(f), removable));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Dir(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), true);
            Dir(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), true, filesToo: false);
            Dir(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), true);
            Dir(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), true, filesToo: false);
            Dir(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), true);
            Dir(Environment.GetFolderPath(Environment.SpecialFolder.Programs), true);
            Dir(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), false);
            Dir(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), false);
            Dir(Environment.SystemDirectory, false);

            void Keys(RegistryKey hive, string path, int depth, bool removable)
            {
                try
                {
                    using var key = hive.OpenSubKey(path);
                    if (key is null) return;
                    foreach (var name in key.GetSubKeyNames())
                    {
                        items.Add(new Item("key", $"{hive.Name}\\{path}", name, removable));
                        if (depth > 1) Keys(hive, path + "\\" + name, depth - 1, removable);
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
            void Values(RegistryKey hive, string path)
            {
                try
                {
                    using var key = hive.OpenSubKey(path);
                    if (key is not null) foreach (var name in key.GetValueNames()) items.Add(new Item("value", $"{hive.Name}\\{path}", name, true));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
            Keys(Registry.CurrentUser, "Software", 2, true);
            Keys(Registry.LocalMachine, "Software", 1, false);
            Values(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run");
            Values(Registry.CurrentUser, "Environment");
            return items;
        }

        public static List<Item> Diff(HashSet<Item> before, HashSet<Item> after) => after.Where(a => !before.Contains(a)).ToList();

        /// <summary>Removes new things a user may remove (folders, files, HKCU keys and values). Returns what couldn't be.</summary>
        public static List<string> RemoveNew(List<Item> items, InstallLog log)
        {
            var left = new List<string>();
            foreach (var item in items)
            {
                if (!item.UserRemovable) { left.Add(item.Describe); continue; }
                try
                {
                    switch (item.Kind)
                    {
                        case "folder": Directory.Delete(System.IO.Path.Combine(item.Where, item.Name), true); break;
                        case "file": File.Delete(System.IO.Path.Combine(item.Where, item.Name)); break;
                        case "key":
                            if (!item.Where.StartsWith("HKEY_CURRENT_USER\\")) { left.Add(item.Describe); continue; }
                            Registry.CurrentUser.DeleteSubKeyTree(item.Where["HKEY_CURRENT_USER\\".Length..] + "\\" + item.Name, false);
                            break;
                        case "value":
                            using (var key = Registry.CurrentUser.OpenSubKey(item.Where["HKEY_CURRENT_USER\\".Length..], writable: true))
                                key?.DeleteValue(item.Name, false);
                            break;
                    }
                    log.Write($"  removed {item.Describe} (made by the setup)");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                {
                    left.Add(item.Describe);
                }
            }
            return left;
        }
    }
}
