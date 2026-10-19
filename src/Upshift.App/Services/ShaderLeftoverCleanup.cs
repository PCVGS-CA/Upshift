using Upshift.Core.Install;

namespace Upshift.App.Services;

/// <summary>
/// Once, at start-up after 1.3.0-beta.1: games whose OptiScaler.ini still loads the removed shader recorder get their
/// values put back, then the feature's data (shaderwarm, moved-aside driver caches) goes. A game that can't be changed
/// now (running, permission declined) is tried again at the next start, and the data stays until then.
/// </summary>
public static class ShaderLeftoverCleanup
{
    public static async Task RunAsync()
    {
        var data = AppServices.DataDir;
        if (!ShaderLeftovers.Pending(data)) return;
        var log = new InstallLog(data);
        var allDone = true;
        foreach (var game in AppServices.Library.Current.ToList())
        {
            try
            {
                if (game.TargetDir is not { } dir || OptiScalerInstaller.ReadManifest(dir) is not { Removed: false } manifest) continue;
                var iniPath = Path.Combine(dir, "OptiScaler.ini");
                if (!File.Exists(iniPath) || IniFile.Load(iniPath) is not { } ini || !ShaderLeftovers.Loads(ini, data)) continue;
                var settings = ShaderLeftovers.TurnOff(ini, manifest.IniChanges, data);
                if (settings.Count == 0) continue;
                var result = await GameUpdates.RunExclusiveAsync(game, () => InstallRunner.RunAsync(new InstallPlan
                {
                    Operation = InstallOperation.Configure, GameId = game.Id, GameName = game.Name, TargetDir = dir, IniSettings = settings
                }));
                log.Write($"SHADER RECORDER OFF {game.Name}: {result.Message}");
                if (!result.Success) allDone = false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { allDone = false; log.Write($"SHADER RECORDER OFF {game.Name} FAILED: {ex.Message}"); }
        }
        if (!allDone) return;
        foreach (var line in await Task.Run(() => ShaderLeftovers.RemoveData(data))) log.Write("  " + line);
    }
}
