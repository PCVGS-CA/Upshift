using Upshift.Core.Models;

namespace Upshift.Core.Install;

/// <summary>One game where Upshift has a record of changes: OptiScaler in the exe folder, upscaler files Upshift updated, or both.</summary>
public sealed record CleanupTarget(string GameId, string Name, string? TargetDir, string InstallRoot)
{
    /// <summary>The folder a plan is written for: the exe folder when OptiScaler is there, otherwise the install folder.</summary>
    public string MainDir => TargetDir ?? InstallRoot;
}

/// <summary>
/// "Remove Upshift from all games" (Settings) and the uninstaller's "Also remove OptiScaler and Upshift's changes from your
/// games?": for each game, by its own records only, OptiScaler and everything Upshift added goes (with its OptiScaler.ini
/// changes and the shader recorder's values), and the game's original DLSS, FSR and XeSS files come back. Files Upshift
/// didn't add, and files changed since Upshift's change, stay where they are and are listed. Nothing else in the game
/// folder is looked at (REFramework, saves, the user's own files).
/// </summary>
public static class GameCleanup
{
    /// <summary>The games with a record of Upshift's changes (an active OptiScaler record, or upscaler files Upshift updated).</summary>
    public static List<CleanupTarget> Find(IEnumerable<GameInfo> games)
    {
        var found = new List<CleanupTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            try
            {
                var target = game.TargetDir is { Length: > 0 } t && OptiScalerInstaller.ReadManifest(t) is { Removed: false } ? t : null;
                var root = game.InstallDir is { Length: > 0 } i ? i : game.TargetDir;
                var upscaler = new[] { root, game.TargetDir }.Any(d => d is { Length: > 0 } && UpscalerFiles.ReadRecord(d) is { Files.Count: > 0 });
                if ((target is null && !upscaler) || root is null) continue;
                if (!seen.Add($"{target}|{root}")) continue;
                found.Add(new CleanupTarget(game.Id, game.Name, target, root));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return found.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Why this game has to wait (it's running, or a file Upshift would change is in use), or null. Reads only.</summary>
    public static string? WhyNotNow(CleanupTarget game)
    {
        var running = OptiScalerInstaller.RunningFrom(game.TargetDir, game.InstallRoot);
        if (running.Count > 0) return $"it's running ({string.Join(", ", running)}); close it and try again";

        var inUse = new List<string>();
        if (game.TargetDir is { } target && OptiScalerInstaller.ReadManifest(target) is { Removed: false } manifest)
            foreach (var file in manifest.Added.Concat(manifest.Replaced))
                if (Path.Combine(target, file.Path) is var path && File.Exists(path) && OptiScalerInstaller.IsLocked(path)) inUse.Add(file.Path);
        foreach (var root in Roots(game))
            foreach (var file in UpscalerFiles.ReadRecord(root)?.Files ?? new())
                if (Path.Combine(root, file.Path) is var path && File.Exists(path) && OptiScalerInstaller.IsLocked(path)) inUse.Add(file.Path);
        return inUse.Count > 0 ? $"files are in use by another program ({string.Join(", ", inUse.Distinct(StringComparer.OrdinalIgnoreCase).Take(3))})" : null;
    }

    /// <summary>The folders a removal writes to (the exe folder, the install folder, and each updated upscaler file's folder).</summary>
    public static IEnumerable<string> FoldersToWrite(string? targetDir, string? installRoot)
    {
        foreach (var dir in new[] { targetDir, installRoot }.Where(d => d is { Length: > 0 }).Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(dir)) yield return dir!;
        foreach (var root in new[] { installRoot, targetDir }.Where(d => d is { Length: > 0 }).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var file in UpscalerFiles.ReadRecord(root!)?.Files ?? new())
                if (Path.GetDirectoryName(Path.Combine(root!, file.Path)) is { } dir && Directory.Exists(dir)) yield return dir;
    }

    public static InstallPlan Plan(CleanupTarget game, string settingsFolder) => new()
    {
        Operation = InstallOperation.RemoveFromGame,
        GameId = game.GameId,
        GameName = game.Name,
        TargetDir = game.MainDir,
        InstallRoot = game.InstallRoot,
        SettingsFolder = settingsFolder
    };

    /// <summary>Removes Upshift's changes from one game (in the app, or in the elevated helper). Each part is tried on its own.</summary>
    public static InstallResult Remove(InstallPlan plan, string dataDir, InstallLog log)
    {
        log.Write($"REMOVE FROM GAME {plan.GameName} | {plan.TargetDir} | install folder {plan.InstallRoot}");
        var result = new InstallResult { Success = true };
        var problems = new List<string>();
        var target = plan.TargetDir;
        var removedOptiScaler = false;
        var restored = 0;

        // 1. OptiScaler, by its record. The shader recorder's values go first, so the saved copy of OptiScaler.ini
        //    (kept in Upshift's data for a later reinstall) doesn't carry them.
        if (OptiScalerInstaller.ReadManifest(target) is { Removed: false } manifest)
        {
            try
            {
                var iniPath = Path.Combine(target, "OptiScaler.ini");
                if (File.Exists(iniPath) && IniFile.Load(iniPath) is var ini && ShaderLeftovers.Loads(ini, dataDir))
                {
                    var undo = ShaderLeftovers.TurnOff(ini, manifest.IniChanges, dataDir);
                    var off = OptiScalerInstaller.Configure(new InstallPlan
                    {
                        Operation = InstallOperation.Configure, GameId = plan.GameId, GameName = plan.GameName, TargetDir = target,
                        IniSettings = undo
                    }, log);
                    if (!off.Success) problems.Add($"the shader recorder's settings: {off.Message}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"the shader recorder's settings: {ex.Message}"); }

            var uninstall = OptiScalerInstaller.Uninstall(new InstallPlan
            {
                Operation = InstallOperation.Uninstall, GameId = plan.GameId, GameName = plan.GameName, TargetDir = target,
                SettingsFolder = plan.SettingsFolder
            }, log);
            result.KeptChanged.AddRange(uninstall.KeptChanged.Select(p => $"{Path.Combine(target, p)}: changed since Upshift added it, so it was left as it is"));
            result.Remaining.AddRange(uninstall.Remaining);
            if (uninstall.PermissionRefused) result.PermissionRefused = true;
            if (uninstall.Success) removedOptiScaler = true;
            else problems.Add(uninstall.Message);
        }

        // 2. The game's own upscaler files Upshift updated: the originals come back where Upshift's copy is still there
        //    (or the game already put its original back). A file changed since is left alone, with its backup.
        foreach (var root in new[] { plan.InstallRoot, target }.Where(d => d is { Length: > 0 }).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (UpscalerFiles.ReadRecord(root!) is not { Files.Count: > 0 } record) continue;
            var ours = new List<UpscalerFileJob>();
            foreach (var file in record.Files)
            {
                var full = Path.Combine(root!, file.Path);
                string? current = null;
                try { current = File.Exists(full) ? OptiScalerInstaller.Sha256(full) : null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{full}: {ex.Message}"); continue; }
                if (current is not null && (current == file.Sha256 || current == file.OriginalSha256)) ours.Add(new UpscalerFileJob { Path = file.Path });
                else result.KeptChanged.Add(current is null
                    ? $"{full}: missing, so the game's original wasn't put back (it's still in {OptiScalerInstaller.StateFolder}\\{UpscalerFiles.OriginalsFolder})"
                    : $"{full}: changed since Upshift updated it, so it was left as it is (the game's original is still in {OptiScalerInstaller.StateFolder}\\{UpscalerFiles.OriginalsFolder})");
            }
            if (ours.Count == 0) continue;
            var restore = UpscalerFiles.Restore(new InstallPlan
            {
                Operation = InstallOperation.RestoreUpscalerFiles, GameId = plan.GameId, GameName = plan.GameName, TargetDir = root!, UpscalerFiles = ours
            }, log);
            if (restore.Success) restored += ours.Count;
            else problems.Add(restore.Message);
        }

        result.Success = problems.Count == 0;
        result.Remaining.InsertRange(0, problems);
        var done = new List<string>();
        if (removedOptiScaler) done.Add("OptiScaler removed");
        if (restored > 0) done.Add(restored == 1 ? "1 original upscaler file back" : $"{restored} original upscaler files back");
        result.Message = !result.Success
            ? $"Not finished: {string.Join("; ", problems)}"
            : (done.Count > 0 ? string.Join(", ", done) : "Nothing of Upshift's was left to remove")
              + (result.KeptChanged.Count > 0 ? $"; {result.KeptChanged.Count} changed file(s) left as they are" : "") + ".";
        log.Write((result.Success ? "  done: " : "  INCOMPLETE: ") + result.Message);
        return result;
    }

    private static IEnumerable<string> Roots(CleanupTarget game) =>
        new[] { game.InstallRoot, game.TargetDir }.Where(d => d is { Length: > 0 }).Distinct(StringComparer.OrdinalIgnoreCase)!;
}
