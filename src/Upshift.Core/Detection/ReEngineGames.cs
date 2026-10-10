using Upshift.Core.Models;
using Upshift.Core.Wiki;

namespace Upshift.Core.Detection;

/// <summary>
/// Capcom RE Engine games that crash with OptiScaler unless REFramework is installed first. Upshift never downloads,
/// bundles or installs REFramework; it only warns before installing OptiScaler into one of these games without it.
/// </summary>
public static class ReEngineGames
{
    public const string Warning = "This game crashes with OptiScaler unless REFramework is installed first.";

    /// <summary>
    /// Taken from the OptiScaler wiki on 2026-10-10: its "Capcom RE engine games" page says "Capcom RE games in general
    /// require REFramework for Optiscaler to work", and each of these games' pages (or Compatibility List rows) says
    /// REFramework is required. Left out on purpose: Monster Hunter Rise (its page says REFramework "isn't required"),
    /// and Dead Rising Deluxe Remaster and Kunitsu-Gami (no REFramework note). The wiki's titles, as it writes them.
    /// </summary>
    public static readonly IReadOnlyList<string> Games = new[]
    {
        "Resident Evil 9 Requiem",
        "Resident Evil 8 Village",
        "Resident Evil 7 Biohazard",
        "Resident Evil 4 (2023)",
        "Resident Evil 3 (2020)",
        "Resident Evil 2 (2019)",
        "Devil May Cry 5",
        "Monster Hunter Wilds",
        "Monster Hunter Stories 3: Twisted Reflection",
        "Dragon's Dogma 2",
        "Onimusha: Way of the Sword",
        "PRAGMATA",
    };

    /// <summary>
    /// The RE Engine's data file, next to the exe in every RE Engine game (checked in Requiem's and Resident Evil 2's
    /// folders). It tells "Resident Evil 4" (2023) from the 2005 game, whose store name is the same once "(2005)" goes.
    /// </summary>
    public const string EngineFile = "re_chunk_000.pak";

    /// <summary>
    /// The wiki's title for this game when it's on the list and its exe folder has the RE Engine's file. The store name
    /// may leave out the wiki's number ("Resident Evil Requiem" for "Resident Evil 9 Requiem"), so the title without its
    /// number words also counts when three or more words are left; the engine file check keeps that from matching
    /// another game.
    /// </summary>
    public static string? Match(string gameName, string? exeDir)
    {
        var key = WikiMatcher.Key(gameName);
        if (key.Length == 0) return null;
        var title = Games.FirstOrDefault(t => Keys(t).Contains(key));
        if (title is null) return null;
        try { return exeDir is not null && File.Exists(Path.Combine(exeDir, EngineFile)) ? title : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<string> Keys(string title)
    {
        var key = WikiMatcher.Key(title);
        yield return key;
        var words = key.Split(' ').Where(w => !w.All(char.IsDigit)).ToArray();
        if (words.Length >= 3 && words.Length < key.Split(' ').Length) yield return string.Join(' ', words);
    }

    /// <summary>
    /// REFramework in the exe folder, as the OptiScaler wiki installs it: its own <c>dinput8.dll</c> (the default), renamed
    /// to <c>ReShade64.dll</c>, or the <c>reframework</c> folder it creates on its first run.
    /// </summary>
    public static bool HasReFramework(string exeDir)
    {
        try
        {
            return File.Exists(Path.Combine(exeDir, "dinput8.dll"))
                || File.Exists(Path.Combine(exeDir, "ReShade64.dll"))
                || Directory.Exists(Path.Combine(exeDir, "reframework"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>True when installing OptiScaler into this game should show <see cref="Warning"/> first.</summary>
    public static bool NeedsWarning(GameInfo game) =>
        game.TargetDir is { } dir && Match(game.Name, dir) is not null && !HasReFramework(dir);
}
