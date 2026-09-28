using System.Text.RegularExpressions;
using Upshift.Core.Hardware;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.Core.Wiki;

public enum SuggestionKind
{
    /// <summary>Load OptiScaler under another name, e.g. winmm.dll. Action: use it for the next install (or reinstall).</summary>
    Proxy,
    /// <summary>Which upscaler to pick in the game's own menu. Text only.</summary>
    InGameUpscaler,
    /// <summary>A frame generation mode the wiki says works. Action: apply it.</summary>
    FrameGen,
    /// <summary>A launch option such as -dx12. Action: copy it.</summary>
    LaunchOption,
    /// <summary>An OptiScaler.ini fix such as Dxgi=false. Action: write it (recorded in the manifest).</summary>
    IniFix,
    Warning,
    Text
}

public sealed class Suggestion
{
    public SuggestionKind Kind { get; set; }
    public string Text { get; set; } = "";
    public string? ProxyName { get; set; }
    public string? FrameGenModeId { get; set; }
    public string? LaunchOption { get; set; }
    /// <summary>Where to paste a launch option, for this game's launcher.</summary>
    public string? LaunchOptionWhere { get; set; }
    public IniSetting? Ini { get; set; }
    /// <summary>The wiki's own wording, when Text is a shortened version of it.</summary>
    public string? Source { get; set; }
}

/// <summary>The "Suggestions for this game" card (from the OptiScaler wiki) or, without a wiki entry, "General advice".</summary>
public sealed class GameSuggestions
{
    public bool FromWiki { get; set; }
    public string Title => FromWiki ? "Suggestions for this game" : "General advice";
    public string? MatchedName { get; set; }
    public string? Page { get; set; }
    public string? WebUrl { get; set; }
    public string? LastTested { get; set; }
    public bool NotWorking { get; set; }
    public List<Suggestion> Items { get; set; } = new();
    /// <summary>Frame generation mode ids the wiki says don't work for this game.</summary>
    public HashSet<string> HiddenFrameGen { get; set; } = new();
    /// <summary>The in-game upscaler input the wiki recommends when using OptiScaler (e.g. "XeSS").</summary>
    public string? InGameInput { get; set; }
    /// <summary>The loading name the wiki recommends, when it isn't the default dxgi.dll.</summary>
    public string? ProxyName { get; set; }
}

/// <summary>
/// Turns a game's OptiScaler wiki notes into short suggestions. Advice for other GPU vendors, Linux, or other store
/// versions (e.g. Game Pass) is left out; ini keys are only offered when OptiScaler.ini really has them.
/// </summary>
public static partial class SuggestionBuilder
{
    public static GameSuggestions FromWiki(CompatEntry entry, GamePage? page, GameInfo game, GpuInfo? gpu,
        IReadOnlyDictionary<string, List<string>> iniKeys, string? webUrl)
    {
        var result = new GameSuggestions
        {
            FromWiki = true,
            MatchedName = entry.Name,
            Page = entry.Page,
            WebUrl = webUrl,
            LastTested = page?.LastTested,
            NotWorking = entry.Status == "not working"
        };
        if (result.NotWorking)
            result.Items.Add(new Suggestion { Kind = SuggestionKind.Warning, Text = "The OptiScaler wiki lists this game as not working with OptiScaler." });

        // Loading name: the first name the page reports working, if it isn't the default.
        var proxy = page?.Filenames.Select(f => f.ToLowerInvariant()).FirstOrDefault(f => OptiScalerInstaller.ProxyNames.Contains(f));
        if (proxy is not null && proxy != "dxgi.dll")
            AddProxy(result, proxy, $"Load OptiScaler as {proxy} (the name the wiki reports working).");

        // Frame generation: modes the page reports working, and ones it says don't.
        foreach (var line in page?.FrameGen ?? new())
        {
            if (!Relevant(line, gpu, game)) continue;
            foreach (var mode in FrameGenModesIn(line))
                if (!result.Items.Any(i => i.FrameGenModeId == mode))
                    result.Items.Add(new Suggestion
                    {
                        Kind = SuggestionKind.FrameGen,
                        FrameGenModeId = mode,
                        Text = $"Frame generation that works: {OptiScalerOptions.AllFrameGenModes.First(m => m.Id == mode).Label}",
                        Source = line
                    });
        }

        var lines = new List<string>();
        if (page is not null) lines.AddRange(page.Settings.Concat(page.KnownIssues).Concat(page.Notes));
        lines.AddRange(Sentences(entry.Notes));

        foreach (var line in lines.Distinct())
        {
            HideBrokenFrameGen(line, result.HiddenFrameGen, game);
            if (!Relevant(line, gpu, game)) continue;
            Classify(line, result, game, iniKeys);
        }

        // A mode can't be both suggested and hidden.
        result.Items.RemoveAll(i => i.FrameGenModeId is { } m && result.HiddenFrameGen.Contains(m));
        return result;
    }

