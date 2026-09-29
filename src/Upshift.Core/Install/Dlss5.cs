using System.Globalization;
using Upshift.Core.Catalog;
using Upshift.Core.Detection;
using Upshift.Core.Hardware;
using Upshift.Core.Models;

namespace Upshift.Core.Install;

/// <summary>What Upshift can tell about a DLSS 5 file (nvngx_dlssnr.dll) the user supplied.</summary>
public sealed record Dlss5File(bool OfficialNvidia, string? Version, string Sha256, string? KnownFrom)
{
    /// <summary>"Official NVIDIA file (signed, version 310.8.0.0)" or "Modified file: not signed by NVIDIA, …".</summary>
    public string Label => OfficialNvidia
        ? $"Official NVIDIA file (signed, version {Version ?? "unknown"}){(KnownFrom is null ? "" : $" · the same file as in {KnownFrom}")}"
        : "Modified file: not signed by NVIDIA, can't be verified as safe";
}

/// <summary>Everything the DLSS 5 section needs to say about one game on this PC.</summary>
public sealed class Dlss5Status
{
    public NeuralOption Option { get; init; } = null!;
    /// <summary>Reasons the switch can't be made (it's disabled while there are any).</summary>
    public List<string> Blockers { get; } = new();
    /// <summary>Things that may stop it working or that the user should know, without blocking the switch.</summary>
    public List<string> Warnings { get; } = new();
    /// <summary>AMD-NR's runtime for this card (the first the catalog lists for its generation).</summary>
    public NeuralRuntime? PreferredRuntime { get; init; }
    public bool IsAmd => Option.Backend?.Vendor == GpuVendor.Amd;
    public bool CanSwitch => Option.Backend is not null && Blockers.Count == 0;
}

/// <summary>DLSS 5 (Neural Rendering): the rules for the user's file, the card, the driver and the game.</summary>
public static class Dlss5
{
    public const string Section = "DlssNr";
    public const string NvidiaFileName = "nvngx_dlssnr.dll";
    private static readonly string[] NvidiaSigner = { "NVIDIA Corporation" };

