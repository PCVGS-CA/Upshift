using Upshift.Core.Models;

namespace Upshift.Core.Detection;

/// <summary>File and folder names that tell us what is inside a game folder.</summary>
public static class Fingerprints
{
    public sealed record UpscalerFile(string FileName, UpscalerFamily Family, string Feature);

    public static readonly IReadOnlyDictionary<string, UpscalerFile> UpscalerFiles = new[]
    {
        new UpscalerFile("nvngx_dlss.dll", UpscalerFamily.Dlss, "Super Resolution"),
        new UpscalerFile("nvngx_dlssd.dll", UpscalerFamily.Dlss, "Ray Reconstruction"),
        new UpscalerFile("nvngx_dlssg.dll", UpscalerFamily.Dlss, "Frame Generation"),
        new UpscalerFile("nvngx_dlssnr.dll", UpscalerFamily.Dlss, "Neural Rendering (DLSS 5)"),
        new UpscalerFile("sl.dlss.dll", UpscalerFamily.Dlss, "Super Resolution (Streamline)"),
        new UpscalerFile("sl.dlss_g.dll", UpscalerFamily.Dlss, "Frame Generation (Streamline)"),
        new UpscalerFile("sl.interposer.dll", UpscalerFamily.Streamline, "Streamline"),
        new UpscalerFile("libxess.dll", UpscalerFamily.Xess, "Super Resolution"),
        new UpscalerFile("libxess_dx11.dll", UpscalerFamily.Xess, "Super Resolution (DX11)"),
        new UpscalerFile("libxess_fg.dll", UpscalerFamily.Xess, "Frame Generation"),
        new UpscalerFile("libxell.dll", UpscalerFamily.Xess, "Low Latency (XeLL)"),
        new UpscalerFile("amd_fidelityfx_dx12.dll", UpscalerFamily.Fsr, "FSR 3.1+ (DX12)"),
        new UpscalerFile("amd_fidelityfx_vk.dll", UpscalerFamily.Fsr, "FSR 3.1+ (Vulkan)"),
        new UpscalerFile("amd_fidelityfx_loader_dx12.dll", UpscalerFamily.Fsr, "FidelityFX loader"),
        new UpscalerFile("amd_fidelityfx_upscaler_dx12.dll", UpscalerFamily.Fsr, "FSR upscaler"),
        new UpscalerFile("amd_fidelityfx_framegeneration_dx12.dll", UpscalerFamily.Fsr, "FSR frame generation"),
        new UpscalerFile("ffx_fsr2_api_x64.dll", UpscalerFamily.Fsr, "FSR 2"),
        new UpscalerFile("ffx_fsr2_api_dx12_x64.dll", UpscalerFamily.Fsr, "FSR 2 (DX12)"),
        new UpscalerFile("ffx_fsr2_api_vk_x64.dll", UpscalerFamily.Fsr, "FSR 2 (Vulkan)")
    }.ToDictionary(f => f.FileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Files that almost only ever appear in game folders. Used by the drive scan.</summary>
    private static readonly HashSet<string> GameMarkerFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityPlayer.dll",
        "steam_api64.dll",
        "steam_api.dll",
        "EOSSDK-Win64-Shipping.dll",
        "Galaxy64.dll"
    };

    /// <summary>DLL names OptiScaler (or ReShade / Special K) can be installed as.</summary>
    public static readonly string[] ProxyNames =
    {
        "dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll",
        "wininet.dll", "winhttp.dll", "d3d11.dll", "OptiScaler.asi"
    };

    public static bool IsGameMarker(string fileName) =>
        UpscalerFiles.ContainsKey(fileName)
        || GameMarkerFiles.Contains(fileName)
        || IsUnrealShippingExe(fileName);

    public static bool IsUnrealShippingExe(string fileName) =>
        fileName.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith("-WinGDK-Shipping.exe", StringComparison.OrdinalIgnoreCase);

    // ---------- Executable picking ----------

    private static readonly string[] JunkExeWords =
    {
        "unins", "setup", "installer", "redist", "vc_redist", "vcredist", "dxsetup", "dxwebsetup",
        "crashhandler", "crashreport", "crashpad", "bugreport", "easyanticheat", "beservice",
        "battleye", "ueprereq", "dotnet", "cefprocess", "qtwebengine", "unitycrashhandler",
        "7za", "helper", "touchup", "cleanup", "overlay", "start_protected_game"
    };

