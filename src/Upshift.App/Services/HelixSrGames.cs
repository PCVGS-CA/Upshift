using Upshift.Core.Catalog;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.App.Services;

/// <summary>
/// HelixSR (experimental) for the app: whether it's offered on this card and for a game, which games use it, and the
/// safety net: a game set to HelixSR whose files are missing (removed, or a failed or blocked setup) goes back to the
/// upscaler it had before, with a message saying so. Changes go through the same one-change-at-a-time gate as updates.
/// Nothing here ever throws: HelixSR's state must never take Upshift down. State that can't be read counts as
/// "Not set up" (HelixSr.ReadState says why), and never switches a game back on its own.
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
    public static HelixSrCard Card
    {
        get
        {
            try
            {
                return Release is { } r ? HelixSr.ForCard(AppServices.EffectiveGpu, r, AppServices.ShowHelixSrForTesting)
                                        : new HelixSrCard(false, false, "Not available: this catalog has no HelixSR entry.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new HelixSrCard(false, false, $"Not available: {ex.Message}");
            }
        }
    }

    /// <summary>HelixSR's state on this PC (never throws).</summary>
    public static HelixSrState State => HelixSr.ReadState(AppServices.DataDir);

    public static HelixSrInstalled? Installed => State.Installed;

    /// <summary>The per-game option: an offered card, a Direct3D 12 game without anti-cheat (OptiScaler installed is checked by the caller).</summary>
    public static bool OfferedFor(GameInfo game)
    {
        try { return Release is not null && HelixSr.OfferedFor(game, Card); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    private static IniFile? Ini(GameInfo game)
    {
        try
        {
            if (game.TargetDir is not { } dir || OptiScalerInstaller.ReadManifest(dir) is not { Removed: false }) return null;
            var path = Path.Combine(dir, "OptiScaler.ini");
            return File.Exists(path) ? IniFile.Load(path) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }   // a locked or unreadable ini: not known to use HelixSR
    }

    public static bool InUse(GameInfo game) => Ini(game) is { } ini && HelixSr.InUse(ini, AppServices.DataDir);

    public static List<GameInfo> GamesUsingIt()
    {
        try { return AppServices.Library.Current.Where(InUse).ToList(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new List<GameInfo>(); }
    }

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
        try
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return $"{game.Name} is set to HelixSR, but {why}, and switching it back failed: {ex.Message} Choose another upscaler in its OptiScaler options.";
        }
    }

    /// <summary>Every game set to HelixSR while one of its files is missing goes back to its previous upscaler.</summary>
    public static async Task<List<string>> CheckAllAsync()
    {
        var messages = new List<string>();
        try
        {
            if (HelixSr.MissingFile(AppServices.DataDir) is not { } missing) return messages;
            foreach (var game in GamesUsingIt())
                messages.Add(await SwitchBackAsync(game, $"HelixSR's {missing} is missing"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            messages.Add($"Upshift couldn't check the games set to HelixSR: {ex.Message}");
        }
        return messages;
    }

    /// <summary>One game: switched back when it's set to HelixSR and a file is missing; null when nothing was needed.</summary>
    public static async Task<string?> CheckAsync(GameInfo game)
    {
        try
        {
            if (!InUse(game) || HelixSr.MissingFile(AppServices.DataDir) is not { } missing) return null;
            return await SwitchBackAsync(game, $"HelixSR's {missing} is missing");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
}
