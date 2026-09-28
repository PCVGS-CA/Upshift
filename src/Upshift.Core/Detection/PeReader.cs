using System.Text;

namespace Upshift.Core.Detection;

public sealed record PeInfo(ushort Machine, bool Is64Bit, IReadOnlyList<string> Imports);

/// <summary>
/// Reads just enough of a Windows .exe/.dll header to know its bitness and which DLLs it imports.
/// Only seeks to the parts it needs, so it stays fast on 200 MB game executables.
/// </summary>
public static class PeReader
{
    private const ushort MachineX64 = 0x8664;
    private const ushort MachineArm64 = 0xAA64;

    public static PeInfo? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return null; // "MZ"

            fs.Position = 0x3C;
            var peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset > fs.Length - 24) return null;

            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return null; // "PE\0\0"

            var machine = br.ReadUInt16();
            var sectionCount = br.ReadUInt16();
            fs.Position += 12;
            var optionalHeaderSize = br.ReadUInt16();
            fs.Position += 2;

            var optionalStart = fs.Position;
            var pe32Plus = br.ReadUInt16() == 0x20B;

            fs.Position = optionalStart + (pe32Plus ? 108 : 92);
            var dirCount = br.ReadUInt32();
            var dirTable = optionalStart + (pe32Plus ? 112 : 96);

            (uint Rva, uint Size) ReadDir(int index)
            {
                if (index >= dirCount) return (0, 0);
                fs.Position = dirTable + index * 8;
                return (br.ReadUInt32(), br.ReadUInt32());
            }

            var importDir = ReadDir(1);
            var delayDir = ReadDir(13);

            var sections = new List<(uint Va, uint VSize, uint Raw, uint RawSize)>();
            var sectionTable = optionalStart + optionalHeaderSize;
            for (var s = 0; s < sectionCount; s++)
            {
                fs.Position = sectionTable + s * 40 + 8;
                var vSize = br.ReadUInt32();
                var va = br.ReadUInt32();
                var rawSize = br.ReadUInt32();
                var raw = br.ReadUInt32();
                sections.Add((va, vSize, raw, rawSize));
            }

            long ToOffset(uint rva)
            {
                foreach (var s in sections)
                {
                    var span = Math.Max(s.VSize, s.RawSize);
                    if (rva >= s.Va && rva < s.Va + span) return s.Raw + (rva - s.Va);
                }
                return -1;
            }

            string? ReadAsciiz(uint rva)
            {
                var offset = ToOffset(rva);
                if (offset < 0 || offset >= fs.Length) return null;
                fs.Position = offset;
                var sb = new StringBuilder();
                for (var n = 0; n < 260; n++)
                {
                    var b = fs.ReadByte();
                    if (b <= 0) break;
                    sb.Append((char)b);
                }
                return sb.ToString();
            }

            var imports = new List<string>();

            // Normal imports: 20-byte descriptors, DLL name RVA at +12, table ends with a zeroed entry.
            if (importDir.Rva != 0)
            {
                var start = ToOffset(importDir.Rva);
                for (var n = 0; start >= 0 && n < 1024; n++)
                {
                    fs.Position = start + n * 20 + 12;
                    var nameRva = br.ReadUInt32();
                    if (nameRva == 0) break;
                    var name = ReadAsciiz(nameRva);
                    if (!string.IsNullOrEmpty(name)) imports.Add(name);
                }
            }

            // Delay-load imports: 32-byte descriptors, DLL name RVA at +4.
            if (delayDir.Rva != 0)
            {
                var start = ToOffset(delayDir.Rva);
                for (var n = 0; start >= 0 && n < 1024; n++)
                {
                    fs.Position = start + n * 32 + 4;
                    var nameRva = br.ReadUInt32();
                    if (nameRva == 0) break;
                    var name = ReadAsciiz(nameRva);
                    if (!string.IsNullOrEmpty(name)) imports.Add(name);
                }
            }

            return new PeInfo(machine, machine is MachineX64 or MachineArm64, imports);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }
}
