using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using Upshift.Core.Models;

namespace Upshift.Core.Launch;

/// <summary>How a game gets started, worked out before starting it (also used to explain launch options).</summary>
public sealed record LaunchPlan(
    string Via,
    string FileName,
    string? Arguments,
    string? WorkingDirectory,
    bool IsUrl,
    bool TakesLaunchOptions);

public sealed record LaunchResult(bool Success, string Message);

/// <summary>
/// Starts games the way their store does: Steam with -applaunch, Epic / Ubisoft / EA / Heroic through their launch
/// URLs, Battle.net through its launcher, Xbox through shell:AppsFolder, and everything else by running the main exe
/// from its own folder. Games are never started with admin rights: an exe that asks for them is refused, and nothing
/// is launched while Upshift itself runs as administrator.
/// </summary>
public static partial class GameLauncher
{
    private const int ErrorElevationRequired = 740;

    /// <summary>Whether the store's way of starting the game can pass launch options, and if not, where they go instead.</summary>
    public static (bool Takes, string Note) LaunchOptionsSupport(GameInfo game) => game.Source switch
    {
        GameSourceKind.Steam => (true, "Passed to Steam with the game when you press Play here. Launch options set in Steam itself still apply too."),
        GameSourceKind.Epic => (false, "Epic's launch link can't pass launch options. Add them in Epic Games Launcher > Settings > the game > Additional Command Line Arguments."),
        GameSourceKind.Heroic => (false, "Heroic's launch link can't pass launch options. Add them in Heroic > the game > Settings > Advanced > Game arguments."),
        GameSourceKind.Xbox => (false, "Xbox app games start through Windows and can't take launch options."),
        GameSourceKind.EA => (false, "The EA app's launch link can't pass launch options. Add them in the EA app > the game > View properties > Advanced launch options."),
        GameSourceKind.Ubisoft => (false, "Ubisoft Connect's launch link can't pass launch options. Add them in Ubisoft Connect > the game > Properties > Launch arguments."),
        GameSourceKind.BattleNet => (false, "Battle.net can't take launch options from another app. Add them in Battle.net > the game's cog > Game Settings > Additional command line arguments."),
        _ => (true, "Passed to the game's main file when you press Play here.")
    };

    /// <summary>How the game would be started, or null with the reason it can't be.</summary>
    public static LaunchPlan? Plan(GameInfo game, string? launchOptions, out string? problem)
    {
        problem = null;
        var options = string.IsNullOrWhiteSpace(launchOptions) ? null : launchOptions.Trim();
        switch (game.Source)
        {
            case GameSourceKind.Steam:
                if (string.IsNullOrEmpty(game.SourceId)) { problem = "Steam didn't report this game's app ID."; return null; }
                var steam = SteamExe();
                if (steam is null) { problem = "Steam isn't installed (steam.exe wasn't found in the registry)."; return null; }
                return new LaunchPlan("Steam", steam, $"-applaunch {game.SourceId}{(options is null ? "" : " " + options)}", Path.GetDirectoryName(steam), false, true);

            case GameSourceKind.Epic when !string.IsNullOrEmpty(game.SourceId):
                return Url("Epic Games Launcher", $"com.epicgames.launcher://apps/{Uri.EscapeDataString(game.SourceId)}?action=launch&silent=true");

            case GameSourceKind.Heroic when !string.IsNullOrEmpty(game.SourceId):
                return Url("Heroic", $"heroic://launch/{HeroicRunner(game.SourceId)}/{Uri.EscapeDataString(game.SourceId)}");

            case GameSourceKind.Ubisoft when !string.IsNullOrEmpty(game.SourceId):
                return Url("Ubisoft Connect", $"uplay://launch/{game.SourceId}/0");

            case GameSourceKind.EA when EaContentId(game.InstallDir) is { } offer:
                return Url("EA app", $"origin2://game/launch?offerIds={Uri.EscapeDataString(offer)}&autoDownload=1");

            case GameSourceKind.BattleNet when !string.IsNullOrEmpty(game.SourceId) && BattleNetExe() is { } bnet:
                return new LaunchPlan("Battle.net", bnet, $"--exec=\"launch_uid {game.SourceId}\"", Path.GetDirectoryName(bnet), false, false);

            case GameSourceKind.Xbox:
                if (XboxAumid(game.InstallDir) is not { } aumid) { problem = "Windows has no app registered for this Xbox game folder, so it can't be started from here."; return null; }
                return new LaunchPlan("the Xbox app", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    $"shell:AppsFolder\\{aumid}", null, false, false);
        }

        // GOG, itch.io, games added by hand or found by the drive scan, and stores whose ID we couldn't read: the main exe.
        var exe = new[] { game.LaunchExePath, game.ExePath }.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        if (exe is null) { problem = "The game's main file wasn't found. Use Change… under Main game file to pick it."; return null; }
        return new LaunchPlan(Path.GetFileName(exe), exe, options, Path.GetDirectoryName(exe), false, true);
    }

    private static LaunchPlan Url(string via, string url) => new(via, url, null, null, true, false);

