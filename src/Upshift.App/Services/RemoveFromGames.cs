using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.App.Services;

/// <summary>What "Remove Upshift from all games" did, for the summary.</summary>
public sealed class RemovalSummary
{
    /// <summary>Games finished: (name, what was done).</summary>
    public List<(string Name, string What)> Done { get; } = new();
    /// <summary>Games not changed this time: (name, why). Trying again later picks them up.</summary>
    public List<(string Name, string Why)> Skipped { get; } = new();
    /// <summary>Files left in place because they changed since Upshift's change (full path and why).</summary>
    public List<string> LeftAsIs { get; } = new();
    /// <summary>The driver shader cache backup put back (one line per cache), or empty when there was none.</summary>
    public List<string> ShaderCache { get; } = new();

    /// <summary>The summary as plain text (for a message box, or the text file the uninstaller leaves).</summary>
    public string Text()
    {
        var lines = new List<string>();
        if (Done.Count == 0 && Skipped.Count == 0) lines.Add("No game had changes from Upshift.");
        if (Done.Count > 0)
        {
            lines.Add($"Finished ({Done.Count}):");
            lines.AddRange(Done.Select(d => $"  • {d.Name}: {d.What}"));
        }
        if (Skipped.Count > 0)
        {
            lines.Add("");
            lines.Add($"Skipped ({Skipped.Count}), nothing changed in these:");
            lines.AddRange(Skipped.Select(s => $"  • {s.Name}: {s.Why}"));
        }
        if (LeftAsIs.Count > 0)
        {
            lines.Add("");
            lines.Add("Left as they are (changed since Upshift's change, so not Upshift's to remove):");
            lines.AddRange(LeftAsIs.Select(l => $"  • {l}"));
        }
        if (ShaderCache.Count > 0)
        {
            lines.Add("");
            lines.Add("Removed what Pre-build shaders (no longer in Upshift) left:");
            lines.AddRange(ShaderCache.Select(l => $"  • {l}"));
        }
        return string.Join("\n", lines);
    }
}

/// <summary>
/// "Remove Upshift from all games": Settings (for the portable version) and the uninstaller's question both use this.
/// Each game goes through <see cref="GameCleanup"/> by its own records, in this process or, for folders that need it,
/// through the UAC helper. A game that's running or has a file in use is skipped and listed; the rest carry on.
/// </summary>
public static class RemoveFromGames
{
    /// <param name="inApp">From Settings: each game goes through the app's one-change-at-a-time gate and its card is refreshed.</param>
    public static async Task<RemovalSummary> RunAsync(IEnumerable<GameInfo> games, IProgress<string>? progress, bool inApp = false)
    {
        var summary = new RemovalSummary();
        var all = games.ToList();
        games = all;
        var dataDir = AppServices.DataDir;
        var targets = GameCleanup.Find(games);
        var everyGameDone = true;
        for (var i = 0; i < targets.Count; i++)
        {
            var game = targets[i];
            progress?.Report($"{game.Name} ({i + 1} of {targets.Count})…");
            try
            {
                if (await Task.Run(() => GameCleanup.WhyNotNow(game)) is { } why)
                {
                    summary.Skipped.Add((game.Name, why));
                    everyGameDone = false;
                    continue;
                }
                var plan = GameCleanup.Plan(game, SavedSettings.FolderFor(dataDir, game.Name));
                var result = inApp && all.FirstOrDefault(g => g.Id == game.GameId) is { } info
                    ? await GameUpdates.RunExclusiveAsync(info, () => InstallRunner.RunAsync(plan))
                    : await InstallRunner.RunAsync(plan);
                summary.LeftAsIs.AddRange(result.KeptChanged);
                if (result.Success) summary.Done.Add((game.Name, result.Message));
                else
                {
                    everyGameDone = false;
                    summary.Skipped.Add((game.Name, result.PermissionRefused
                        ? "Windows' permission was declined (the game is in a protected folder)"
                        : result.Message));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                everyGameDone = false;
                summary.Skipped.Add((game.Name, ex.Message));
            }
        }

        // What the removed "Pre-build shaders" left (its data, moved-aside driver caches) goes once every game is done.
        if (everyGameDone && ShaderLeftovers.Pending(dataDir))
        {
            progress?.Report("Removing what Pre-build shaders left…");
            summary.ShaderCache.AddRange(await Task.Run(() => ShaderLeftovers.RemoveData(dataDir)));
        }
        return summary;
    }
}
