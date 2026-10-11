using Upshift.Core.Catalog;
using Upshift.Core.Models;
using Upshift.Core.Wiki;

namespace Upshift.Core.Detection;

/// <summary>How firmly to warn before installing OptiScaler into an RE Engine game without REFramework.</summary>
public enum ReFrameworkWarning
{
    /// <summary>Not an RE Engine game, REFramework is there, the wiki says it isn't needed, or the game is blocked anyway.</summary>
    None,
    /// <summary>The OptiScaler wiki says this game needs REFramework.</summary>
    Confirmed,
    /// <summary>Another RE Engine game: REFramework is usually needed.</summary>
    Likely
}

/// <summary>
/// Capcom RE Engine games need REFramework before OptiScaler works. Games are recognised by the engine's own files
/// (catalog <c>reEngine.engineFiles</c>), not by name; the catalog's two lists from the OptiScaler wiki only decide
/// how firm the warning is. Upshift never downloads, bundles or installs REFramework.
/// <para>
/// Capcom's REX engine is RE Engine's successor and no REX game has shipped yet. When the first one does, check
/// whether it still has <c>re_chunk_000.pak</c> next to the exe (or what replaces it) and update engineFiles.
/// </para>
/// </summary>
public static class ReEngineGames
{
    public const string ConfirmedText = "This game crashes with OptiScaler unless REFramework is installed first.";
    public const string LikelyText = "Capcom RE Engine games usually need REFramework before OptiScaler will work. " +
                                     "Check that REFramework supports this game.";

    /// <summary>The engine's files when the catalog has no reEngine section (an older online catalog).</summary>
    private static readonly string[] DefaultEngineFiles = { "re_chunk_000.pak" };
    private static readonly string[] DefaultReFrameworkFiles = { "dinput8.dll", "ReShade64.dll", "reframework" };

    public static string? Text(ReFrameworkWarning warning) => warning switch
    {
        ReFrameworkWarning.Confirmed => ConfirmedText,
        ReFrameworkWarning.Likely => LikelyText,
        _ => null
    };

    /// <summary>
    /// The warning to show before installing OptiScaler. Anti-cheat comes first: a game Upshift won't install into
    /// never gets this warning.
    /// </summary>
    public static ReFrameworkWarning For(GameInfo game, ReEngineCatalog? catalog)
    {
        if (game.HasAntiCheat || game.TargetDir is not { } dir) return ReFrameworkWarning.None;
        if (!IsReEngine(dir, catalog) || HasReFramework(dir, catalog)) return ReFrameworkWarning.None;
        if (catalog is not null && Matches(game.Name, catalog.NoWarning)) return ReFrameworkWarning.None;
        if (catalog is not null && Matches(game.Name, catalog.RequiresReFramework)) return ReFrameworkWarning.Confirmed;
        return ReFrameworkWarning.Likely;
    }

    /// <summary>
    /// The game page's notice: null when it doesn't apply (not an RE Engine game, anti-cheat, or one the wiki says
    /// doesn't need REFramework); otherwise whether REFramework was found and, if not, how firm the warning is.
    /// </summary>
    public static (bool Found, ReFrameworkWarning Warning)? Notice(GameInfo game, ReEngineCatalog? catalog)
    {
        if (game.HasAntiCheat || game.TargetDir is not { } dir || !IsReEngine(dir, catalog)) return null;
        if (catalog is not null && Matches(game.Name, catalog.NoWarning)) return null;
        if (HasReFramework(dir, catalog)) return (true, ReFrameworkWarning.None);
        return (false, catalog is not null && Matches(game.Name, catalog.RequiresReFramework) ? ReFrameworkWarning.Confirmed : ReFrameworkWarning.Likely);
    }

    /// <summary>Where to get REFramework: its own nightly releases (Upshift never downloads it).</summary>
    public const string DownloadPage = "https://github.com/praydog/REFramework-nightly/releases";

    /// <summary>One of the engine's files is next to the exe.</summary>
    public static bool IsReEngine(string exeDir, ReEngineCatalog? catalog) =>
        AnyExists(exeDir, catalog is { EngineFiles.Count: > 0 } ? catalog.EngineFiles : DefaultEngineFiles);

    /// <summary>
    /// REFramework next to the exe, as the OptiScaler wiki installs it: its own <c>dinput8.dll</c> (the default), renamed
    /// to <c>ReShade64.dll</c>, or the <c>reframework</c> folder it creates on its first run.
    /// </summary>
    public static bool HasReFramework(string exeDir, ReEngineCatalog? catalog) =>
        AnyExists(exeDir, catalog is { ReFrameworkFiles.Count: > 0 } ? catalog.ReFrameworkFiles : DefaultReFrameworkFiles);

    /// <summary>
    /// The game's name matches one of the wiki's titles. A store name may leave out the title's number ("Resident Evil
    /// Requiem" for "Resident Evil 9 Requiem"), so the title without its number words also counts when three or more
    /// words are left. Only used on games already recognised by the engine's files.
    /// </summary>
    public static bool Matches(string gameName, IEnumerable<string> titles)
    {
        var key = WikiMatcher.Key(gameName);
        return key.Length > 0 && titles.Any(t => Keys(t).Contains(key));
    }

    private static IEnumerable<string> Keys(string title)
    {
        var key = WikiMatcher.Key(title);
        yield return key;
        var words = key.Split(' ').Where(w => !w.All(char.IsDigit)).ToArray();
        if (words.Length >= 3 && words.Length < key.Split(' ').Length) yield return string.Join(' ', words);
    }

    private static bool AnyExists(string dir, IEnumerable<string> names)
    {
        try
        {
            return names.Any(n => File.Exists(Path.Combine(dir, n)) || Directory.Exists(Path.Combine(dir, n)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
