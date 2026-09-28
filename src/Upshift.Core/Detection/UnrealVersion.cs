using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Upshift.Core.Detection;

/// <summary>
/// Works out which Unreal Engine an Unreal game uses. Checked in this order:
/// <list type="number">
/// <item>The engine's build branch ("++UE5+Release-5.1") in the exe's version info or inside the exe itself. Gives the exact version,
/// but many games replace it with their own branch name or strip it.</item>
/// <item>UE5-only console variables inside the exe (r.Lumen.*, r.Nanite.*). UE5 always compiles these in, UE4 never has them.
/// Only trusted when the exe is readable (an ordinary engine string like r.Tonemapper is present), since protected exes are encrypted.</item>
/// <item>Package formats in Content\Paks, which don't depend on the exe: IoStore .utoc version 4+ and .pak version 12+ only exist in UE5,
/// and .pak version 10 or lower only in UE4. Engine\Content\Renderer\TessellationTable.bin only ships with UE 5.3+.</item>
/// </list>
/// </summary>
public static class UnrealVersion
{
    private const uint PakMagic = 0x5A6F12E1;
    private static readonly byte[] UtocMagic = Encoding.ASCII.GetBytes("-==--==--==--==-");

    private static readonly byte[] BranchWide = Encoding.Unicode.GetBytes("++UE");
    private static readonly byte[] BranchAscii = Encoding.ASCII.GetBytes("++UE");
    private static readonly byte[][] Ue5Markers = { Encoding.Unicode.GetBytes("r.Lumen."), Encoding.Unicode.GetBytes("r.Nanite.") };
    private static readonly byte[] ReadableMarker = Encoding.Unicode.GetBytes("r.Tonemapper");

    public static string Detect(string installDir, string? exe)
    {
        if (exe is not null)
        {
            var info = FileVersions.TryGetInfo(exe);
            var exact = ExactVersion($"{info?.ProductVersion} {info?.FileVersion}");
            if (exact is not null) return exact;
        }

        var scan = exe is null ? default : ScanExe(exe);
        if (scan.Exact is not null) return scan.Exact;
        if (scan.HasUe5Code) return "Unreal Engine 5";

        var folder = FolderMajor(installDir);
        if (folder is not null) return $"Unreal Engine {folder}";

        return scan.Readable ? "Unreal Engine 4" : "Unreal Engine";
    }

    private static string? ExactVersion(string text)
    {
        var m = Regex.Match(text, @"\+\+UE(\d)\+Release-(\d)\.(\d+)");
        return m.Success ? $"Unreal Engine {m.Groups[2].Value}.{m.Groups[3].Value}" : null;
    }

    private record struct ExeScan(string? Exact, bool HasUe5Code, bool Readable);

    /// <summary>Streams through the exe (they're often 100-250 MB) looking for the markers above.</summary>
    private static ExeScan ScanExe(string exe)
    {
        const int chunk = 4 * 1024 * 1024;
        const int overlap = 256;
        var result = new ExeScan();

        try
        {
            using var stream = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
            var buffer = new byte[chunk + overlap];
            var carried = 0;

            while (true)
            {
                var read = stream.Read(buffer, carried, chunk);
                if (read <= 0) break;
                var span = buffer.AsSpan(0, carried + read);

                result.Exact ??= FindBranch(span, BranchWide, Encoding.Unicode) ?? FindBranch(span, BranchAscii, Encoding.ASCII);
                if (result.Exact is not null) return result;

                foreach (var marker in Ue5Markers)
                    if (!result.HasUe5Code && span.IndexOf(marker) >= 0) result.HasUe5Code = true;
                if (!result.Readable) result.Readable = span.IndexOf(ReadableMarker) >= 0;

                carried = Math.Min(overlap, span.Length);
                span[^carried..].CopyTo(buffer);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // Lumen/Nanite names in an exe we can't otherwise read would be a coincidence; don't trust them.
        if (!result.Readable) result.HasUe5Code = false;
        return result;
    }

    private static string? FindBranch(ReadOnlySpan<byte> span, byte[] marker, Encoding encoding)
    {
        var offset = 0;
        while (offset < span.Length)
        {
            var i = span[offset..].IndexOf(marker);
            if (i < 0) return null;
            var start = offset + i;
            var take = Math.Min(64 * (encoding == Encoding.Unicode ? 2 : 1), span.Length - start);
            var exact = ExactVersion(encoding.GetString(span.Slice(start, take)));
            if (exact is not null) return exact;
            offset = start + marker.Length;
        }
        return null;
    }

    /// <summary>"5", "4" or null, from the package files the game ships.</summary>
    private static string? FolderMajor(string installDir)
    {
        try
        {
            if (File.Exists(Path.Combine(installDir, "Engine", "Content", "Renderer", "TessellationTable.bin"))) return "5";

            var utocMax = 0;
            var pakMax = 0;

            foreach (var project in Directory.EnumerateDirectories(installDir))
            {
                var paks = Path.Combine(project, "Content", "Paks");
                if (!Directory.Exists(paks)) continue;

                foreach (var file in Directory.EnumerateFiles(paks, "*.utoc").Take(20))
                    utocMax = Math.Max(utocMax, UtocVersion(file));

                foreach (var file in Directory.EnumerateFiles(paks, "*.pak").Take(20))
                {
                    var v = PakVersion(file);
                    if (v <= 0) continue;
                    pakMax = Math.Max(pakMax, v);
                }
            }

            if (utocMax >= 4 || pakMax >= 12) return "5";
            if (utocMax == 0 && pakMax > 0 && pakMax <= 10) return "4";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static int UtocVersion(string file)
    {
        try
        {
            using var s = File.OpenRead(file);
            Span<byte> header = stackalloc byte[17];
            if (s.Read(header) < header.Length || !header[..16].SequenceEqual(UtocMagic)) return 0;
            return header[16];
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The version number that follows the magic in a .pak file's footer (the footer's size varies by version).</summary>
    private static int PakVersion(string file)
    {
        try
        {
            using var s = File.OpenRead(file);
            var length = (int)Math.Min(512, s.Length);
            if (length < 44) return 0;
            s.Seek(-length, SeekOrigin.End);
            var tail = new byte[length];
            s.ReadExactly(tail);

            for (var i = length - 44; i >= 0; i--)
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == PakMagic)
                    return BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(i + 4));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return 0;
    }
}
