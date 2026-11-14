using Upshift.Core.Install;
using Upshift.Core.Models;
using Upshift.Core.Store;

namespace Upshift.App.Services;

/// <summary>
/// ANR frame generation, now the Upscaler Store's "anr" recipe (store.json): this keeps the Frame generation choice on the game
/// page working as before (Settings > Experimental switch, version and date, go back, use the newest, switch back). Adding,
/// updating and taking it out go through <see cref="StoreService"/>. A game set up by 1.7.0-test.2 (the old ANR record in the
/// manifest) can still be switched back.
/// </summary>
public static class AnrGames
{
    public static StoreEntry? Entry => StoreService.Store.Find(Anr.ComponentId);

    public static bool Enabled => AppServices.Settings.Current.AnrEnabled && Entry is not null;

    /// <summary>ANR in a game: its release and date, and whether it's the old (test.2) record.</summary>
    public sealed record InUse(string Version, DateTimeOffset? Released, bool Legacy);

    public static InUse? InGame(GameInfo game)
    {
        if (StoreService.InGame(game, Anr.ComponentId) is { } installed) return new InUse(installed.Version, installed.Released, false);
        return game.TargetDir is { } dir && OptiScalerInstaller.ReadManifest(dir) is { Removed: false, Anr: { } old } ? new InUse(old.Version, old.Released, true) : null;
    }

    /// <summary>Why ANR isn't offered for this game (the OptiScaler version is checked by the view: "Needs a newer OptiScaler").</summary>
    public static string? WhyNot(GameInfo game, InstallManifest? manifest)
    {
        if (!Enabled) return "Turn on ANR in Settings > Experimental.";
        if (game.HasAntiCheat) return "Not for games with anti-cheat.";
        if (manifest is not { Removed: false }) return "Install OptiScaler with Upshift first.";
        if (!Anr.ApiFits(game.Api)) return "Only for DirectX 12 and Vulkan games.";
        return null;
    }

    public static Task<InstallResult> AddAsync(GameInfo game, IProgress<string>? progress) =>
        Entry is { } e ? StoreService.UseNewestAsync(e, game, progress) : Task.FromResult(Fail("This store has no ANR entry."));

    public static Task<InstallResult> RemoveAsync(GameInfo game, IProgress<string>? progress) =>
        InGame(game) is { Legacy: true }
            ? GameUpdates.RunExclusiveAsync(game, () => InstallRunner.RunAsync(new InstallPlan
                { Operation = InstallOperation.RemoveAnr, GameId = game.Id, GameName = game.Name, TargetDir = game.TargetDir! }))
            : StoreService.RemoveAsync(game, Anr.ComponentId, progress);

    public static Task<InstallResult> GoBackAsync(GameInfo game, IProgress<string>? progress) =>
        Entry is { } e ? StoreService.GoBackAsync(e, game, progress) : Task.FromResult(Fail("This store has no ANR entry."));

    public static Upshift.Core.Components.CachedComponent? PreviousOf(string version) => Entry is { } e ? StoreService.PreviousOf(e, version) : null;

    public static bool IsHeld(GameInfo game) => StoreService.IsHeld(game, Anr.ComponentId);

    public static Upshift.Core.Components.ReleaseInfo? Newest => StoreService.NewestKnown(Anr.ComponentId);

    private static InstallResult Fail(string message) => new() { Message = message };
}
