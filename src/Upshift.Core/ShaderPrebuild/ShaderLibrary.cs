using System.Diagnostics;
using System.Text;
using Upshift.Core.Install;
using Upshift.Core.Models;

namespace Upshift.Core.ShaderPrebuild;

/// <summary>
/// "Pre-build shaders" (experimental): Upshift's recorder (upshift_rec.asi, loaded by OptiScaler) learns which pipelines
/// a game builds while it's played; Upshift merges each finished recording into one library per game and graphics card
/// model; and upshift_prebuild.exe rebuilds them all when the driver's shader cache was wiped. It restores pipelines
/// already learned, nothing else: it never reads shaders from game files, and ray tracing pipelines aren't covered.
/// Everything lives in %LocalAppData%\Upshift\shaderwarm and never leaves the PC.
/// <code>
/// shaderwarm\plugins\upshift_rec.asi        the recorder OptiScaler loads (Upshift copies it here)
/// shaderwarm\&lt;program&gt;\recordings\&lt;stamp&gt;\  one game session: session.txt, timing.csv, refused.bin, pipelines.bin
/// shaderwarm\&lt;program&gt;\blobs\&lt;hash&gt;.bin     shader bytecode and root signatures, each stored once
/// shaderwarm\&lt;program&gt;\library\&lt;model&gt;.bin  the merged library for one card model (read by upshift_prebuild.exe)
/// shaderwarm\&lt;program&gt;\replays\&lt;stamp&gt;.txt    what each pre-build did
/// </code>
/// </summary>
public static class ShaderLibrary
{
    public static readonly byte[] RecordMagic = Encoding.ASCII.GetBytes("USWREC1\0");
    public static readonly byte[] LibraryMagic = Encoding.ASCII.GetBytes("USWLIB1\0");

    /// <summary>Recordings kept for the report (the newest; older ones go once merged).</summary>
    public const int SessionsKept = 20;

    public static string Root(string dataDir) => Path.Combine(dataDir, "shaderwarm");
    public static string PluginsDir(string dataDir) => Path.Combine(Root(dataDir), "plugins");
    public static string RecorderPath(string dataDir) => Path.Combine(PluginsDir(dataDir), "upshift_rec.asi");

    /// <summary>The game's folder: named after its program, which is what the recorder in the game sees.</summary>
    public static string GameDir(string dataDir, string program) => Path.Combine(Root(dataDir), Path.GetFileName(program));

    /// <summary>The game's program as the recorder names it: the main game file's name ("re9.exe").</summary>
    public static string? ProgramOf(GameInfo game) => game.ExePath is { Length: > 0 } exe ? Path.GetFileName(exe) : null;

    // ---------------- learning: the two OptiScaler.ini values ----------------

    /// <summary>The OptiScaler.ini values that load the recorder ([Plugins] Path and LoadAsiPlugins).</summary>
    public static List<IniSetting> LearnSettings(string dataDir) => new()
    {
        new IniSetting("Plugins", "Path", PluginsDir(dataDir)),
        new IniSetting("Plugins", "LoadAsiPlugins", "true"),
    };

    /// <summary>"Put back what was there": Configure restores each value's original (null).</summary>
    public static List<IniSetting> PutBackSettings() => new()
    {
        new IniSetting("Plugins", "Path", null),
        new IniSetting("Plugins", "LoadAsiPlugins", null),
    };

    /// <summary>OptiScaler's own defaults, for a value that was already the recorder's before Upshift recorded it.</summary>
    public static List<IniSetting> DefaultSettings() => new()
    {
        new IniSetting("Plugins", "Path", OptiScalerOptions.Auto),
        new IniSetting("Plugins", "LoadAsiPlugins", OptiScalerOptions.Auto),
    };

