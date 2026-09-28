using System.Runtime.InteropServices;
using System.Text.Json;
using Upshift.Core.Detection;
using Upshift.Core.Install;

namespace Upshift.Core.UserFiles;

public enum UserFileKind
{
    /// <summary>NVIDIA's DLSS 5 file (nvngx_dlssnr.dll). Used by a later phase.</summary>
    DlssNr,
    /// <summary>The community FSR 4.0.2c INT8 build (amdxcffx64.dll), recommended for AMD RX 6000.</summary>
    Fsr4Int8
}

/// <summary>A file the user supplied, copied into the app's user-files folder.</summary>
public sealed class UserFileInfo
{
    public UserFileKind Kind { get; set; }
    public string FileName { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Version { get; set; }
    public string Sha256 { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public DateTime AddedUtc { get; set; }
}

/// <summary>
/// Optional files the user supplies themselves. The app never downloads or bundles them: they're copied from wherever
/// the user points (or from a file found on their drives) into %LocalAppData%\Upshift\user-files.
/// </summary>
public sealed class UserFileStore
{
    private readonly string _dir;

    public UserFileStore(string dataDir)
    {
        _dir = System.IO.Path.Combine(dataDir, "user-files");
    }

    public string Folder => _dir;

    public static string FileNameFor(UserFileKind kind) => kind switch
    {
        UserFileKind.DlssNr => "nvngx_dlssnr.dll",
        UserFileKind.Fsr4Int8 => "amdxcffx64.dll",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public UserFileInfo? Get(UserFileKind kind)
    {
        var meta = MetaPath(kind);
        try
        {
            if (!File.Exists(meta)) return null;
            var info = JsonSerializer.Deserialize<UserFileInfo>(File.ReadAllText(meta));
            return info is not null && File.Exists(info.Path) ? info : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    /// <summary>Copies the chosen file in under its expected name and records its version and SHA-256.</summary>
    public async Task<UserFileInfo> ImportAsync(UserFileKind kind, string source)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("That file doesn't exist any more.", source);
        if (PeReader.TryRead(source) is not { Is64Bit: true })
            throw new InvalidDataException("That isn't a 64-bit Windows DLL.");

        Directory.CreateDirectory(_dir);
        var name = FileNameFor(kind);
        var target = System.IO.Path.Combine(_dir, name);
        var temp = target + ".tmp";
        await Task.Run(() => File.Copy(source, temp, overwrite: true));
        File.Move(temp, target, overwrite: true);

        var info = new UserFileInfo
        {
            Kind = kind,
            FileName = name,
            Path = target,
            Version = FileVersions.Read(target),
            Sha256 = OptiScalerInstaller.Sha256(target),
            OriginalPath = source,
            AddedUtc = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(MetaPath(kind), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
        return info;
    }

    /// <summary>Forgets the app's copy. Games that already use it keep theirs until the option is turned off.</summary>
    public void Remove(UserFileKind kind)
    {
        foreach (var path in new[] { System.IO.Path.Combine(_dir, FileNameFor(kind)), MetaPath(kind) })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
        }
    }

    /// <summary>
    /// Looks for files with this exact name (read-only) and reports each one as soon as it's found.
    /// Likely places come first — Downloads, Desktop, Documents, then the library's game folders — and only then the
    /// rest of every fixed drive, skipping the same folders the drive scan skips (Windows, WindowsApps, AppData except
    /// AppData\Local\Temp, node_modules, shader caches, the recycle bin…) and the app's own copy.
    /// Cancelling never throws: the search stops and returns what it found so far, marked as cancelled.
    /// </summary>
    public Task<FileSearchResult> FindOnDrivesAsync(string fileName, IEnumerable<string> gameFolders,
        IProgress<string>? status, IProgress<FoundFile>? found, CancellationToken ct)
    {
        var games = gameFolders.ToList();
        // No token passed to Task.Run: a token cancelled before the task starts would make awaiting it throw.
        return Task.Run(() =>
        {
            var hits = new List<FoundFile>();
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PathNormalize(_dir) };
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline
            };

            var likely = new List<(string Label, string Path)>
            {
                ("Downloads", KnownFolders.Downloads),
                ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
            };
            likely.AddRange(games.Select((g, i) => ($"game folders ({i + 1} of {games.Count})", g)));
            var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => ($"drive {d.Name.TrimEnd('\\')}", d.RootDirectory.FullName));

            foreach (var (label, root) in likely.Concat(drives))
            {
                if (ct.IsCancellationRequested) return new FileSearchResult(hits, Cancelled: true);
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) || searched.Contains(PathNormalize(root))) continue;
                status?.Report($"Looking in {label}…");
                if (!Walk(root)) return new FileSearchResult(hits, Cancelled: true);
            }
            return new FileSearchResult(hits, Cancelled: false);

            // Returns false when the search was cancelled.
            bool Walk(string root)
            {
                var stack = new Stack<string>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    if (ct.IsCancellationRequested) return false;
                    var dir = stack.Pop();
                    if (!searched.Add(PathNormalize(dir))) continue;
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(dir, fileName, options))
                        {
                            if (!seenFiles.Add(file)) continue;
                            var hit = new FoundFile(file, FileVersions.Read(file), File.GetLastWriteTime(file));
                            hits.Add(hit);
                            found?.Report(hit);
                        }
                        foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
                        {
                            var name = System.IO.Path.GetFileName(sub);
                            if (name.Equals("AppData", StringComparison.OrdinalIgnoreCase))
                            {
                                // AppData is skipped, except the temp folder where downloads sometimes get unpacked.
                                var temp = System.IO.Path.Combine(sub, "Local", "Temp");
                                if (Directory.Exists(temp)) stack.Push(temp);
                                continue;
                            }
                            if (Fingerprints.SkipOnDriveScan(name)) continue;
                            if (!searched.Contains(PathNormalize(sub))) stack.Push(sub);
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                return true;
            }
        });
    }

    private string MetaPath(UserFileKind kind) => System.IO.Path.Combine(_dir, FileNameFor(kind) + ".json");

    private static string PathNormalize(string p) => System.IO.Path.GetFullPath(p).TrimEnd('\\').ToLowerInvariant();
}

/// <summary>A file "Find it for me" found.</summary>
public sealed record FoundFile(string Path, string? Version, DateTime Modified);

/// <summary>Everything found, and whether the user stopped the search early.</summary>
public sealed record FileSearchResult(List<FoundFile> Found, bool Cancelled);

/// <summary>Windows folders .NET has no SpecialFolder value for.</summary>
internal static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The user's Downloads folder, wherever it has been moved to.</summary>
    public static string Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var path) == 0) return path;
            }
            catch (Exception) { /* fall back below */ }
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out string path);
}
