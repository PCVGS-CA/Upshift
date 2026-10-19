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
            // The earlier test kit's values (set by hand, never recorded by Upshift) go back to what they were before it;
            // otherwise Upshift's own two changes are put back.
            var kit = Ini(game) is { } ini && OptiScalerInstaller.ReadManifest(game.TargetDir!) is { } manifest
                ? ShaderLibrary.KitUndo(ini, manifest.IniChanges, AppServices.DataDir)
                : new List<IniSetting>();
            var back = await Configure(game, kit.Count > 0 ? kit : ShaderLibrary.PutBackSettings());
            return back.Success ? null : back.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return ex.Message; }
    }

    /// <summary>"Did it help?" for one game: its sessions labelled, and the latest Cold start and Pre-built compared.</summary>
    public static StutterComparison Comparison(GameInfo game)
    {
        var gameDir = GameDir(game);
        var caches = new ShaderCaches(AppServices.DataDir);
        var events = caches.Events().Concat(PrebuildReport.PrebuildEvents(gameDir)).ToList();
        var runs = AppServices.Measurements.Get(game.Id).Runs;
        return StutterReport.Compare(StutterReport.Label(ShaderLibrary.Sessions(gameDir), events, runs, PrebuildReport.PrebuildRuns(gameDir), caches.Clears()));
    }

    /// <summary>
    /// The game's program that's running, or null. A quick check by name (the game's exe and its launcher's), leaving out
    /// Upshift's own pre-build copy, which runs under the game's name from Upshift's data folder.
    /// </summary>
    public static string? RunningByName(GameInfo game)
    {
        var names = new[] { ShaderLibrary.ProgramOf(game), game.LaunchExePath is { Length: > 0 } l ? Path.GetFileName(l) : null }
            .Where(n => n is { Length: > 0 }).Distinct(StringComparer.OrdinalIgnoreCase);
        var data = Path.GetFullPath(AppServices.DataDir).TrimEnd('\\') + "\\";
        foreach (var name in names)
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name!)))
            {
                using (p)
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch (Exception) { /* can't be read: counts as the game */ }
                    if (path is not null && path.StartsWith(data, StringComparison.OrdinalIgnoreCase)) continue;   // Upshift's pre-build copy
                    return name;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The thorough check, run just before clearing the cache or pre-building (and while a pre-build runs): anything
    /// running from the game's folders (the game or its launcher), or the game's programs by name. Null when nothing runs.
    /// </summary>
    public static string? RunningProgram(GameInfo game)
    {
        try
        {
            var folders = new[] { game.TargetDir, game.InstallDir, game.LaunchExePath is { Length: > 0 } l ? Path.GetDirectoryName(l) : null };
            if (OptiScalerInstaller.RunningFrom(folders).FirstOrDefault() is { } running) return running;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return RunningByName(game);
    }

    /// <summary>Games with a Cold start or Pre-built session (only while the feature is on).</summary>
    private static List<(string Name, StutterComparison Comparison)> Comparisons()
    {
        if (!Enabled) return new();
        var list = new List<(string, StutterComparison)>();
        foreach (var game in AppServices.Library.Current.Where(g => !g.HasAntiCheat && ShaderLibrary.ProgramOf(g) is not null).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(Path.Combine(GameDir(game), "recordings"))) continue;
                var c = Comparison(game);
                if (c.HasAny) list.Add((game.Name, c));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        return list;
    }

    public static Task<bool> HasComparisonsAsync() => Task.Run(() => Comparisons().Count > 0);

    /// <summary>
    /// "Export all results": the "Did it help?" results, one row per game, in a CSV beside the Measure results
    /// ("… - shader pre-build.csv"; its columns differ, so the two tables stay separate). Returns the file name, or null.
    /// </summary>
    public static async Task<string?> ExportComparisonsAsync(string measureCsvPath, Core.Services.PcInfo pc)
    {
        var games = await Task.Run(Comparisons);
        if (games.Count == 0) return null;
        var path = Path.Combine(Path.GetDirectoryName(measureCsvPath)!, Path.GetFileNameWithoutExtension(measureCsvPath) + " - shader pre-build.csv");
        await File.WriteAllTextAsync(path, StutterReport.Csv(games, pc), new System.Text.UTF8Encoding(true));
        return Path.GetFileName(path);
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
        // Checked again here, just before it starts: a pre-build while the game runs builds shaders twice at once and
        // spoils the comparison.
        if (ShaderPrebuildGames.RunningProgram(game) is { } running)
        {
            LastResult = new PrebuildResult(false, 0, 0, 0, null, $"Not started: {running} is running. Close {game.Name} first, then press Pre-build now.");
            LastGameId = game.Id;
            Changed?.Invoke();
            return;
        }
        GameId = game.Id;
        Progress = null;
        _cts = new CancellationTokenSource();
        var gameDir = ShaderPrebuildGames.GameDir(game);
        var progress = new Progress<PrebuildProgress>(p => { Progress = p; Changed?.Invoke(); });
        Changed?.Invoke();
        var cts = _cts;
        string? startedMeanwhile = null;
        // The game started while its shaders are being pre-built: stop (the pre-build would only compete with the game).
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(2000, cts.Token); } catch (OperationCanceledException) { return; }
                if (ShaderPrebuildGames.RunningProgram(game) is { } running) { startedMeanwhile = running; cts.Cancel(); return; }
            }
        });
        _ = Task.Run(async () =>
        {
            PrebuildResult result;
            try
            {
                ShaderLibrary.Merge(gameDir);   // the latest finished session counts too
                result = await PrebuildRunner.RunAsync(ShaderPrebuildGames.AppPrebuild, program, gameDir, progress, cts.Token);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { result = new PrebuildResult(false, 0, 0, 0, null, ex.Message); }
            if (startedMeanwhile is { } who)
                result = result with { Success = false, Problem = $"Stopped: {who} was started while the shaders were being pre-built. Close {game.Name}, press Pre-build now again, and start the game once it's done." };
            cts.Cancel();   // ends the watcher
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
