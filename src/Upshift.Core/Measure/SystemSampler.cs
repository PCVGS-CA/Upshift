using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Upshift.Core.Measure;

/// <summary>
/// What Windows' own counters said during a measurement. Every value is null when its counter wasn't available on this
/// PC (a missing counter never fails a measurement).
/// </summary>
/// <param name="GpuPercent">Average GPU usage: per sample, the busiest engine type (3D, compute, copy…) summed over all
/// processes, as Task Manager shows it, on the graphics card that was busiest over the run.</param>
/// <param name="CpuPercent">Average usage of the whole processor.</param>
/// <param name="BusiestCorePercent">Average, over the samples, of the busiest single core at that moment: a game whose
/// main thread holds it back keeps one core near 100% while the total stays low.</param>
/// <param name="PeakVramBytes">The most dedicated video memory in use on that card during the run (all programs).</param>
/// <param name="TotalVramBytes">How much dedicated video memory the card has.</param>
public sealed record SystemLoad(double? GpuPercent, double? CpuPercent, double? BusiestCorePercent, long? PeakVramBytes, long? TotalVramBytes, int Samples);

/// <summary>
/// Samples Windows' performance counters once a second while a measurement runs: "GPU Engine" utilization, "GPU
/// Adapter Memory" dedicated usage, and "Processor Information" processor time, through PDH (part of Windows, no
/// download, no administrator rights). Nothing here throws.
/// </summary>
public sealed class SystemSampler : IDisposable
{
    private readonly object _sync = new();
    private IntPtr _query;
    private IntPtr _gpuEngine, _gpuMemory, _cpuTotal, _cpuCores;
    private Timer? _timer;
    private bool _stopped;
    private int _samples;
    private readonly List<double> _cpu = new(), _busiestCore = new();
    private readonly Dictionary<string, List<double>> _gpuByAdapter = new();
    private readonly Dictionary<string, long> _peakVramByAdapter = new();

