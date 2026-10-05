using System.Text;
using Microsoft.Win32;
using Upshift.App.ViewModels;
using Upshift.Core.Install;
using Upshift.Core.Services;

namespace Upshift.App.Services;

/// <summary>
/// The contents of "Report a problem" (About) and "Report a problem with this game" (a game's panel). Everything is
/// text, read from this PC and scrubbed of the Windows user name and the SteamGridDB key. Nothing is sent anywhere.
/// </summary>
public static class ProblemReports
{
    public const string IssuesUrl = "https://github.com/PCVGS-CA/Upshift/issues/new/choose";

    public static async Task<List<ReportItem>> BuildAsync(GameCardViewModel? card)
    {
        var items = new List<ReportItem>();
        var gpus = await AppServices.GetAllGpusAsync();
        var used = AppServices.Gpu;
        var system = new StringBuilder();
        system.AppendLine($"Upshift {AppInfo.Version}");
        system.AppendLine($"Windows: {WindowsVersion()}");
        system.AppendLine($"Report made: {DateTime.Now:yyyy-MM-dd HH:mm}");
        system.AppendLine("Graphics cards Windows reports:");
        foreach (var gpu in gpus)
            system.AppendLine($"- {gpu.Name} ({gpu.Generation}), driver {gpu.DriverVersion}{(gpu == used ? "  <- the one Upshift uses" : "")}");
        if (gpus.Count == 0) system.AppendLine("- none detected");
        items.Add(new ReportItem("system.txt", "Upshift version, Windows version and every graphics card, with the one Upshift uses", system.ToString()));

        // Today's Upshift log; a game's report keeps only the entries about that game.
        var logPath = Path.Combine(AppServices.DataDir, "logs", $"install-{DateTime.Now:yyyy-MM-dd}.log");
        if (File.Exists(logPath))
        {
            var log = ReadShared(logPath);
            items.Add(card is null
                ? new ReportItem("upshift-log-today.txt", "Today's Upshift log (every install, update and repair today, with the games' names)", log)
                : new ReportItem("upshift-log-today.txt", "Today's Upshift log, only the entries about this game",
                    ProblemReport.LogForGame(log, card.Name, card.Info.TargetDir)));
        }
        if (File.Exists(CrashLog.FilePath))
            items.Add(new ReportItem("crash.log", "Upshift's crash log (errors that closed Upshift)", Tail(ReadShared(CrashLog.FilePath), 400_000)));

        if (card is not null) AddGame(items, card);

        var settings = AppServices.Settings.Current;
        return items.Select(i => i with
        {
            Text = ProblemReport.Scrub(i.Text, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName,
                new[] { settings.SteamGridDbKey })
        }).ToList();
    }

    private static void AddGame(List<ReportItem> items, GameCardViewModel card)
    {
        var info = card.Info;
        var game = new StringBuilder();
        game.AppendLine($"Game: {card.Name}");
        game.AppendLine($"Store: {card.SourceText}");
        game.AppendLine($"Install folder: {info.InstallDir}");
        game.AppendLine($"Main game file: {info.ExePath ?? "not found"}");
        game.AppendLine($"Graphics API: {card.ApiText} · Engine: {card.EngineText} · Build: {card.BuildText} · Anti-cheat: {card.AntiCheatText}");
        var dir = info.TargetDir;
        var manifest = dir is null ? null : OptiScalerInstaller.ReadManifest(dir);
        if (manifest is { Removed: false })
        {
            game.AppendLine($"OptiScaler: {GameUpdates.ComponentFor(manifest.ComponentId).Name} {manifest.Version}, loading file {manifest.ProxyName}, in {dir}");
            if (card.HasFsr4Text) game.AppendLine(card.Fsr4Text);
            if (card.HasLoadStatus) game.AppendLine(card.LoadStatusText + (card.HasLoadStatusDetail ? " " + card.LoadStatusDetail : ""));
            if (card.NeedsRepair) game.AppendLine("Needs repair: " + card.RepairText.Replace("\n\n", " "));
        }
        else game.AppendLine("OptiScaler: not installed by Upshift" + (card.RecordDamaged ? " (Upshift's record for this folder is missing or damaged)" : ""));
        items.Add(new ReportItem("game.txt", "The game's name, store, folders, OptiScaler version and loading file, and its status", game.ToString()));

        if (dir is not null)
        {
            Add(items, Path.Combine(dir, "OptiScaler.ini"), "OptiScaler.ini", "The game's OptiScaler.ini");
            Add(items, Path.Combine(dir, OptiScalerInstaller.StateFolder, "manifest.json"), "upshift-record.json", "Upshift's record of what it installed in this game");
            Add(items, Path.Combine(dir, OptiScalerLog.FileName), "OptiScaler.log", "OptiScaler's own log from the last run", 2_000_000);
        }
        Add(items, UpscalerFiles.RecordPath(info.InstallDir), "upshift-upscaler-files.json", "Upshift's record of the DLSS, FSR and XeSS files it updated");

        var changes = new StringBuilder();
        foreach (var change in card.Changes)
            changes.AppendLine($"{change.Date}\t{change.Text}" + (change.HasDetail ? $"\n\t{change.Detail.Replace("\n", "\n\t")}" : ""));
        items.Add(new ReportItem("changes.txt", "Changes Upshift made to this game (its change history)", changes.Length == 0 ? "(none)\n" : changes.ToString()));
        items.Add(new ReportItem("game-files.txt", "The files in the game's folder: names, sizes and versions only, not the files themselves",
            ProblemReport.FileList(info.InstallDir)));
    }

    private static void Add(List<ReportItem> items, string path, string name, string description, int max = 400_000)
    {
        if (File.Exists(path)) items.Add(new ReportItem(name, description, Tail(ReadShared(path), max)));
    }

    /// <summary>Reads a file another program may still have open (a log being written).</summary>
    private static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"(couldn't be read: {ex.Message})"; }
    }

    private static string Tail(string text, int max) => text.Length <= max ? text : "(… earlier lines left out)\n" + text[^max..];

    /// <summary>"Windows 11 Pro 24H2 (build 26200.6584)".</summary>
    private static string WindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var build = key?.GetValue("CurrentBuild") as string ?? Environment.OSVersion.Version.Build.ToString();
            if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            var display = key?.GetValue("DisplayVersion") as string;
            var ubr = key?.GetValue("UBR") is int u ? $".{u}" : "";
            return $"{product}{(display is null ? "" : " " + display)} (build {build}{ubr})";
        }
        catch (Exception) { return Environment.OSVersion.VersionString; }
    }
}
