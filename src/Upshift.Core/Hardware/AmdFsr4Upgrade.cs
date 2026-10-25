using System.Text.Json;
using Upshift.Core.Models;

namespace Upshift.Core.Hardware;

public enum Fsr4UpgradeOffer
{
    /// <summary>Not an AMD card the upgrade is offered on (RX 9000, or RX 7000 / RX 6000 with Adrenalin 26.10.1+).</summary>
    NotThisCard,
    /// <summary>An RX 7000 / RX 6000 whose Adrenalin is older than 26.10.1 (or unknown).</summary>
    NeedsDriver,
    /// <summary>Not a DirectX 12 game with its own FSR 3.1 or newer DLL.</summary>
    NotThisGame,
    /// <summary>Offered (RX 9000).</summary>
    Offered,
    /// <summary>Offered, still in testing (RX 7000 / RX 6000 with Adrenalin 26.10.1+).</summary>
    OfferedTesting
}

/// <summary>One game Upshift allowed AMD's FSR 4 upgrade for (data\amd-fsr4-upgrade.json), so it can be turned off again.</summary>
public sealed class Fsr4UpgradeRecord
{
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    /// <summary>The game's exe file name and its folder, exactly as given to the driver.</summary>
    public string FileName { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime AllowedUtc { get; set; }
}

/// <summary>
/// "Allow AMD's FSR 4 upgrade": AMD's driver upgrades a game's own FSR 3.1 to FSR 4 when the game's exe is in its
/// FSROVR whitelist. Upshift adds the exe with AMD's ADL application profiles (driver area "FSROVR", profile
/// "FsrOvrWhitelistProfile") and removes it again; no game file is touched. This class decides who gets the switch and
/// keeps the record of what Upshift added; <see cref="AdlProfiles"/> talks to the driver.
/// </summary>
public static class AmdFsr4Upgrade
{
    public const string DriverArea = "FSROVR";
    public const string ProfileName = "FsrOvrWhitelistProfile";
    public const string MinAdrenalin = "26.10.1";

    /// <summary>The game's own FSR 3.1+ DLLs (the FSR 3.1 API's and the FidelityFX SDK 2's), not OptiScaler's copies.</summary>
    public static readonly string[] Fsr31Files = { "amd_fidelityfx_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll" };

    public static Fsr4UpgradeOffer For(GpuInfo? gpu, string? adrenalin, GameInfo game, IReadOnlyCollection<string> optiScalerFiles)
    {
        if (gpu is not { Vendor: GpuVendor.Amd, IsIntegrated: false }) return Fsr4UpgradeOffer.NotThisCard;
        var testing = gpu.Generation is "RDNA 3" or "RDNA 2";
        if (gpu.Generation != "RDNA 4" && !testing) return Fsr4UpgradeOffer.NotThisCard;
        if (!HasOwnFsr31(game, optiScalerFiles)) return Fsr4UpgradeOffer.NotThisGame;
        if (testing && DriverDetails.CompareVersions(adrenalin, MinAdrenalin) is not >= 0) return Fsr4UpgradeOffer.NeedsDriver;
        return testing ? Fsr4UpgradeOffer.OfferedTesting : Fsr4UpgradeOffer.Offered;
    }

    /// <summary>A DirectX 12 game with its own FSR 3.1+ DLL. <paramref name="optiScalerFiles"/> are full paths OptiScaler added.</summary>
    public static bool HasOwnFsr31(GameInfo game, IReadOnlyCollection<string> optiScalerFiles) =>
        game.ExePath is not null && game.Api.HasFlag(GraphicsApi.D3D12)
        && game.Upscalers.Any(u => Fsr31Files.Contains(u.FileName, StringComparer.OrdinalIgnoreCase)
                                   && !optiScalerFiles.Contains(u.FullPath, StringComparer.OrdinalIgnoreCase));

    // ---------------- the record ----------------

    public static string RecordPath(string dataDir) => System.IO.Path.Combine(dataDir, "amd-fsr4-upgrade.json");

    public static List<Fsr4UpgradeRecord> Records(string dataDir)
    {
        try { return JsonSerializer.Deserialize<List<Fsr4UpgradeRecord>>(File.ReadAllText(RecordPath(dataDir))) ?? new(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(); }
    }

    public static Fsr4UpgradeRecord? RecordFor(string dataDir, string gameId) => Records(dataDir).FirstOrDefault(r => r.GameId == gameId);

    public static void SaveRecords(string dataDir, List<Fsr4UpgradeRecord> records)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(RecordPath(dataDir), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Adds the game's exe to the driver's FSR 4 list and records it. Returns why it failed, or null.</summary>
    public static string? Allow(string dataDir, GameInfo game)
    {
        if (game.ExePath is not { } exe) return "Upshift doesn't know which program is this game's.";
        var (file, folder) = (System.IO.Path.GetFileName(exe), System.IO.Path.GetDirectoryName(exe) ?? "");
        using var adl = AdlProfiles.Open(out var openProblem);
        if (adl is null) return openProblem;
        if (adl.Assign(file, folder, game.Name, DriverArea, ProfileName) is { } problem) return problem;
        var records = Records(dataDir);
        records.RemoveAll(r => r.GameId == game.Id);
        records.Add(new Fsr4UpgradeRecord { GameId = game.Id, GameName = game.Name, FileName = file, Path = folder, AllowedUtc = DateTime.UtcNow });
        SaveRecords(dataDir, records);
        return null;
    }

    /// <summary>Takes back what Upshift added for this game (only that). Returns why it failed, or null.</summary>
    public static string? Remove(string dataDir, Fsr4UpgradeRecord record)
    {
        using var adl = AdlProfiles.Open(out var openProblem);
        if (adl is null) return openProblem;
        if (adl.Remove(record.FileName, record.Path, DriverArea) is { } problem) return problem;
        var records = Records(dataDir);
        records.RemoveAll(r => r.GameId == record.GameId);
        SaveRecords(dataDir, records);
        return null;
    }
}
