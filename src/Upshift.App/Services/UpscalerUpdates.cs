using Upshift.Core.Catalog;
using Upshift.Core.Components;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.App.Services;

/// <summary>A game whose DLSS file can be updated, for the Updates page checklist and the Library notice.</summary>
public sealed record DlssCandidate(GameInfo Game, string Current, string Target);

/// <summary>
/// Updating the DLSS, FSR and XeSS files games ship, and restoring the originals, for the Library and the Updates
/// page. Downloads come from the catalog's sources; each change goes through InstallRunner (UAC when the folder needs
/// it) and GameUpdates' one-at-a-time gate.
/// </summary>
public static class UpscalerUpdates
{
    private const string DlssSr = "nvngx_dlss.dll";

    /// <summary>Raised (on the downloading thread) when a newer DLSS file than any game has finished downloading.</summary>
    public static event Action<UpscalerFileSource>? DlssDownloaded;

    static UpscalerUpdates()
    {
        AppServices.Components.UpscalerFileDownloaded += source =>
        {
            if (source.File.Equals(DlssSr, StringComparison.OrdinalIgnoreCase)) DlssDownloaded?.Invoke(source);
        };
    }

    public static List<UpscalerFileItem> Items(GameInfo game) => UpscalerFiles.Items(game, AppServices.Catalog);

    /// <summary>
    /// The game's DLSS Super Resolution file when it can be updated. Games can carry several copies; this is the oldest
    /// copy that's older than the new version, or null when every copy is already up to date.
    /// </summary>
    public static UpscalerFileItem? DlssUpdate(GameInfo game) =>
        game.HasAntiCheat ? null : Items(game)
            .Where(i => i.CanUpdate && i.FileName.Equals(DlssSr, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => UpscalerFiles.ParseVersion(i.CurrentVersion ?? "0"))
            .FirstOrDefault();

    /// <summary>True when the file is already downloaded, so the update needs no download.</summary>
    public static bool IsOnThisPc(UpscalerFileSource source) => AppServices.Components.TryGetUpscalerFile(source) is not null;

    /// <summary>Every game (without anti-cheat) whose DLSS file is older than the newest one offered.</summary>
    public static List<DlssCandidate> DlssCandidates() =>
        AppServices.Library.Current
            .Where(g => !g.HasAntiCheat)
            .Select(g => (Game: g, Item: DlssUpdate(g)))
            .Where(x => x.Item is not null)
            .Select(x => new DlssCandidate(x.Game, x.Item!.CurrentVersion ?? "unknown", x.Item.Target!.Version))
            .OrderBy(c => c.Game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The DLSS files (Super Resolution and Ray Reconstruction) of a game that can be updated.</summary>
    public static List<string> DlssPaths(GameInfo game) =>
        Items(game).Where(i => i.CanUpdate && i.Family == UpscalerFamily.Dlss).Select(i => i.RelativePath).ToList();

    /// <summary>
    /// Downloads (when needed) and puts in the newer copy of each listed file, or of every file with an update when
    /// <paramref name="paths"/> is null.
    /// </summary>
    public static Task<InstallResult> UpdateAsync(GameInfo game, IReadOnlyCollection<string>? paths, IProgress<string>? progress) =>
        GameUpdates.RunExclusiveAsync(game, async () =>
        {
            if (game.HasAntiCheat) return Fail("Games with anti-cheat are never changed.");
            // Checked against the files on disk right now: copies already at the new version are simply left alone.
            var items = Items(game).Where(i => i.CanUpdate && (paths is null || paths.Contains(i.RelativePath, StringComparer.OrdinalIgnoreCase))).ToList();
            if (items.Count == 0) return new InstallResult { Success = true, Message = "Already up to date." };

            var jobs = new List<UpscalerFileJob>();
            foreach (var item in items)
            {
                var file = await AppServices.Components.EnsureUpscalerFileAsync(AppServices.Catalog, item.Target!, progress, CancellationToken.None);
                jobs.Add(new UpscalerFileJob
                {
                    Path = item.RelativePath, SourceFile = file.Path, SourceSha256 = file.Sha256,
                    Version = item.Target!.Version, Family = item.Family
                });
            }

            progress?.Report(jobs.Count == 1 ? $"Updating {items[0].FileName} in {game.Name}…" : $"Updating {jobs.Count} files in {game.Name}…");
            return await InstallRunner.RunAsync(new InstallPlan
            {
                Operation = InstallOperation.UpdateUpscalerFiles,
                GameId = game.Id,
                GameName = game.Name,
                TargetDir = game.InstallDir,
                UpscalerFiles = jobs
            });
        });

    /// <summary>Puts back the game's originals of the listed files (all of Upshift's updates when null).</summary>
    public static Task<InstallResult> RestoreAsync(GameInfo game, IReadOnlyCollection<string>? paths, IProgress<string>? progress) =>
        GameUpdates.RunExclusiveAsync(game, () =>
        {
            progress?.Report($"Putting back the original files in {game.Name}…");
            return InstallRunner.RunAsync(new InstallPlan
            {
                Operation = InstallOperation.RestoreUpscalerFiles,
                GameId = game.Id,
                GameName = game.Name,
                TargetDir = game.InstallDir,
                UpscalerFiles = paths?.Select(p => new UpscalerFileJob { Path = p }).ToList() ?? new()
            });
        });

    private static InstallResult Fail(string message) => new() { Message = message };
}
