using Upshift.Core.Install;
using Upshift.Core.Models;
using Upshift.Core.ShaderPrebuild;

namespace Upshift.App.Services;

/// <summary>
/// "Shader pre-build (Experimental)" for the app: whether it's switched on in Settings, which games it's offered for,
/// and turning learning on and off in a game. Learning is two OptiScaler.ini values, set through Upshift's usual
/// settings change (recorded, so switching it off or uninstalling OptiScaler puts them back exactly), plus Upshift's
/// recorder in its data folder. Never for games with anti-cheat.
/// </summary>
public static class ShaderPrebuildGames
{
    /// <summary>Raised (on the UI thread) when the Settings switch changes.</summary>
    public static event Action? EnabledChanged;

    public static bool Enabled => AppServices.Settings.Current.ShaderPrebuild;

    public static void RaiseEnabledChanged() => EnabledChanged?.Invoke();

    /// <summary>The native parts shipped with Upshift (ShaderPrebuild\ next to Upshift.exe).</summary>
    public static string AppRecorder => Path.Combine(Core.Services.AppLocations.ExeDir, "ShaderPrebuild", "upshift_rec.asi");
    public static string AppPrebuild => Path.Combine(Core.Services.AppLocations.ExeDir, "ShaderPrebuild", "upshift_prebuild.exe");
    public static bool Shipped => File.Exists(AppRecorder) && File.Exists(AppPrebuild);

    /// <summary>Why the card can't be used for this game, or null when it can.</summary>
    public static string? WhyNot(GameInfo game, bool installedByUs)
    {
        if (game.HasAntiCheat) return null;   // never offered: the card isn't shown at all
        if (!installedByUs) return "Pre-building shaders needs OptiScaler installed by Upshift in this game: Upshift's recorder is loaded by OptiScaler. Install OptiScaler first.";
        if (game.Api != GraphicsApi.None && !game.Api.HasFlag(GraphicsApi.D3D12)) return "Pre-building shaders works with Direct3D 12 games only.";
        if (ShaderLibrary.ProgramOf(game) is null) return "Upshift doesn't know this game's main file yet.";
        if (!Shipped) return "This copy of Upshift doesn't include the pre-build program (ShaderPrebuild folder missing).";
        return null;
    }

    public static string GameDir(GameInfo game) => ShaderLibrary.GameDir(AppServices.DataDir, ShaderLibrary.ProgramOf(game) ?? "unknown.exe");

    private static IniFile? Ini(GameInfo game)
    {
        try
        {
            if (game.TargetDir is not { } dir || OptiScalerInstaller.ReadManifest(dir) is not { Removed: false }) return null;
            var path = Path.Combine(dir, "OptiScaler.ini");
            return File.Exists(path) ? IniFile.Load(path) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    public static bool IsLearning(GameInfo game) => Ini(game) is { } ini && ShaderLibrary.IsLearning(ini, AppServices.DataDir);

    public static List<GameInfo> GamesLearning()
    {
        try { return AppServices.Library.Current.Where(IsLearning).ToList(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(); }
    }

    private static Task<InstallResult> Configure(GameInfo game, IReadOnlyList<IniSetting> settings) =>
        GameUpdates.RunExclusiveAsync(game, () => InstallRunner.RunAsync(new InstallPlan
        {
            Operation = InstallOperation.Configure, GameId = game.Id, GameName = game.Name, TargetDir = game.TargetDir!, IniSettings = settings.ToList()
        }));

    /// <summary>Turns learning on (the recorder in place, then the two values) or off (the values put back). Returns a problem, or null.</summary>
    public static async Task<string?> SetLearningAsync(GameInfo game, bool on)
    {
        try
        {
            if (on)
            {
                if (game.HasAntiCheat) return "Not for games with anti-cheat.";
                var (ok, problem) = ShaderLibrary.InstallRecorder(AppRecorder, AppServices.DataDir);
                if (!ok) return problem;
                var result = await Configure(game, ShaderLibrary.LearnSettings(AppServices.DataDir));
                return result.Success ? null : result.Message;
            }
            var back = await Configure(game, ShaderLibrary.PutBackSettings());
            if (!back.Success) return back.Message;
            // Values that were already the recorder's before Upshift recorded them (an earlier test kit set them by hand)
            // go back to OptiScaler's defaults instead.
            if (IsLearning(game))
            {
                var defaults = await Configure(game, ShaderLibrary.DefaultSettings());
                if (!defaults.Success) return defaults.Message;
            }
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ex.Message; }
    }

    /// <summary>The Settings switch turned off: learning off in every game, and the recorder removed. Returns problems.</summary>
    public static async Task<List<string>> TurnOffEverywhereAsync()
    {
        var problems = new List<string>();
        foreach (var game in GamesLearning())
            if (await SetLearningAsync(game, false) is { } problem) problems.Add($"{game.Name}: {problem}");
        if (GamesLearning().Count == 0) ShaderLibrary.RemoveRecorder(AppServices.DataDir);
        return problems;
    }
}

/// <summary>
/// The pre-build that's running (one at a time, app-wide), so the card shows its progress again after switching tabs or
/// games, and its last result. Changed is raised on the UI thread.
/// </summary>
public static class PrebuildSession
{
    public static event Action? Changed;

    public static string? GameId { get; private set; }
    public static PrebuildProgress? Progress { get; private set; }
    public static PrebuildResult? LastResult { get; private set; }
    public static string? LastGameId { get; private set; }
    public static bool Running => GameId is not null;

    private static Microsoft.UI.Dispatching.DispatcherQueue? _ui;
    private static CancellationTokenSource? _cts;

    public static void Start(GameInfo game)
    {
        if (Running || ShaderLibrary.ProgramOf(game) is not { } program) return;
        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        GameId = game.Id;
        Progress = null;
        _cts = new CancellationTokenSource();
        var gameDir = ShaderPrebuildGames.GameDir(game);
        var progress = new Progress<PrebuildProgress>(p => { Progress = p; Changed?.Invoke(); });
        Changed?.Invoke();
        _ = Task.Run(async () =>
        {
            PrebuildResult result;
            try
            {
                ShaderLibrary.Merge(gameDir);   // the latest finished session counts too
                result = await PrebuildRunner.RunAsync(ShaderPrebuildGames.AppPrebuild, program, gameDir, progress, _cts.Token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { result = new PrebuildResult(false, 0, 0, 0, null, ex.Message); }
            _ui?.TryEnqueue(() =>
            {
                LastResult = result;
                LastGameId = GameId;
                GameId = null;
                Progress = null;
                Changed?.Invoke();
            });
        });
    }

    public static void Cancel() => _cts?.Cancel();
}