    private static readonly string[] LauncherExeWords = { "launcher", "prelauncher", "config", "settings" };

    /// <summary>Negative for things that are clearly not the game; lower for launchers.</summary>
    public static int ExeNamePenalty(string lowerNameWithoutExtension)
    {
        if (JunkExeWords.Any(lowerNameWithoutExtension.Contains)) return -1000;
        if (LauncherExeWords.Any(lowerNameWithoutExtension.Contains)) return -60;
        return 0;
    }

    // ---------- Folders to skip ----------

    private static readonly HashSet<string> SkipInsideGameNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "OptiScaler", ".upshift", ".pcvgs", "_CommonRedist", "__Installer", "Redist", "Redistributables",
        "DirectX", "vcredist", "Support", "Manuals", "__overlay"
    };

    /// <summary>Folders inside a game that never hold anything we care about.</summary>
    public static bool SkipInsideGame(string dirName) => SkipInsideGameNames.Contains(dirName);

    private static readonly HashSet<string> SkipOnDriveScanNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "ProgramData", "Recovery", "PerfLogs", "System Volume Information",
        "Config.Msi", "MSOCache", "AppData", "node_modules", "WindowsApps", "WinSxS", "Temp", "tmp",
        "shadercache", "compatdata", "workshop", "downloading", "Common Files", "dotnet",
        "Microsoft SDKs", "Windows Kits", "Microsoft Visual Studio", "Microsoft Office",
        "Reference Assemblies", "Windows Defender", "WindowsPowerShell", "Package Cache",
        "OneDriveTemp", "Intel", "NVIDIA Corporation", "AMD", "Microsoft", "Google", "JetBrains",
        "Docker", "Mozilla Firefox"
    };

    /// <summary>Folders the whole-drive scan never walks into.</summary>
    public static bool SkipOnDriveScan(string dirName) =>
        dirName.StartsWith('$') || dirName.StartsWith('.') || SkipOnDriveScanNames.Contains(dirName);

    // ---------- Anti-cheat ----------

    public sealed record AntiCheatRule(string Name, string[] DirNames, string[] FileNames);

    public static readonly AntiCheatRule[] AntiCheatRules =
    {
        new("Easy Anti-Cheat", new[] { "EasyAntiCheat", "EasyAntiCheat_EOS" },
            new[] { "EasyAntiCheat_EOS_Setup.exe", "EasyAntiCheat_Setup.exe", "start_protected_game.exe" }),
        new("BattlEye", new[] { "BattlEye" }, new[] { "BEService.exe", "BEService_x64.exe", "BEClient_x64.dll" }),
        new("nProtect GameGuard", new[] { "GameGuard" }, new[] { "GameMon.des", "GameMon64.des" }),
        new("XIGNCODE3", new[] { "XIGNCODE" }, new[] { "x3.xem" }),
        new("EA Javelin", Array.Empty<string>(), new[] { "EAAntiCheat.GameServiceLauncher.exe", "EAAntiCheat.Installer.exe" }),
        new("mhyprot", Array.Empty<string>(), new[] { "mhyprot2.sys", "mhyprot3.sys" }),
        new("Anti-Cheat Expert", new[] { "AntiCheatExpert" }, new[] { "ACE-Base.sys", "SGuard64.exe" }),
        new("PunkBuster", new[] { "pb" }, new[] { "PnkBstrA.exe", "PnkBstrB.exe", "pbsvc.exe" }),
        // Installed by Steam on first run from <game>\Elytra (e.g. WARDOGS); runs as the Elytra.Service service.
        new("Elytra Anti-Cheat", new[] { "Elytra" }, new[] { "Elytra-Setup.exe" })
    };

    public static string? MatchAntiCheat(string name, bool isDirectory)
    {
        foreach (var rule in AntiCheatRules)
        {
            var list = isDirectory ? rule.DirNames : rule.FileNames;
            if (list.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) return rule.Name;
        }
        return null;
    }

    /// <summary>Anti-cheat that lives outside the game folder, recognised by where the game is installed.</summary>
    public static string? AntiCheatByLocation(string installDir) =>
        installDir.Contains(@"\Riot Games\", StringComparison.OrdinalIgnoreCase) ? "Riot Vanguard" : null;
}