    /// <summary>Advice for games without a wiki entry, from what the scan found (engine, graphics API, 32/64-bit, upscalers).</summary>
    public static GameSuggestions General(GameInfo game)
    {
        var result = new GameSuggestions { FromWiki = false };
        void Text(string t) => result.Items.Add(new Suggestion { Kind = SuggestionKind.Text, Text = t });

        if (game.Is64Bit == false)
            result.Items.Add(new Suggestion { Kind = SuggestionKind.Warning, Text = "This game is 32-bit. OptiScaler only works with 64-bit games." });

        var hasInputs = game.Upscalers.Any(u => u.Family is UpscalerFamily.Dlss or UpscalerFamily.Fsr or UpscalerFamily.Xess);
        if (!hasInputs)
            Text("OptiScaler replaces an upscaler the game already has (DLSS, FSR 2+ or XeSS). If the game has none in its menu, OptiScaler can't add one.");

        if (game.Engine.StartsWith("Unreal", StringComparison.OrdinalIgnoreCase))
            Text("Unreal Engine game: OptiScaler goes next to the …-Win64-Shipping.exe inside <Game>\\Binaries\\Win64 (the app already picks that folder).");
        if (game.Engine == "Unity")
        {
            result.Items.Add(new Suggestion
            {
                Kind = SuggestionKind.LaunchOption,
                LaunchOption = "-force-d3d12",
                LaunchOptionWhere = WhereToPaste(game.Source),
                Text = "Unity game: many Unity games only offer DLSS/FSR in DirectX 12 mode, which this launch option turns on."
            });
        }

        var dx12 = game.Api.HasFlag(GraphicsApi.D3D12);
        var dx11 = game.Api.HasFlag(GraphicsApi.D3D11);
        var vulkan = game.Api.HasFlag(GraphicsApi.Vulkan);
        if (dx11 && !dx12)
            Text("DirectX 11 game: use FSR 2.2 or FSR 3.1, or XeSS / FSR 4 through DirectX 11-on-12 (listed as \"w/Dx12\" in OptiScaler's menu).");
        if (vulkan && !dx12)
            Text("Vulkan game: XeSS frame generation doesn't support Vulkan; FSR 4 works through \"VK w/Dx12\".");

        Text("Install as dxgi.dll (the default). If the game doesn't start or OptiScaler's menu doesn't appear, try winmm.dll or version.dll.");
        return result;
    }

    public static string WhereToPaste(GameSourceKind source) => source switch
    {
        GameSourceKind.Steam => "Paste into Steam > right-click the game > Properties > Launch Options.",
        GameSourceKind.Gog => "Paste into GOG Galaxy > the game > Settings > Manage installation > Configure > Additional arguments.",
        GameSourceKind.Epic => "Paste into Epic Games Launcher > Settings > the game > Additional Command Line Arguments.",
        GameSourceKind.Xbox => "The Xbox app has no launch options, so this may not be possible for the Game Pass version.",
        GameSourceKind.Heroic => "Paste into Heroic > the game > Settings > Advanced > Game arguments.",
        _ => "Add it to the end of Target in the game's shortcut (right-click > Properties)."
    };

    // ---------------- Line handling ----------------

