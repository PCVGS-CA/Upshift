using Upshift.Core.Detection;

namespace Upshift.Core.Install;

/// <summary>One line of "Changes Upshift made to this game". Utc is null when the record predates dated entries.</summary>
public sealed record ChangeEntry(DateTime? Utc, string Text, string? Detail = null);

public static partial class OptiScalerInstaller
{
    /// <summary>
    /// What this app has changed in the folder right now, from its manifest: the OptiScaler install and its last
    /// update, each game file it replaced (with the backed-up original's version), files the user added through the
    /// options, OptiScaler.ini values, and the last repair. Newest first. <paramref name="versionLabel"/> turns a
    /// file's version into display text (e.g. adds the DLSS name).
    /// </summary>
    public static List<ChangeEntry> Changes(string targetDir, InstallManifest manifest, Func<string, string, string> versionLabel)
    {
        var list = new List<ChangeEntry>();
        string Version(string path, string name) =>
            File.Exists(path) && FileVersions.Read(path) is { } v ? versionLabel(v, name) : "unknown version";

        if (manifest.Removed)
        {
            var left = manifest.Added.Concat(manifest.Replaced).Select(f => f.Path).ToList();
            list.Add(new(manifest.RemovedUtc, "Removed OptiScaler",
                left.Count == 0 ? null : $"Left in place because they changed after the install: {string.Join(", ", left)}"));
            return list;
        }

        var fromRelease = manifest.Added.Where(f => !IsUserFile(f.Path)).Select(f => f.Path).ToList();
        var first = manifest.FirstVersion ?? manifest.LastUpdate?.FromVersion ?? manifest.Version;
        list.Add(new(manifest.InstalledUtc, $"Installed OptiScaler {first} as {manifest.ProxyName}",
            $"Added {fromRelease.Count} file(s): {Shorten(fromRelease)}"));

        if (manifest.LastUpdate is { } update)
            list.Add(new(update.UpdatedUtc, $"Updated OptiScaler from {update.FromVersion} to {update.ToVersion}",
                update.KeptIniValues.Count == 0 ? null : $"Carried over from {update.FromVersion}: {string.Join(", ", update.KeptIniValues.Select(c => $"{c.Key}={c.Current}"))}"));

        foreach (var file in manifest.Replaced)
        {
            var name = Path.GetFileName(file.Path);
            var backup = file.Backup is null ? null : Path.Combine(targetDir, file.Backup);
            var when = backup is not null && File.Exists(backup) ? File.GetCreationTimeUtc(backup) : manifest.InstalledUtc;
            var original = backup is null ? "unknown version" : Version(backup, name);
            var now = Version(Path.Combine(targetDir, file.Path), name);
            var changedSince = !IsUnchanged(targetDir, file);
            list.Add(new(when,
                IsUserFile(file.Path) ? $"Replaced the game's {file.Path} with your own file"
                : UpscalerList.IsUpscalerFile(name) ? $"OptiScaler replaced the game's {file.Path}: {original} → OptiScaler's {now}"
                : $"OptiScaler replaced the game's {file.Path}",
                (IsUserFile(file.Path) ? "Added from the options. " : "")
                + $"The original ({original}) is backed up in {Path.GetDirectoryName(file.Backup) ?? StateFolder} and comes back on uninstall."
                + (changedSince ? " The file has changed since (a game update?)." : "")));
        }

        foreach (var file in manifest.Added.Where(f => IsUserFile(f.Path)))
        {
            var path = Path.Combine(targetDir, file.Path);
            list.Add(new(File.Exists(path) ? File.GetCreationTimeUtc(path) : null,
                $"Added {file.Path} ({Version(path, file.Path)})", "Your own file, added from the options. Removed on uninstall."));
        }

        foreach (var over in manifest.Overrides ?? new List<FileOverride>())
            list.Add(new(over.AddedUtc, $"Your {OverrideName(over.Kind)} file in place of OptiScaler's {over.Path}" + (over.Version is null ? "" : $" ({versionLabel(over.Version, over.Path)})"),
                $"OptiScaler's own copy waits in {Path.GetDirectoryName(over.Backup)} and comes back when the option is turned off or OptiScaler is uninstalled."));

        foreach (var change in manifest.IniChanges)
            list.Add(new(change.ChangedUtc, $"OptiScaler.ini: [{change.Section}] {change.Key} = {change.Current}",
                $"OptiScaler's default is {change.Original ?? "not set"}"));

        if (manifest.LastRepair is { Fixed.Count: > 0 } repair)
            list.Add(new(repair.RepairedUtc, $"Repaired {repair.Fixed.Count} file(s)", Shorten(repair.Fixed)));

        return list.OrderByDescending(e => e.Utc ?? DateTime.MinValue).ToList();
    }

    /// <summary>"FSR 4.1.1b" for "fsr4-4.1.1b".</summary>
    public static string OverrideName(string kind) => kind switch
    {
        "fsr4-4.1.1b" => "FSR 4.1.1b",
        _ => kind
    };

    private static string Shorten(IReadOnlyList<string> names) =>
        names.Count <= 6 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(6))} and {names.Count - 6} more";
}
