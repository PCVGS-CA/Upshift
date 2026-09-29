using Upshift.Core.Models;
using Upshift.Core.Util;

namespace Upshift.Core.Detection;

/// <summary>Looks inside one game folder and works out everything the Library shows about it.</summary>
public static class GameAnalyzer
{
    /// <summary>
    /// How deep the exe, engine and anti-cheat checks look. Upscaler DLLs are searched for in the whole folder:
    /// Unreal games keep them in plugin folders 7 to 10 levels down (Plugins\NVIDIA\DLSS\Binaries\ThirdParty\Win64).
    /// </summary>
    private const int ExeMaxDepth = 6;

    /// <summary>A launcher-suggested exe smaller than this is almost always a launcher or stub.</summary>
    private const long SmallExeBytes = 5L * 1024 * 1024;

    /// <param name="exeOverride">The main exe the user chose for this game, if any. Used as long as the file exists.</param>
    public static GameInfo Analyze(DiscoveredGame game, CancellationToken ct, string? exeOverride = null)
    {
        var info = new GameInfo
        {
            Id = PathUtil.StableId(game.InstallDir),
            Name = game.Name,
            Source = game.Source,
            SourceId = game.SourceId,
            InstallDir = game.InstallDir,
            LaunchExePath = game.ExePath is { } launch && File.Exists(launch) ? launch : null,
            ArtworkPath = game.ArtworkPath,
            ArtworkSource = game.ArtworkPath is null ? null : "Steam",
            ScannedUtc = DateTime.UtcNow
        };

        if (!PathUtil.SafeDirectoryExists(game.InstallDir)) return info;

        var exes = new List<string>();
        var upscalerDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var antiCheat = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        bool unity = false, unreal = false, source2 = false, cryEngine = false;
        bool redLauncher = false, redBundles = false, redArchives = false;

        foreach (var entry in FileWalker.Walk(game.InstallDir, int.MaxValue, int.MaxValue, ct, Fingerprints.SkipInsideGame))
        {
            var name = Path.GetFileName(entry.Path);
            var shallow = entry.Depth <= (entry.IsDirectory ? ExeMaxDepth + 1 : ExeMaxDepth);

            if (shallow)
            {
                var ac = Fingerprints.MatchAntiCheat(name, entry.IsDirectory);
                if (ac is not null) antiCheat.Add(ac);
            }

            if (entry.IsDirectory)
            {
                if (entry.Depth <= 2 && name.Equals("Engine", StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(Path.Combine(entry.Path, "Binaries")))
                {
                    unreal = true;
                }
                continue;
            }

            if (Fingerprints.UpscalerFiles.TryGetValue(name, out var upscaler))
            {
                info.Upscalers.Add(new UpscalerDll
                {
                    FileName = name,
                    FullPath = entry.Path,
                    RelativePath = Path.GetRelativePath(game.InstallDir, entry.Path),
                    Family = upscaler.Family,
                    Feature = upscaler.Feature,
                    Version = FileVersions.Read(entry.Path)
                });
                upscalerDirs.Add(Path.GetDirectoryName(entry.Path)!);
            }

            if (!shallow) continue;

            if (name.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase)) unity = true;
            else if (name.Equals("engine2.dll", StringComparison.OrdinalIgnoreCase)) source2 = true;
            else if (name.Equals("CrySystem.dll", StringComparison.OrdinalIgnoreCase)) cryEngine = true;
            else if (name.Equals("REDprelauncher.exe", StringComparison.OrdinalIgnoreCase)) redLauncher = true;
            else if (!redBundles && name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)
                     && IsUnder(game.InstallDir, entry.Path, "content")) redBundles = true;
            else if (!redArchives && name.EndsWith(".archive", StringComparison.OrdinalIgnoreCase)
                     && IsUnder(game.InstallDir, entry.Path, @"archive\pc\content")) redArchives = true;

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                exes.Add(entry.Path);
                if (Fingerprints.IsUnrealShippingExe(name)) unreal = true;
            }
        }

        // Upscaler files Upshift updated that a game update (or "verify files") has since put back.
        info.UpscalerFilesRestoredByGame = Install.UpscalerFiles.RestoredByGame(game.InstallDir);

        var byLocation = Fingerprints.AntiCheatByLocation(game.InstallDir);
        if (byLocation is not null) antiCheat.Add(byLocation);
        info.AntiCheat = antiCheat.ToList();

        info.Upscalers = info.Upscalers
            .OrderBy(u => u.Family)
            .ThenBy(u => u.Feature, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var chosen = !string.IsNullOrEmpty(exeOverride) && File.Exists(exeOverride);
        var exe = chosen ? exeOverride : PickExe(game, exes, upscalerDirs);
        info.ExeChosenByUser = chosen;
        if (exe is not null)
        {
            info.ExePath = exe;
            info.TargetDir = Path.GetDirectoryName(exe);

            var pe = PeReader.TryRead(exe);
            if (pe is not null)
            {
                info.Is64Bit = pe.Is64Bit;
                info.Api = ApiFromImports(pe.Imports);
            }

            // Unity keeps the renderer in UnityPlayer.dll, not the tiny game exe.
            var unityPlayer = Path.Combine(info.TargetDir!, "UnityPlayer.dll");
            if (File.Exists(unityPlayer))
            {
                var unityPe = PeReader.TryRead(unityPlayer);
                if (unityPe is not null) info.Api |= ApiFromImports(unityPe.Imports);
            }

            // Games using the DirectX 12 Agility SDK ship a D3D12 folder next to the exe.
            if (Directory.Exists(Path.Combine(info.TargetDir!, "D3D12"))) info.Api |= GraphicsApi.D3D12;

            info.ExistingMods = ModDetector.Detect(info.TargetDir!);
            // An OptiScaler this app installed: are its files still as we left them?
            info.RepairProblems = Install.OptiScalerInstaller.Verify(info.TargetDir!);
        }

        info.Engine = unreal ? UnrealVersion.Detect(game.InstallDir, exe)
            : unity ? "Unity"
            : source2 ? "Source 2"
            : cryEngine ? "CryEngine"
            : redArchives ? "REDengine 4"
            : redBundles ? "REDengine 3"
            : redLauncher ? "REDengine"
            : "Unknown";

        return info;
    }

    private static string? PickExe(DiscoveredGame game, List<string> exes, HashSet<string> upscalerDirs)
    {
        // Unreal games often launch through a small exe at the top that starts the real one in Binaries\Win64.
        var shipping = exes.FirstOrDefault(e => Fingerprints.IsUnrealShippingExe(Path.GetFileName(e)));
        if (shipping is not null) return shipping;

        // The launcher's exe is only a suggestion (GOG lists REDprelauncher.exe for The Witcher 3, for example).
        var hint = game.ExePath is { } h && File.Exists(h) ? h : null;
        if (hint is not null && IsTrustworthyHint(hint, upscalerDirs)) return hint;

        var candidates = new List<string>(exes);
        if (hint is not null && !candidates.Contains(hint, StringComparer.OrdinalIgnoreCase)) candidates.Add(hint);

        var folderKey = Compact(Path.GetFileName(game.InstallDir.TrimEnd('\\')));

        int Score(string path)
        {
            var nameLower = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            var score = Fingerprints.ExeNamePenalty(nameLower);
            if (score <= -500) return score;

            // Strongly prefer a 64-bit exe sitting next to the game's upscaler DLLs: that's the one the game renders with.
            var is64 = PeReader.TryRead(path)?.Is64Bit;
            if (is64 == true) score += 60;
            else if (is64 == false) score -= 60;
            if (upscalerDirs.Contains(Path.GetDirectoryName(path)!)) score += is64 == true ? 60 : 20;

            // Games with a DX11 and a DX12 build (bin\x64 and bin\x64_dx12): take DX12.
            if (IsDx12Folder(Path.GetRelativePath(game.InstallDir, Path.GetDirectoryName(path)!))) score += 25;

            var compact = Compact(nameLower);
            if (folderKey.Length > 2 && compact.Length > 2 && (compact.Contains(folderKey) || folderKey.Contains(compact)))
                score += 30;

            if (path.Contains(@"\Binaries\Win64\", StringComparison.OrdinalIgnoreCase)) score += 20;

            try { score += (int)Math.Min(40, new FileInfo(path).Length / (5L * 1024 * 1024)); } catch { }

            if (hint is not null && path.Equals(hint, StringComparison.OrdinalIgnoreCase)) score += 10;

            score -= Path.GetRelativePath(game.InstallDir, path).Count(c => c == '\\') * 2;
            return score;
        }

        return candidates
            .Select(e => (Path: e, Score: Score(e)))
            .Where(x => x.Score > -500)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    /// <summary>
    /// A launcher's exe is used as-is only if it looks like the real game: not tiny, 64-bit, not named "launcher",
    /// and in the same folder as the upscaler DLLs (when the game has any).
    /// </summary>
    private static bool IsTrustworthyHint(string hint, HashSet<string> upscalerDirs)
    {
        if (Path.GetFileName(hint).Contains("launcher", StringComparison.OrdinalIgnoreCase)) return false;
        try { if (new FileInfo(hint).Length < SmallExeBytes) return false; } catch { return false; }
        if (PeReader.TryRead(hint)?.Is64Bit != true) return false;
        return upscalerDirs.Count == 0 || upscalerDirs.Contains(Path.GetDirectoryName(hint)!);
    }

    /// <summary>A folder named for DirectX 12, like "x64_dx12", "DX12" or "bin-dx12".</summary>
    private static bool IsDx12Folder(string relativeDir) =>
        relativeDir.Split('\\', '/').Any(part =>
            part.Equals("dx12", StringComparison.OrdinalIgnoreCase)
            || part.EndsWith("_dx12", StringComparison.OrdinalIgnoreCase)
            || part.EndsWith("-dx12", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when file sits somewhere below installDir\relativeFolder.</summary>
    private static bool IsUnder(string installDir, string file, string relativeFolder) =>
        Path.GetRelativePath(installDir, file).StartsWith(relativeFolder + "\\", StringComparison.OrdinalIgnoreCase);

    private static string Compact(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static GraphicsApi ApiFromImports(IEnumerable<string> imports)
    {
        var api = GraphicsApi.None;
        foreach (var dll in imports)
        {
            switch (dll.ToLowerInvariant())
            {
                case "d3d12.dll": api |= GraphicsApi.D3D12; break;
                case "d3d11.dll": api |= GraphicsApi.D3D11; break;
                case "d3d10.dll":
                case "d3d10_1.dll": api |= GraphicsApi.D3D10; break;
                case "d3d9.dll": api |= GraphicsApi.D3D9; break;
                case "vulkan-1.dll": api |= GraphicsApi.Vulkan; break;
                case "opengl32.dll": api |= GraphicsApi.OpenGL; break;
            }
        }
        return api;
    }
}