    private static void Classify(string line, GameSuggestions result, GameInfo game, IReadOnlyDictionary<string, List<string>> iniKeys)
    {
        var acted = false;

        foreach (Match m in LaunchOption().Matches(line))
        {
            var option = m.Value.Trim('`');
            if (result.Items.Any(i => i.LaunchOption == option)) continue;
            result.Items.Add(new Suggestion
            {
                Kind = SuggestionKind.LaunchOption, LaunchOption = option, LaunchOptionWhere = WhereToPaste(game.Source), Text = line
            });
            acted = true;
        }

        // Key=value fixes, only for keys OptiScaler.ini has in exactly one section, and not when the line is about another file.
        if (!OtherConfigFile().IsMatch(line))
        {
            foreach (Match m in IniAssignment().Matches(line))
            {
                var key = m.Groups[1].Value;
                if (!iniKeys.TryGetValue(key, out var sections) || sections.Count != 1) continue;
                var setting = new IniSetting(sections[0], key, m.Groups[2].Value);
                if (result.Items.Any(i => i.Ini == setting)) continue;
                result.Items.Add(new Suggestion { Kind = SuggestionKind.IniFix, Ini = setting, Text = line });
                acted = true;
            }
        }

        var proxy = ProxyAdvice().Match(line);
        if (proxy.Success && proxy.Groups[1].Value.ToLowerInvariant() is { } name && name != "dxgi.dll"
            && OptiScalerInstaller.ProxyNames.Contains(name))
        {
            AddProxy(result, name, line);
            acted = true;
        }

        // OptiPatcher is a separate plugin; "use OptiPatcher to use DLSS inputs" isn't advice about the in-game upscaler.
        var input = line.Contains("OptiPatcher", StringComparison.OrdinalIgnoreCase) ? Match.Empty : InGameInputAdvice().Match(line);
        if (input.Success)
        {
            var tech = Normalise(input.Groups[1].Value)
                       + (input.Groups[2].Success ? " or " + Normalise(input.Groups[2].Value) : "");
            result.InGameInput ??= tech;
            result.Items.Add(new Suggestion { Kind = SuggestionKind.InGameUpscaler, Text = line });
            acted = true;
        }

        if (!acted && !result.Items.Any(i => i.Text == line))
            result.Items.Add(new Suggestion { Kind = SuggestionKind.Text, Text = line });
    }

    private static void AddProxy(GameSuggestions result, string name, string text)
    {
        result.ProxyName ??= name;
        if (!result.Items.Any(i => i.ProxyName == name))
            result.Items.Add(new Suggestion { Kind = SuggestionKind.Proxy, ProxyName = name, Text = text });
    }

    /// <summary>Frame generation modes a line reports working, e.g. "FSR FG via Nukems" or "OptiFG -> XeFG/FSR4-FG".</summary>
    private static IEnumerable<string> FrameGenModesIn(string line)
    {
        var l = line.ToLowerInvariant();
        if (Negative(l)) yield break;
        if (l.Contains("nukem")) { yield return "nukems"; yield break; }
        if (l.Contains("optifg") || l.Contains("upscaler fg"))
        {
            if (l.Contains("xefg") || l.Contains("xess fg") || l.Contains("xess 3 fg")) yield return "optifg-xefg";
            if (Regex.IsMatch(l, @"fsr\d?(-|\s)?fg")) yield return "optifg-fsrfg";
            yield break;
        }
        if (Regex.IsMatch(l, @"dlssg via (sl|streamline)"))
        {
            yield return "dlssg-fsrfg";
            yield return "dlssg-xefg";
            yield break;
        }
        if (Regex.IsMatch(l, @"fsr ?3(\.\d)? ?fg") && l.Contains("input"))
        {
            yield return "fsrfg-fsrfg";
            yield return "fsrfg-xefg";
        }
    }

    /// <summary>Hides modes a line says don't work: "DLSSG via SL FG inputs are not supported", "Avoid FSR-FG inputs!"…</summary>
    private static void HideBrokenFrameGen(string line, HashSet<string> hidden, GameInfo game)
    {
        var l = line.ToLowerInvariant();
        if (StoreSpecific().IsMatch(l) && game.Source != GameSourceKind.Xbox) return;

        // The "doesn't work" has to be about the mode itself: "HUDfix probably won't work" says nothing about OptiFG.
        if (SaysBroken(l, @"dlssg via (?:sl|streamline)(?: fg)?(?: inputs?)?")) { hidden.Add("dlssg-fsrfg"); hidden.Add("dlssg-xefg"); }
        if (SaysBroken(l, @"fsr[- ]?(?:3(?:\.\d)? ?)?fg inputs?")) { hidden.Add("fsrfg-fsrfg"); hidden.Add("fsrfg-xefg"); }
        if (SaysBroken(l, @"nukem'?s?(?: dlssg| mod| fg)?")) hidden.Add("nukems");
        if (SaysBroken(l, @"optifg(?: \(upscaler\))?")) { hidden.Add("optifg-fsrfg"); hidden.Add("optifg-xefg"); }
    }

