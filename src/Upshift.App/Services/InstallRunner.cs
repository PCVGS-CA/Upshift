using System.ComponentModel;
using System.Diagnostics;
using Upshift.Core.Install;

namespace Upshift.App.Services;

/// <summary>
/// Runs an install or uninstall. When the game folder is writable it happens right here; when it isn't
/// (games under Program Files), the app starts a second copy of itself with a UAC prompt that does just this one
/// job from a plan file (Upshift.exe --apply plan.json) and hands back a result file. The main window never runs as admin.
/// </summary>
public static class InstallRunner
{
    private const int ErrorCancelled = 1223; // the user said No to the UAC prompt

    private static int _running;

    /// <summary>True while an install or uninstall is running (the app isn't restarted for an update then).</summary>
    public static bool IsRunning => Volatile.Read(ref _running) > 0;

    public static async Task<InstallResult> RunAsync(InstallPlan plan)
    {
        Interlocked.Increment(ref _running);
        try { return await RunCoreAsync(plan); }
        finally { Interlocked.Decrement(ref _running); }
    }

    private static async Task<InstallResult> RunCoreAsync(InstallPlan plan)
    {
        var log = new InstallLog(AppServices.DataDir);
        if (FoldersToWrite(plan).All(OptiScalerInstaller.CanWrite))
            return await Task.Run(() => Apply(plan, log));

        log.Write($"{plan.Operation.ToString().ToUpperInvariant()} {plan.GameName}: folder needs admin rights, asking via UAC");
        var pending = Path.Combine(AppServices.DataDir, "pending");
        Directory.CreateDirectory(pending);
        var id = Guid.NewGuid().ToString("N");
        var planPath = Path.Combine(pending, $"plan-{id}.json");
        var resultPath = Path.Combine(pending, $"result-{id}.json");
        await File.WriteAllTextAsync(planPath, OptiScalerInstaller.SerializePlan(plan));
        var planHash = OptiScalerInstaller.Sha256(planPath);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath!,
                Arguments = $"--apply \"{planPath}\" --sha256 {planHash} --result \"{resultPath}\"",
                UseShellExecute = true,
                Verb = "runas"
            })!;
            await process.WaitForExitAsync();

            if (!File.Exists(resultPath))
                return new InstallResult { Message = $"The admin helper stopped without a result (exit code {process.ExitCode}). See the log in {Path.GetDirectoryName(log.FilePath)}." };
            return OptiScalerInstaller.DeserializeResult(await File.ReadAllTextAsync(resultPath))
                   ?? new InstallResult { Message = "The admin helper's result couldn't be read." };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            log.Write("  admin prompt declined; nothing changed");
            return new InstallResult { Message = "Windows didn't get permission to change the game folder, so nothing was changed.", PermissionRefused = true };
        }
        finally
        {
            TryDelete(planPath);
            TryDelete(resultPath);
        }
    }

    /// <summary>
    /// The elevated side: Upshift.exe --apply plan.json --sha256 HASH --result result.json.
    /// Checks the plan wasn't altered after the main window wrote it and only touches what a plan is allowed to touch.
    /// </summary>
    public static int RunElevated(string[] args)
    {
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var planPath = Arg("--apply");
        var resultPath = Arg("--result");
        var expectedHash = Arg("--sha256");
        if (planPath is null || resultPath is null || expectedHash is null) return 2;

        // Log next to the plan (the main window's data folder), even if the admin account is a different user.
        var dataDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(planPath)))!;
        var log = new InstallLog(dataDir);
        InstallResult result;
        try
        {
            if (!File.Exists(planPath) || OptiScalerInstaller.Sha256(planPath) != expectedHash)
                result = new InstallResult { Message = "The install plan was changed after it was written, so nothing was done." };
            else if (OptiScalerInstaller.DeserializePlan(File.ReadAllText(planPath)) is not { } plan)
                result = new InstallResult { Message = "The install plan couldn't be read." };
            else if (Validate(plan, dataDir) is { } problem)
                result = new InstallResult { Message = problem };
            else
                result = Apply(plan, log);
        }
        catch (Exception ex)
        {
            result = new InstallResult { Message = $"The admin helper failed: {ex.Message}" };
        }

        if (!result.Success) log.Write("  admin helper: " + result.Message);
        File.WriteAllText(resultPath, OptiScalerInstaller.SerializeResult(result));
        return result.Success ? 0 : 1;
    }

    private static InstallResult Apply(InstallPlan plan, InstallLog log) => plan.Operation switch
    {
        InstallOperation.Install => OptiScalerInstaller.Install(plan, log),
        InstallOperation.Configure => OptiScalerInstaller.Configure(plan, log),
        InstallOperation.Update => OptiScalerInstaller.Update(plan, log),
        InstallOperation.UndoUpdate => OptiScalerInstaller.UndoUpdate(plan, log),
        InstallOperation.Repair => OptiScalerInstaller.Repair(plan, log),
        // Signers come from the catalog compiled into the app, never from a downloaded catalog or the plan.
        InstallOperation.UpdateUpscalerFiles => UpscalerFiles.Update(plan, Core.Catalog.CatalogLoader.LoadBuiltIn().UpscalerFiles.Signers, log),
        InstallOperation.RestoreUpscalerFiles => UpscalerFiles.Restore(plan, log),
        InstallOperation.SwitchBuild => OptiScalerInstaller.Update(plan, log),
        InstallOperation.SwitchBack => OptiScalerInstaller.SwitchBack(plan, log),
        // Only names a fresh scan of the folder offers are removed (checked inside).
        InstallOperation.RemoveWithoutRecord => OptiScalerInstaller.RemoveWithoutRecord(plan, log),
        _ => OptiScalerInstaller.Uninstall(plan, log)
    };

    /// <summary>
    /// The folders a plan writes to: the exe folder for OptiScaler; for upscaler files, each file's folder and the
    /// install folder (for .upshift). All must be writable to skip the UAC helper.
    /// </summary>
    private static IEnumerable<string> FoldersToWrite(InstallPlan plan)
    {
        yield return plan.TargetDir;
        if (plan.Operation is not (InstallOperation.UpdateUpscalerFiles or InstallOperation.RestoreUpscalerFiles)) yield break;
        var paths = plan.UpscalerFiles.Count > 0
            ? plan.UpscalerFiles.Select(j => j.Path)
            : UpscalerFiles.ReadRecord(plan.TargetDir)?.Files.Select(f => f.Path) ?? Enumerable.Empty<string>();
        foreach (var dir in paths.Select(p => Path.GetDirectoryName(Path.Combine(plan.TargetDir, p))!).Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(dir)) yield return dir;
    }

    /// <summary>Files a settings change may copy in or take out: only the user-supplied ones the app knows.</summary>
    private static readonly string[] UserFileNames = OptiScalerInstaller.UserFileNames;
    private static readonly string[] OverrideNames = OptiScalerInstaller.OverrideNames;

    /// <summary>
    /// An elevated copy only installs from our own component cache into an existing folder, under a known loading name,
    /// and only copies user-supplied files from the app's user-files folder under their known names.
    /// </summary>
    private static string? Validate(InstallPlan plan, string dataDir)
    {
        if (!Directory.Exists(plan.TargetDir)) return "The game folder in the plan doesn't exist.";

        // Saved OptiScaler settings are only written to, and read from, the app's own saved-settings folder; the old
        // version's defaults only come from its download folder.
        var saved = Path.GetFullPath(Path.Combine(dataDir, SavedSettings.FolderName)) + Path.DirectorySeparatorChar;
        foreach (var path in new[] { plan.SettingsFolder, plan.RestoreSettingsFrom }.Where(p => p is not null))
            if (!(Path.GetFullPath(path!) + Path.DirectorySeparatorChar).StartsWith(saved, StringComparison.OrdinalIgnoreCase))
                return "The plan points at a settings file outside the app's saved-settings folder, so nothing was done.";
        if (plan.RestoreDefaultsFrom is not null
            && !Path.GetFullPath(plan.RestoreDefaultsFrom).StartsWith(Path.GetFullPath(Path.Combine(dataDir, "components")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "The plan points at files outside the app's download folder, so nothing was done.";

        if (plan.Operation is InstallOperation.Uninstall or InstallOperation.UndoUpdate or InstallOperation.RestoreUpscalerFiles
            or InstallOperation.SwitchBack or InstallOperation.RemoveWithoutRecord) return null;
        if (plan.Operation == InstallOperation.SwitchBuild)
        {
            // Like an update, plus the user's DLSS 5 file from the user-files folder under its known name.
            var userFiles = Path.GetFullPath(Path.Combine(dataDir, "user-files")) + Path.DirectorySeparatorChar;
            if (plan.AddFileFrom is not null && !Path.GetFullPath(plan.AddFileFrom).StartsWith(userFiles, StringComparison.OrdinalIgnoreCase))
                return "The plan points at a file outside the app's user-files folder, so nothing was done.";
            if (plan.AddFileAs is not null && !UserFileNames.Contains(plan.AddFileAs, StringComparer.OrdinalIgnoreCase))
                return "The plan names a file the app doesn't manage, so nothing was done.";
            var store = Path.GetFullPath(Path.Combine(dataDir, "components")) + Path.DirectorySeparatorChar;
            if (plan.ExtraSourceDir is not null && !Path.GetFullPath(plan.ExtraSourceDir).StartsWith(store, StringComparison.OrdinalIgnoreCase))
                return "The plan points at files outside the app's download folder, so nothing was done.";
        }
        if (plan.Operation == InstallOperation.UpdateUpscalerFiles)
        {
            // Only DLLs from the app's own download folder; UpscalerFiles.Update checks names, signatures and paths.
            var store = Path.GetFullPath(Path.Combine(dataDir, "components", "upscaler-files")) + Path.DirectorySeparatorChar;
            return plan.UpscalerFiles.All(j => j.SourceFile is not null && Path.GetFullPath(j.SourceFile).StartsWith(store, StringComparison.OrdinalIgnoreCase))
                ? null
                : "The plan points at files outside the app's download folder, so nothing was done.";
        }
        if (plan.Operation == InstallOperation.Configure)
        {
            var userFiles = Path.GetFullPath(Path.Combine(dataDir, "user-files")) + Path.DirectorySeparatorChar;
            if (plan.AddFileFrom is not null && !Path.GetFullPath(plan.AddFileFrom).StartsWith(userFiles, StringComparison.OrdinalIgnoreCase))
                return "The plan points at a file outside the app's user-files folder, so nothing was done.";
            foreach (var name in new[] { plan.AddFileAs, plan.RemoveFile }.Where(n => n is not null))
                if (!UserFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    return "The plan names a file the app doesn't manage, so nothing was done.";
            // A file of the user's in place of OptiScaler's: only the FSR 4.1.1b upscaler DLL, only from user-files.
            if (plan.OverrideFrom is not null && !Path.GetFullPath(plan.OverrideFrom).StartsWith(userFiles, StringComparison.OrdinalIgnoreCase))
                return "The plan points at a file outside the app's user-files folder, so nothing was done.";
            foreach (var name in new[] { plan.OverrideAs, plan.RemoveOverride }.Where(n => n is not null))
                if (!OverrideNames.Contains(Path.GetFileName(name), StringComparer.OrdinalIgnoreCase) || name!.Contains(".."))
                    return "The plan names a file the app doesn't manage, so nothing was done.";
            return null;
        }

        var components = Path.GetFullPath(Path.Combine(dataDir, "components")) + Path.DirectorySeparatorChar;
        if (plan.SourceDir is null || !Path.GetFullPath(plan.SourceDir).StartsWith(components, StringComparison.OrdinalIgnoreCase)
            || (plan.OldSourceDir is not null && !Path.GetFullPath(plan.OldSourceDir).StartsWith(components, StringComparison.OrdinalIgnoreCase)))
            return "The plan points at files outside the app's download folder, so nothing was done.";
        if (plan.Operation is InstallOperation.Update or InstallOperation.Repair or InstallOperation.SwitchBuild) return null; // the loading name comes from the manifest
        if (plan.ProxyName is null || !OptiScalerInstaller.ProxyNames.Contains(plan.ProxyName, StringComparer.OrdinalIgnoreCase))
            return "The plan has an unknown loading name, so nothing was done.";
        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
