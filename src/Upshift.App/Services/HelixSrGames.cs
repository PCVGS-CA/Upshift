using Upshift.Core.Catalog;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.App.Services;

/// <summary>
/// HelixSR (experimental) for the app: whether it's offered on this card and for a game, which games use it, and the
/// safety net: a game set to HelixSR whose files are missing (removed, or a failed or blocked setup) goes back to the
/// upscaler it had before, with a message saying so. Changes go through the same one-change-at-a-time gate as updates.
/// </summary>
public static class HelixSrGames
{
    public static HelixSrRelease? Release => AppServices.Catalog.HelixSr;

    /// <summary>Messages about games switched back from HelixSR, for the Library's notice (raised on the UI thread).</summary>
    public static event Action<IReadOnlyList<string>>? Notices;

    public static void RaiseNotices(IReadOnlyList<string> messages)
    {
        if (messages.Count > 0) Notices?.Invoke(messages);
    }
    public static string FilesDir => HelixSr.FilesDir(AppServices.DataDir);

    /// <summary>The card the screens describe (a pretend one when set), with the developer testing switch.</summary>
    public static HelixSrCard Card =>
        Release is { } r ? HelixSr.ForCard(AppServices.EffectiveGpu, r, AppServices.ShowHelixSrForTesting)
                         : new HelixSrCard(false, false, "Not available: this catalog has no HelixSR entry.");

    public static HelixSrInstalled? Installed => HelixSr.Installed(AppServices.DataDir);

    /// <summary>The per-game option: an offered card, a Direct3D 12 game without anti-cheat (OptiScaler installed is checked by the caller).</summary>
    public static bool OfferedFor(GameInfo game) => Release is not null && HelixSr.OfferedFor(game, Card);

    private static IniFile? Ini(GameInfo game)
    {
        if (game.TargetDir is not { } dir || OptiScalerInstaller.ReadManifest(dir) is not { Removed: false }) return null;
        var path = Path.Combine(dir, "OptiScaler.ini");
        return File.Exists(path) ? IniFile.Load(path) : null;
    }

    public static bool InUse(GameInfo game) => Ini(game) is { } ini && HelixSr.InUse(ini, AppServices.DataDir);

    public static List<GameInfo> GamesUsingIt() => AppServices.Library.Current.Where(InUse).ToList();

    /// <summary>The upscaler the game had before HelixSR, as offered on this PC's card now (Automatic when it isn't).</summary>
    public static UpscalerChoice PreviousChoice(GameInfo game)
    {
        var choices = OptiScalerOptions.Upscalers(AppServices.Gpu, game.Api, AppServices.Catalog.Fsr4, int8On: false);
        var id = HelixSr.PreviousChoice(AppServices.DataDir, game.Id);
        return choices.FirstOrDefault(c => c.Id == id && c.Id != HelixSr.ChoiceId) ?? choices[0];
    }

    /// <summary>
    /// Switches the game from HelixSR back to its previous upscaler: OptiScaler's own FidelityFX paths come back (that
    /// choice's values put both back). Returns the message to show.
    /// </summary>
    public static async Task<string> SwitchBackAsync(GameInfo game, string why)
    {
        var previous = PreviousChoice(game);
        var plan = new InstallPlan
        {
            Operation = InstallOperation.Configure,
            GameId = game.Id,
            GameName = game.Name,
            TargetDir = game.TargetDir!,
            IniSettings = previous.Settings.ToList()
        };
        var result = await GameUpdates.RunExclusiveAsync(game, () => InstallRunner.RunAsync(plan));
        if (result.Success)
        {
            HelixSr.SetPreviousChoice(AppServices.DataDir, game.Id, null);
            return $"{game.Name}: {why}, so it was switched back to {previous.Label}.";
        }
        return $"{game.Name} is set to HelixSR, but {why}, and switching it back to {previous.Label} failed: {result.Message} " +
               "Choose another upscaler in its OptiScaler options.";
    }

    /// <summary>Every game set to HelixSR while one of its files is missing goes back to its previous upscaler.</summary>
    public static async Task<List<string>> CheckAllAsync()
    {
        var messages = new List<string>();
        if (HelixSr.MissingFile(AppServices.DataDir) is not { } missing) return messages;
        foreach (var game in GamesUsingIt())
            messages.Add(await SwitchBackAsync(game, $"HelixSR's {missing} is missing"));
        return messages;
    }

    /// <summary>One game: switched back when it's set to HelixSR and a file is missing; null when nothing was needed.</summary>
    public static async Task<string?> CheckAsync(GameInfo game)
    {
        if (!InUse(game) || HelixSr.MissingFile(AppServices.DataDir) is not { } missing) return null;
        return await SwitchBackAsync(game, $"HelixSR's {missing} is missing");
    }
}
