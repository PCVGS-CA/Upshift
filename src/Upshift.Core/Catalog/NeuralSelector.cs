using Upshift.Core.Hardware;
using Upshift.Core.Models;

namespace Upshift.Core.Catalog;

public enum NeuralStatus
{
    Available,
    NeedsUserFile,
    Unavailable
}

public sealed record NeuralOption(
    NeuralStatus Status,
    NeuralBackend? Backend,
    IReadOnlyList<NeuralRuntime> Runtimes,
    string Title,
    string Message);

/// <summary>
/// Picks the DLSS 5 backend for this PC's graphics card. All the rules live in catalog.json,
/// so supporting a new card or a new fork is a catalog edit, not an app update.
/// </summary>
public static class NeuralSelector
{
    public static NeuralOption Evaluate(GpuInfo? gpu, UpscalerCatalog catalog)
    {
        if (gpu is null)
        {
            return new NeuralOption(NeuralStatus.Unavailable, null, Array.Empty<NeuralRuntime>(),
                "Graphics card not identified",
                "The app couldn't tell which graphics card this PC has, so DLSS 5 options are hidden.");
        }

        var backend = catalog.NeuralBackends.FirstOrDefault(b =>
            b.Vendor == gpu.Vendor && b.Generations.Contains(gpu.Generation, StringComparer.OrdinalIgnoreCase));

        if (backend is null)
        {
            var message = gpu.Vendor switch
            {
                GpuVendor.Intel => "No Windows DLSS 5 runtime exists for Intel cards yet. This option turns on automatically when one is released.",
                GpuVendor.Amd => $"No DLSS 5 runtime supports {gpu.Generation} graphics yet. This option turns on automatically if one is released.",
                GpuVendor.Nvidia => "DLSS 5 needs an RTX 20-series card or newer.",
                _ => "No DLSS 5 runtime supports this graphics card."
            };
            return new NeuralOption(NeuralStatus.Unavailable, null, Array.Empty<NeuralRuntime>(),
                "DLSS 5 isn't available for this card yet", message);
        }

        var runtimes = backend.Runtimes
            .Where(r => r.Generations.Contains(gpu.Generation, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (backend.NeedsNvidiaDll && !backend.RequiresSignedDll)
        {
            return new NeuralOption(NeuralStatus.NeedsUserFile, backend, runtimes,
                "DLSS 5 needs a file you supply", backend.Summary);
        }

        var runtimeNote = runtimes.Count switch
        {
            0 => "",
            1 => $" Runtime: {runtimes[0].Name}.",
            _ => $" Runtimes: {string.Join(" or ", runtimes.Select(r => r.Name))}."
        };

        return new NeuralOption(NeuralStatus.Available, backend, runtimes,
            "DLSS 5 is available on this card (experimental)", backend.Summary + runtimeNote);
    }
}