    /// <summary>Signature, version and SHA-256 of the user's nvngx_dlssnr.dll, compared with the catalog's known files.</summary>
    public static Dlss5File CheckFile(string path, UpscalerCatalog catalog)
    {
        var sha = OptiScalerInstaller.Sha256(path);
        var known = catalog.KnownNvidiaNrFiles.FirstOrDefault(k => k.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase));
        return new Dlss5File(SignatureCheck.Problem(path, NvidiaSigner) is null, FileVersions.Read(path), sha, known?.Source);
    }

    /// <summary>The one plain sentence beside the DLSS 5 file row, for this card.</summary>
    public static string FileRule(NeuralOption option) => option.Backend switch
    {
        { Vendor: GpuVendor.Nvidia, RequiresSignedDll: true } => "Your card is an RTX 50: it needs NVIDIA's official, signed file.",
        { Vendor: GpuVendor.Nvidia } => "RTX 20, 30 and 40 cards need a modified file, because NVIDIA's official DLSS 5 file only runs on RTX 50 cards.",
        { Vendor: GpuVendor.Amd } => "Not needed on your card: AMD cards run DLSS 5 through AMD-NR, which doesn't use this file.",
        _ => "DLSS 5 isn't available for your card yet, so this file isn't used."
    };

    /// <summary>
    /// The NVIDIA driver number from the Windows driver version: the last five digits of its last two parts,
    /// e.g. 32.0.16.1692 → 616.92 and 32.0.15.6094 → 560.94. Null when it isn't an NVIDIA-style version.
    /// </summary>
    public static string? NvidiaDriver(string? windowsVersion)
    {
        if (windowsVersion is null) return null;
        var parts = windowsVersion.Split('.');
        if (parts.Length != 4 || !parts.All(p => p.All(char.IsDigit) && p.Length > 0)) return null;
        var digits = parts[2] + parts[3].PadLeft(4, '0');
        if (digits.Length < 5) return null;
        var last = digits[^5..];
        return $"{int.Parse(last[..3], CultureInfo.InvariantCulture)}.{last[3..]}";
    }

    /// <summary>Works out whether this game can switch to the DLSS 5 build on this card, and what to warn about.</summary>
    public static Dlss5Status Evaluate(GameInfo game, GpuInfo? gpu, UpscalerCatalog catalog, Dlss5File? userFile, IniFile? ini)
    {
        var option = NeuralSelector.Evaluate(gpu, catalog);
        var status = new Dlss5Status { Option = option, PreferredRuntime = option.Runtimes.FirstOrDefault() };
        var backend = option.Backend;

        if (game.HasAntiCheat) status.Blockers.Add("This game uses anti-cheat, so nothing is installed or changed in it.");
        if (backend is null)
        {
            status.Blockers.Add(option.Message);
            return status;
        }

        if (backend.Vendor == GpuVendor.Nvidia)
        {
            // Driver
            var driver = NvidiaDriver(gpu?.DriverVersion);
            if (backend.MinNvidiaDriver is { } min)
            {
                if (driver is null) status.Warnings.Add($"Upshift couldn't read your NVIDIA driver version. DLSS 5 needs driver {min} or newer.");
                else if (UpscalerFiles.ParseVersion(driver) < UpscalerFiles.ParseVersion(min))
                    status.Blockers.Add($"Your NVIDIA driver is {driver}; DLSS 5 needs {min} or newer. Update it with the NVIDIA App or from nvidia.com.");
            }

            // The user's file, by the catalog's rule for this card
            if (userFile is null)
                status.Blockers.Add("Add your DLSS 5 file (nvngx_dlssnr.dll) in Settings > Optional files you supply first.");
            else if (backend.RequiresSignedDll && !userFile.OfficialNvidia)
                status.Blockers.Add("Your card is an RTX 50: it needs NVIDIA's official, signed DLSS 5 file, and the one you added isn't signed by NVIDIA.");
            else if (!backend.RequiresSignedDll && userFile.OfficialNvidia)
                status.Blockers.Add("NVIDIA's official DLSS 5 file only runs on RTX 50 cards. Your card needs a modified file.");

            if (!gpu!.Generation.Equals("Blackwell", StringComparison.OrdinalIgnoreCase))
                status.Warnings.Add("DLSS 5 is very demanding on this card. Start with the model resolution at 75%.");
        }

        // DirectX 11 (and Vulkan with AMD-NR) reach the DirectX 12 pass through OptiScaler's bridge: a "w/Dx12" upscaler.
        if (!game.Api.HasFlag(GraphicsApi.D3D12))
        {
            var amd = backend.Vendor == GpuVendor.Amd;
            if (game.Api.HasFlag(GraphicsApi.D3D11) && !IsDx12Bridge(ini?.Get("Upscalers", "Dx11Upscaler"), amd))
                status.Warnings.Add("This is a DirectX 11 game: DLSS 5 runs on DirectX 12, so choose an upscaler marked \"w/Dx12\" in OptiScaler (for example XeSS w/Dx12 or FSR 3.1 w/Dx12).");
            else if (amd && game.Api.HasFlag(GraphicsApi.Vulkan) && !IsDx12Bridge(ini?.Get("Upscalers", "VulkanUpscaler"), amd))
                status.Warnings.Add("This is a Vulkan game: AMD's DLSS 5 runs on DirectX 12, so choose an upscaler marked \"w/Dx12\" in OptiScaler (FSR w/Dx12).");
        }
        return status;
    }

    /// <summary>A "w/Dx12" upscaler (xess_12, fsr31_12…); "auto" counts for AMD-NR, which picks one by itself.</summary>
    private static bool IsDx12Bridge(string? value, bool autoPicks) =>
        value is null ? autoPicks
        : value.EndsWith("_12", StringComparison.OrdinalIgnoreCase) || (autoPicks && value.Equals("auto", StringComparison.OrdinalIgnoreCase));

    /// <summary>The toggle key's name ("Home", "F9"), or null when none is set.</summary>
    public static string? HotkeyName(IniFile? ini, NeuralBackend? backend)
    {
        var raw = ini?.Get(Section, "ToggleKey");
        int? code = raw is null || raw.Equals("auto", StringComparison.OrdinalIgnoreCase) ? backend?.ToggleKey : ParseKey(raw);
        return code is null or <= 0 ? null : KeyName(code.Value);
    }

    private static int? ParseKey(string raw) =>
        raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : null
            : int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) ? dec : null;

    /// <summary>A Windows virtual-key code's name: 0x24 → "Home", 0x78 → "F9".</summary>
    public static string KeyName(int vk) => vk switch
    {
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x13 => "Pause", 0x14 => "Caps Lock", 0x1B => "Esc", 0x20 => "Space",
        0x21 => "Page Up", 0x22 => "Page Down", 0x23 => "End", 0x24 => "Home", 0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        0x2C => "Print Screen", 0x2D => "Insert", 0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0x90 => "Num Lock", 0x91 => "Scroll Lock",
        _ => $"key 0x{vk:X2}"
    };
}