    /// <summary>The game's OptiScaler.ini loads the recorder.</summary>
    public static bool IsLearning(IniFile ini, string dataDir) =>
        string.Equals(ini.Get("Plugins", "Path")?.Trim().TrimEnd('\\'), PluginsDir(dataDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        && string.Equals(ini.Get("Plugins", "LoadAsiPlugins")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies Upshift's recorder into the plugins folder (when it isn't there, or is another version). False with a reason
    /// when it can't be replaced, e.g. a game has the old one loaded.
    /// </summary>
    public static (bool Ok, string? Problem) InstallRecorder(string appRecorder, string dataDir)
    {
        try
        {
            if (!File.Exists(appRecorder)) return (false, "Upshift's recorder (ShaderPrebuild\\upshift_rec.asi) is missing from its program folder.");
            var target = RecorderPath(dataDir);
            Directory.CreateDirectory(PluginsDir(dataDir));
            if (File.Exists(target) && SameFile(appRecorder, target)) return (true, null);
            File.Copy(appRecorder, target + ".tmp", overwrite: true);
            File.Move(target + ".tmp", target, overwrite: true);
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"The recorder couldn't be put in place ({ex.Message}). If a game is running, close it and try again.");
        }
    }

    /// <summary>Removes the recorder from the plugins folder (when the feature is switched off and no game learns).</summary>
    public static void RemoveRecorder(string dataDir)
    {
        try { if (File.Exists(RecorderPath(dataDir))) File.Delete(RecorderPath(dataDir)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        return System.Security.Cryptography.SHA256.HashData(sa).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(sb));
    }

    // ---------------- recordings ----------------

    /// <summary>A card a recording names: its PCI identity (survives restarts) and the driver's name for it.</summary>
    public sealed record CardModel(uint Vendor, uint Device, uint SubVendor, uint SubSystem, uint Revision, string Name, bool Software)
    {
        /// <summary>"10de-2786-1458-40f3-a1": the library file's name.</summary>
        public string Key => $"{Vendor:x4}-{Device:x4}-{SubVendor:x4}-{SubSystem:x4}-{Revision:x2}";
        public string ModelLine => $"{Vendor:x4} {Device:x4} {SubVendor:x4} {SubSystem:x4} {Revision:x2}";
    }

    /// <summary>What session.txt says: the program, its process id, the cards by LUID, whether it's merged.</summary>
    public sealed record SessionInfo(string Folder, string? Program, int? ProcessId, Dictionary<string, CardModel?> Cards, bool Merged, bool RayTracingScene)
    {
        public string Name => Path.GetFileName(Folder);
        public DateTime? Started => ParseStamp(Name);
    }

    public static DateTime? ParseStamp(string stamp) =>
        DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var t) ? t : null;

    public static SessionInfo ReadSession(string folder)
    {
        string? program = null;
        int? pid = null;
        var cards = new Dictionary<string, CardModel?>(StringComparer.OrdinalIgnoreCase);
        var merged = false;
        var rt = false;
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(folder, "session.txt")))
            {
                var f = line.Split('\t');
                switch (f[0])
                {
                    case "program" when f.Length > 1: program = f[1]; break;
                    case "process_id" when f.Length > 1 && int.TryParse(f[1], out var p): pid = p; break;
                    case "merged": merged = true; break;
                    case "raytracing_scene": rt = true; break;
                    case "adapter" when f.Length >= 5:
                        var ids = f[2].Split(' ');
                        cards[f[1]] = ids.Length == 5 && ids.All(x => uint.TryParse(x, System.Globalization.NumberStyles.HexNumber, null, out _))
                            ? new CardModel(Hex(ids[0]), Hex(ids[1]), Hex(ids[2]), Hex(ids[3]), Hex(ids[4]), f[4], f[3] == "1")
                            : null;
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new SessionInfo(folder, program, pid, cards, merged, rt);
        static uint Hex(string s) => uint.Parse(s, System.Globalization.NumberStyles.HexNumber);
    }

    public static List<SessionInfo> Sessions(string gameDir)
    {
        try
        {
            var dir = Path.Combine(gameDir, "recordings");
            return Directory.Exists(dir)
                ? Directory.EnumerateDirectories(dir).Where(d => File.Exists(Path.Combine(d, "timing.csv"))).OrderBy(d => d, StringComparer.Ordinal).Select(ReadSession).ToList()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>The game that recorded this session is still running (its recording isn't finished).</summary>
    public static bool StillRecording(SessionInfo s)
    {
        if (s.ProcessId is not { } pid || s.Program is not { } program) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && string.Equals(p.ProcessName, Path.GetFileNameWithoutExtension(program), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>One record of pipelines.bin or a library: kind, id and payload, exactly as stored.</summary>
    public sealed record StoredRecord(uint Kind, ulong Id, byte[] Payload);

    /// <summary>Reads records after a header (stops quietly at a cut-off end, e.g. when the game was killed).</summary>
    public static IEnumerable<StoredRecord> ReadRecords(Stream stream)
    {
        var head = new byte[16];
        while (true)
        {
            if (stream.ReadAtLeast(head, 16, throwOnEndOfStream: false) < 16) yield break;
            var kind = BitConverter.ToUInt32(head, 0);
            var id = BitConverter.ToUInt64(head, 4);
            var size = BitConverter.ToUInt32(head, 12);
            if (size > 64 * 1024 * 1024) yield break;
            var payload = new byte[size];
            if (stream.ReadAtLeast(payload, (int)size, throwOnEndOfStream: false) < size) yield break;
            yield return new StoredRecord(kind, id, payload);
        }
    }

    // ---------------- the library ----------------

    /// <summary>One card model's library: its file, the card's name and how many pipelines it holds.</summary>
    public sealed record LibraryInfo(string File, CardModel Model, int Pipelines, long Bytes);

    public static List<LibraryInfo> Libraries(string gameDir)
    {
        var result = new List<LibraryInfo>();
        try
        {
            var dir = Path.Combine(gameDir, "library");
            if (!Directory.Exists(dir)) return result;
            foreach (var file in Directory.EnumerateFiles(dir, "*.bin"))
            {
                using var stream = File.OpenRead(file);
                if (ReadLibraryHeader(stream) is not { } model) continue;
                var count = 0;
                foreach (var _ in ReadRecords(stream)) count++;
                result.Add(new LibraryInfo(file, model, count, new FileInfo(file).Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    /// <summary>The header of a library file: "USWLIB1\0", its size, then "model\t…" and "name\t…" lines.</summary>
    private static CardModel? ReadLibraryHeader(Stream stream)
    {
        var magic = new byte[8];
        var size = new byte[4];
        if (stream.ReadAtLeast(magic, 8, false) < 8 || !magic.AsSpan().SequenceEqual(LibraryMagic) || stream.ReadAtLeast(size, 4, false) < 4) return null;
        var length = BitConverter.ToInt32(size);
        if (length is < 0 or > 65536) return null;
        var text = new byte[length];
        if (stream.ReadAtLeast(text, length, false) < length) return null;
        string? modelLine = null, name = null;
        foreach (var line in Encoding.UTF8.GetString(text).Split('\n'))
        {
            if (line.StartsWith("model\t")) modelLine = line[6..];
            else if (line.StartsWith("name\t")) name = line[5..];
        }
        var ids = modelLine?.Split(' ');
        if (ids is not { Length: 5 }) return null;
        try
        {
            uint H(string s) => uint.Parse(s, System.Globalization.NumberStyles.HexNumber);
            return new CardModel(H(ids[0]), H(ids[1]), H(ids[2]), H(ids[3]), H(ids[4]), name ?? "", false);
        }
        catch (FormatException) { return null; }
    }

    private static void WriteLibraryHeader(Stream stream, CardModel model)
    {
        var text = Encoding.UTF8.GetBytes($"model\t{model.ModelLine}\nname\t{model.Name}\n");
        stream.Write(LibraryMagic);
        stream.Write(BitConverter.GetBytes(text.Length));
        stream.Write(text);
    }

    /// <summary>What a merge did.</summary>
    public sealed record MergeResult(int Merged, int StillRecording, int WithoutCardDetails, int Added, int Pruned);

    /// <summary>
    /// Merges every finished recording into the game's libraries: each pipeline goes into the library of each card model
    /// that built it (software renderers left out), once. A merged recording keeps its timing log and notes (for the
    /// report) but its pipeline file goes; only the newest <see cref="SessionsKept"/> recordings are kept. Recordings
    /// from before cards were noted per pipeline are left as they are. Never throws.
    /// </summary>
    public static MergeResult Merge(string gameDir)
    {
        int merged = 0, running = 0, legacy = 0, added = 0, pruned = 0;
        try
        {
            var libDir = Path.Combine(gameDir, "library");
            var known = new Dictionary<string, HashSet<ulong>>();
            HashSet<ulong> Known(CardModel model)
            {
                if (known.TryGetValue(model.Key, out var set)) return set;
                set = new HashSet<ulong>();
                var file = Path.Combine(libDir, model.Key + ".bin");
                if (File.Exists(file))
                {
                    using var s = File.OpenRead(file);
                    if (ReadLibraryHeader(s) is not null) foreach (var r in ReadRecords(s)) set.Add(r.Id);
                }
                return known[model.Key] = set;
            }

            foreach (var session in Sessions(gameDir))
            {
                var pipelines = Path.Combine(session.Folder, "pipelines.bin");
                if (session.Merged || !File.Exists(pipelines)) continue;
                if (StillRecording(session)) { running++; continue; }
                if (session.Cards.Count == 0) { legacy++; continue; }

                // Which cards built each pipeline (timing.csv: t_ms,kind,ms,result,record_id,new,thread,adapter,…).
                var builtOn = new Dictionary<ulong, HashSet<string>>();
                foreach (var line in File.ReadLines(Path.Combine(session.Folder, "timing.csv")).Skip(1))
                {
                    var f = line.Split(',');
                    if (f.Length < 8 || f[4].Length == 0 || !ulong.TryParse(f[4], System.Globalization.NumberStyles.HexNumber, null, out var id)) continue;
                    if (!builtOn.TryGetValue(id, out var luids)) builtOn[id] = luids = new(StringComparer.OrdinalIgnoreCase);
                    luids.Add(f[7]);
                }

                Directory.CreateDirectory(libDir);
                var writers = new Dictionary<string, FileStream>();
                try
                {
                    using var input = File.OpenRead(pipelines);
                    var magic = new byte[8];
                    if (input.ReadAtLeast(magic, 8, false) == 8 && magic.AsSpan().SequenceEqual(RecordMagic))
                        foreach (var record in ReadRecords(input))
                        {
                            if (!builtOn.TryGetValue(record.Id, out var luids)) continue;
                            foreach (var luid in luids)
                            {
                                if (!session.Cards.TryGetValue(luid, out var model) || model is null || model.Software) continue;
                                if (!Known(model).Add(record.Id)) continue;
                                if (!writers.TryGetValue(model.Key, out var w))
                                {
                                    var file = Path.Combine(libDir, model.Key + ".bin");
                                    var exists = File.Exists(file) && new FileInfo(file).Length > 0;
                                    w = writers[model.Key] = new FileStream(file, FileMode.Append, FileAccess.Write);
                                    if (!exists) WriteLibraryHeader(w, model);
                                }
                                w.Write(BitConverter.GetBytes(record.Kind));
                                w.Write(BitConverter.GetBytes(record.Id));
                                w.Write(BitConverter.GetBytes((uint)record.Payload.Length));
                                w.Write(record.Payload);
                                added++;
                            }
                        }
                }
                finally
                {
                    foreach (var w in writers.Values) w.Dispose();
                }
                File.AppendAllText(Path.Combine(session.Folder, "session.txt"), $"merged\t{DateTime.Now:yyyyMMdd-HHmmss}\n");
                File.Delete(pipelines);
                merged++;
            }

            // Keep the newest recordings for the report; older merged ones go (their pipelines are in the library).
            var all = Sessions(gameDir);
            foreach (var old in all.Take(Math.Max(0, all.Count - SessionsKept)).Where(s => s.Merged || !File.Exists(Path.Combine(s.Folder, "pipelines.bin"))))
            {
                try { Directory.Delete(old.Folder, recursive: true); pruned++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return new MergeResult(merged, running, legacy, added, pruned);
    }

    /// <summary>The space the game's learned shaders take (shader files and libraries).</summary>
    public static long Bytes(string gameDir)
    {
        long total = 0;
        foreach (var sub in new[] { "blobs", "library" })
        {
            try
            {
                var dir = Path.Combine(gameDir, sub);
                if (Directory.Exists(dir)) total += Directory.EnumerateFiles(dir).Sum(f => new FileInfo(f).Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }
}
