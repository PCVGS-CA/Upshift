using Upshift.Core.Catalog;
using Upshift.Core.Models;
using Upshift.Core.Wiki;

namespace Upshift.Core.Detection;

/// <summary>
/// Per-game advice from the catalog (<see cref="GameNote"/>), for one card vendor: what to do before installing
/// OptiScaler, what to know once it's in, and which upscaler to pick in the game's menu.
/// </summary>
public static class GameNotes
{
    /// <summary>Said before removing OptiScaler on AMD and Intel cards, where the game's DLSS option only works through OptiScaler.</summary>
    public const string RemoveWarning = "If the game is set to DLSS in its menu, switch it to XeSS, FSR or TAA first, or it may crash at start-up. " +
                                        "If it already does, launch it once in DX11 mode (-dx11) and change the upscaler.";

    /// <summary>True on cards where a game left on DLSS has nothing to run it once OptiScaler is gone.</summary>
    public static bool WarnBeforeRemoving(GpuVendor? vendor) => vendor is GpuVendor.Amd or GpuVendor.Intel;

    public static string VendorName(GpuVendor? vendor) => vendor switch
    {
        GpuVendor.Amd => "AMD", GpuVendor.Nvidia => "NVIDIA", GpuVendor.Intel => "Intel", _ => "these"
    };

    /// <summary>
    /// The note for this game on this vendor's cards, or null. A note with exe names matches by the game's exe only (store
    /// names vary, and an exe name tells a remake from the original); otherwise by title.
    /// </summary>
    public static GameNote? For(GameInfo game, GpuVendor? vendor, IEnumerable<GameNote>? notes)
    {
        if (notes is null || vendor is null) return null;
        var exe = game.ExePath is { } path ? Path.GetFileName(path) : null;
        var key = WikiMatcher.Key(game.Name);
        foreach (var note in notes)
        {
            if (!note.Vendors.Any(v => v.Equals(vendor.ToString(), StringComparison.OrdinalIgnoreCase))) continue;
            var matches = note.ExeNames.Count > 0
                ? exe is not null && note.ExeNames.Any(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase))
                : key.Length > 0 && note.Titles.Any(t => WikiMatcher.Key(t) == key);
            if (matches) return note;
        }
        return null;
    }
}