    /// <summary>Starts sampling; returns a sampler that records nothing when PDH isn't usable.</summary>
    public static SystemSampler Start()
    {
        var sampler = new SystemSampler();
        try
        {
            if (Pdh.PdhOpenQueryW(null, IntPtr.Zero, out sampler._query) != 0) { sampler._query = IntPtr.Zero; return sampler; }
            sampler._gpuEngine = Add(sampler._query, @"\GPU Engine(*)\Utilization Percentage");
            sampler._gpuMemory = Add(sampler._query, @"\GPU Adapter Memory(*)\Dedicated Usage");
            sampler._cpuTotal = Add(sampler._query, @"\Processor Information(_Total)\% Processor Time");
            sampler._cpuCores = Add(sampler._query, @"\Processor Information(*)\% Processor Time");
            Pdh.PdhCollectQueryData(sampler._query);   // rates need a first sample to compare against
            sampler._timer = new Timer(_ => sampler.Sample(), null, 1000, 1000);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return sampler;
    }

    private static IntPtr Add(IntPtr query, string path) =>
        Pdh.PdhAddEnglishCounterW(query, path, IntPtr.Zero, out var counter) == 0 ? counter : IntPtr.Zero;

    private void Sample()
    {
        lock (_sync)
        {
            if (_stopped || _query == IntPtr.Zero) return;
            try
            {
                if (Pdh.PdhCollectQueryData(_query) != 0) return;
                _samples++;
                if (Single(_cpuTotal) is { } cpu) _cpu.Add(Math.Clamp(cpu, 0, 100));
                var cores = Instances(_cpuCores).Where(i => !i.Name.Contains("_Total", StringComparison.OrdinalIgnoreCase)).ToList();
                if (cores.Count > 0) _busiestCore.Add(Math.Clamp(cores.Max(c => c.Value), 0, 100));

                // GPU: per card (luid), the busiest engine type, each engine type summed over every process.
                var perAdapter = Instances(_gpuEngine)
                    .Select(i => (Adapter: Luid(i.Name), Engine: EngineType(i.Name), i.Value))
                    .Where(i => i.Adapter is not null && i.Engine is not null)
                    .GroupBy(i => i.Adapter!)
                    .ToDictionary(g => g.Key, g => g.GroupBy(i => i.Engine).Max(e => e.Sum(x => x.Value)));
                foreach (var (adapter, value) in perAdapter)
                {
                    if (!_gpuByAdapter.TryGetValue(adapter, out var list)) _gpuByAdapter[adapter] = list = new();
                    list.Add(Math.Clamp(value, 0, 100));
                }
                foreach (var item in Instances(_gpuMemory))
                    if (Luid(item.Name) is { } adapter)
                        _peakVramByAdapter[adapter] = Math.Max(_peakVramByAdapter.GetValueOrDefault(adapter), (long)item.Value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
    }

    /// <summary>Stops sampling and sums up. <paramref name="totalVramBytes"/> is the card's memory, from <see cref="VideoMemory"/>.</summary>
    public SystemLoad Stop(long? totalVramBytes)
    {
        lock (_sync)
        {
            _stopped = true;
            _timer?.Dispose();
            _timer = null;
            // The card the game ran on: the one busiest over the run (an integrated GPU idles beside a discrete one).
            var busiest = _gpuByAdapter.Where(a => a.Value.Count > 0).OrderByDescending(a => a.Value.Average()).Select(a => a.Key).FirstOrDefault();
            double? gpu = busiest is null ? null : _gpuByAdapter[busiest].Average();
            long? peak = busiest is not null && _peakVramByAdapter.TryGetValue(busiest, out var p) ? p
                       : _peakVramByAdapter.Count > 0 ? _peakVramByAdapter.Values.Max() : null;
            if (peak is <= 0) peak = null;
            return new SystemLoad(gpu, _cpu.Count > 0 ? _cpu.Average() : null, _busiestCore.Count > 0 ? _busiestCore.Average() : null,
                peak, totalVramBytes, _samples);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _stopped = true;
            _timer?.Dispose();
            _timer = null;
            if (_query != IntPtr.Zero) { Pdh.PdhCloseQuery(_query); _query = IntPtr.Zero; }
        }
    }

    /// <summary>"luid_0x00000000_0x0000D1A3" from a GPU counter's instance name, or null.</summary>
    public static string? Luid(string instance)
    {
        var i = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var parts = instance[i..].Split('_');
        return parts.Length >= 3 ? $"{parts[0]}_{parts[1]}_{parts[2]}".ToLowerInvariant() : null;
    }

    /// <summary>"3D", "Compute_0", "Copy"… from a GPU Engine instance name, or null.</summary>
    public static string? EngineType(string instance)
    {
        var i = instance.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? null : instance[(i + "engtype_".Length)..];
    }

    private static double? Single(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        return Pdh.PdhGetFormattedCounterValue(counter, Pdh.FmtDouble | Pdh.FmtNoCap100, out _, out var value) == 0 && value.CStatus <= 1
            ? value.DoubleValue : null;
    }

    private static List<(string Name, double Value)> Instances(IntPtr counter)
    {
        var result = new List<(string, double)>();
        if (counter == IntPtr.Zero) return result;
        uint size = 0;
        var status = Pdh.PdhGetFormattedCounterArrayW(counter, Pdh.FmtDouble | Pdh.FmtNoCap100, ref size, out _, IntPtr.Zero);
        if (status != Pdh.MoreData || size == 0) return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Pdh.PdhGetFormattedCounterArrayW(counter, Pdh.FmtDouble | Pdh.FmtNoCap100, ref size, out var count, buffer) != 0) return result;
            var itemSize = Marshal.SizeOf<Pdh.Item>();
            for (var n = 0; n < count; n++)
            {
                var item = Marshal.PtrToStructure<Pdh.Item>(buffer + n * itemSize);
                if (item.Value.CStatus > 1) continue;   // PDH_CSTATUS_VALID_DATA (0) or NEW_DATA (1)
                result.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.DoubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    private static class Pdh
    {
        public const uint FmtDouble = 0x00000200, FmtNoCap100 = 0x00008000;
        public const uint MoreData = 0x800007D2;

        [StructLayout(LayoutKind.Sequential)]
        public struct Value { public uint CStatus; public double DoubleValue; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Item { public IntPtr Name; public Value Value; }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out Value value);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
        [DllImport("pdh.dll")] public static extern uint PdhCloseQuery(IntPtr query);
    }
}

/// <summary>A graphics card's dedicated video memory, from its driver's registry entry (no administrator rights).</summary>
public static class VideoMemory
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>The card with this PCI vendor and device id, or null when Windows doesn't say.</summary>
    public static long? TotalBytes(int vendorId, int deviceId)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (cls is null) return null;
            var want = $"ven_{vendorId:x4}&dev_{deviceId:x4}";
            foreach (var name in cls.GetSubKeyNames())
            {
                if (!name.All(char.IsDigit)) continue;
                using var key = cls.OpenSubKey(name);
                if (key?.GetValue("MatchingDeviceId") is not string match || !match.Contains(want, StringComparison.OrdinalIgnoreCase)) continue;
                if (Read(key.GetValue("HardwareInformation.qwMemorySize")) is { } q && q > 0) return q;
                if (Read(key.GetValue("HardwareInformation.MemorySize")) is { } d && d > 0) return d;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return null;
    }

    private static long? Read(object? value) => value switch
    {
        long l => l,
        int i => (uint)i,
        byte[] { Length: 8 } b => BitConverter.ToInt64(b, 0),
        byte[] { Length: 4 } b => BitConverter.ToUInt32(b, 0),
        _ => null
    };
}