    /// <summary>"avoid X", or X followed closely (same clause) by "not supported", "doesn't work"…</summary>
    private static bool SaysBroken(string l, string subject) =>
        Regex.IsMatch(l, $@"\bavoid (?:using )?{subject}")
        || Regex.IsMatch(l, $@"{subject}[^.;—!]{{0,25}}\b(?:are|is)?\s*(?:not supported|isn't supported|aren't supported|unsupported|doesn't work|don't work|won't work|not working|broken)");

    private static bool Negative(string l) =>
        Regex.IsMatch(l, @"not supported|isn't supported|aren't supported|doesn't work|don't work|won't work|not work|broken|unsupported|avoid");

    /// <summary>Drops Linux-only advice, other stores' versions, and advice aimed only at other GPU vendors.</summary>
    private static bool Relevant(string line, GpuInfo? gpu, GameInfo game)
    {
        var l = line.ToLowerInvariant();
        if (LinuxOnly().IsMatch(l)) return false;
        if (StoreSpecific().IsMatch(l) && game.Source != GameSourceKind.Xbox) return false;
        if (gpu is null) return true;

        var vendors = new List<GpuVendor>();
        if (AmdWords().IsMatch(l)) vendors.Add(GpuVendor.Amd);
        if (NvidiaWords().IsMatch(l)) vendors.Add(GpuVendor.Nvidia);
        if (IntelWords().IsMatch(l)) vendors.Add(GpuVendor.Intel);
        return vendors.Count == 0 || vendors.Contains(gpu.Vendor);
    }

    private static IEnumerable<string> Sentences(string text) =>
        Regex.Split(text, @"(?<=[.!])\s+").Select(s => s.Trim()).Where(s => s.Length > 2);

    private static string Normalise(string tech) => tech.ToLowerInvariant() switch
    {
        "xess" => "XeSS",
        "dlss" => "DLSS",
        var f when f.StartsWith("fsr") => tech.ToUpperInvariant().Replace("FSR", "FSR "),
        _ => tech
    };

    [GeneratedRegex(@"`-{1,2}[a-z][\w-]*`|(?<![\w/-])-(?:force-)?(?:d3d1[12]|dx1[12]|vulkan)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LaunchOption();

    [GeneratedRegex(@"\b([A-Z][A-Za-z0-9]+)=(true|false|auto|-?\d+(?:\.\d+)?)\b")]
    private static partial Regex IniAssignment();

    [GeneratedRegex(@"GameUserSettings|Engine\.ini|Game\.ini|\.cfg\b|registry", RegexOptions.IgnoreCase)]
    private static partial Regex OtherConfigFile();

    [GeneratedRegex(@"\b(?:use|as|rename(?:d)? (?:it )?to|install(?:ed)? as)\s+`?((?:winmm|version|dbghelp|d3d12|wininet|winhttp|dxgi)\.dll)`?", RegexOptions.IgnoreCase)]
    private static partial Regex ProxyAdvice();

    [GeneratedRegex(@"(?:switch to|use|stick to|recommended to use|prefer)\s+(?:either\s+)?(xess|dlss|fsr\s?[\d.]*)\b(?:\s+or\s+(xess|dlss|fsr\s?[\d.]*)\b)?\s*(?:inputs?|in the game|as (?:the )?(?:in-game )?upscaler)?", RegexOptions.IgnoreCase)]
    private static partial Regex InGameInputAdvice();

    [GeneratedRegex(@"linux|proton|steamos|steam deck|\bwine\b|mesa|vkd3d|🐧")]
    private static partial Regex LinuxOnly();

    [GeneratedRegex(@"ms store|microsoft store|game ?pass|xbox app")]
    private static partial Regex StoreSpecific();

    [GeneratedRegex(@"\bamd\b|radeon|rdna|\brx ?\d{3,4}")]
    private static partial Regex AmdWords();

    [GeneratedRegex(@"nvidia|\brtx\b|\bgtx\b|geforce")]
    private static partial Regex NvidiaWords();

    [GeneratedRegex(@"\bintel\b|\barc\b")]
    private static partial Regex IntelWords();
}