    /// <summary>Starts the game. Never elevated; failures come back as a message for the details pane.</summary>
    public static LaunchResult Launch(GameInfo game, string? launchOptions)
    {
        if (IsElevated())
            return new(false, "Upshift is running as administrator, and games are never started with admin rights. Close Upshift and start it normally, then press Play.");
        if (Plan(game, launchOptions, out var problem) is not { } plan) return new(false, problem!);

        try
        {
            if (plan.IsUrl)
            {
                // The store's own launcher handles the link; it runs as this (non-admin) user.
                using var _ = Process.Start(new ProcessStartInfo(plan.FileName) { UseShellExecute = true });
            }
            else
            {
                // CreateProcess, not ShellExecute: an exe that demands admin fails with error 740 instead of showing a UAC prompt.
                var start = new ProcessStartInfo(plan.FileName) { UseShellExecute = false, WorkingDirectory = plan.WorkingDirectory ?? "" };
                if (plan.Arguments is not null) start.Arguments = plan.Arguments;
                using var _ = Process.Start(start);
            }
            return new(true, plan.IsUrl || plan.Via is "Steam" or "Battle.net" or "the Xbox app"
                ? $"Asked {plan.Via} to start {game.Name}."
                : $"Started {plan.Via}{(plan.Arguments is null ? "" : " " + plan.Arguments)}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            return new(false, $"{Path.GetFileName(plan.FileName)} asks for admin rights, and Upshift never starts games as administrator. Start it from its store or its own shortcut instead.");
        }
        catch (Win32Exception ex)
        {
            return new(false, plan.IsUrl
                ? $"Windows couldn't open the {plan.Via} launch link ({ex.Message}). Is {plan.Via} installed?"
                : $"Windows couldn't start {Path.GetFileName(plan.FileName)}: {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new(false, $"The game couldn't be started: {ex.Message}");
        }
    }

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // ---------------- store details ----------------

    private static string? SteamExe()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var exe = (key?.GetValue("SteamExe") as string)?.Replace('/', '\\');
            if (exe is not null && File.Exists(exe)) return exe;
            var root = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
            return root is not null && File.Exists(Path.Combine(root, "steam.exe")) ? Path.Combine(root, "steam.exe") : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    private static string? BattleNetExe()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net", "Battle.net.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Heroic's runner for an app: "legendary" when it's in Heroic's Epic library, otherwise "gog".</summary>
    private static string HeroicRunner(string appName)
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "heroic", "legendaryConfig", "legendary", "installed.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(appName, out _)) return "legendary";
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return "gog";
    }

    /// <summary>The EA app offer ID, from the game's __Installer\installerdata.xml.</summary>
    private static string? EaContentId(string installDir)
    {
        try
        {
            var file = Path.Combine(installDir, "__Installer", "installerdata.xml");
            if (!File.Exists(file)) return null;
            return XDocument.Load(file).Descendants().FirstOrDefault(e => e.Name.LocalName == "contentID")?.Value.Trim() is { Length: > 0 } id ? id : null;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException) { return null; }
    }

    /// <summary>
    /// An Xbox game's AppUserModelID ("PackageFamilyName!AppId"): the package name and app ID come from
    /// MicrosoftGame.config, the publisher part of the family name from Windows' package registry.
    /// </summary>
    private static string? XboxAumid(string contentDir)
    {
        try
        {
            var config = Path.Combine(contentDir, "MicrosoftGame.config");
            if (!File.Exists(config)) return null;
            var doc = XDocument.Load(config);
            var name = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity")?.Attribute("Name")?.Value;
            var appId = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Executable")?.Attribute("Id")?.Value ?? "Game";
            if (string.IsNullOrEmpty(name)) return null;

            using var packages = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            var fullName = packages?.GetSubKeyNames().FirstOrDefault(k => k.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase));
            if (fullName is null) return null;
            // Full name: Name_Version_Arch_ResourceId_PublisherId → family name: Name_PublisherId.
            var parts = fullName.Split('_');
            return $"{parts[0]}_{parts[^1]}!{appId}";
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Battle.net's id for a game ("prometheus"), from its uninstall command's --uid=.</summary>
    public static string? BattleNetUid(string? uninstallString) =>
        uninstallString is not null && BattleNetUidPattern().Match(uninstallString) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"--uid=""?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BattleNetUidPattern();
}

/// <summary>Launch options per game (by game ID), kept in launch-options.json so they survive rescans.</summary>
public sealed class LaunchOptionsStore
{
    private readonly string _path;
    private readonly object _sync = new();
    private Dictionary<string, string> _options;

    public LaunchOptionsStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "launch-options.json");
        try { _options = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { _options = new(); }
    }

    public string Get(string gameId)
    {
        lock (_sync) return _options.GetValueOrDefault(gameId) ?? "";
    }

    public void Set(string gameId, string options)
    {
        lock (_sync)
        {
            options = options.Trim();
            if (options.Length == 0) _options.Remove(gameId);
            else _options[gameId] = options;
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_options, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
    }
}
