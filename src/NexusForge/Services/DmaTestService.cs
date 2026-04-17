using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NexusForge.Helpers;
using NexusForge.Models;

namespace NexusForge.Services;

internal static class VmmNative
{
    private const string VmmDll = "vmm.dll";
    private const string LcDll = "leechcore.dll";

    [DllImport(VmmDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr VMMDLL_Initialize(int argc, string[] argv);

    [DllImport(VmmDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void VMMDLL_Close(IntPtr hVMM);

    [DllImport(VmmDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void VMMDLL_MemFree(IntPtr pvMem);

    [DllImport(VmmDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool VMMDLL_ConfigGet(IntPtr hVMM, ulong fOption, out ulong pqwValue);

    [DllImport(VmmDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool VMMDLL_Map_GetPhysMem(IntPtr hVMM, out IntPtr ppPhysMemMap);

    [DllImport(VmmDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern bool VMMDLL_PidGetFromName(IntPtr hVMM, string szProcName, out uint pdwPID);

    [DllImport(LcDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool LcRead(IntPtr hLC, ulong pa, uint cb, IntPtr pb);

    public const ulong OPT_CORE_LEECHCORE_HANDLE = 0x4000001000000000;
}

public class DmaTestResult
{
    public bool Success { get; set; }
    public string TestType { get; set; } = "";
    public string ErrorMessage { get; set; } = "";

    public int LatencyRps { get; set; }
    public int LatencyMinUs { get; set; }
    public int LatencyMaxUs { get; set; }
    public int LatencyAvgUs { get; set; }
    public long LatencyTotalReads { get; set; }
    public long LatencyFailedReads { get; set; }
    public string LatencyRating { get; set; } = "—";

    public float ThroughputMBps { get; set; }
    public long ThroughputTotalReads { get; set; }
    public long ThroughputFailedReads { get; set; }
    public string ThroughputRating { get; set; } = "—";

    public bool ProcessFound { get; set; }
    public string ProcessInfo { get; set; } = "";

    public string OverallRating { get; set; } = "—";
    public TimeSpan Duration { get; set; }
}

public readonly struct PhysMemPage
{
    public ulong PageBase { get; init; }
    public ulong RemainingBytes { get; init; }
}

public class DmaTestService
{
    private readonly LogService _log;
    private string? _dmaDir;
    private bool _extracted;

    private static string? _dmaDirStatic;
    private static bool _resolverRegistered;
    private static readonly object _resolverLock = new();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    private static readonly string[] EmbeddedDlls =
    {
        "vmm.dll", "leechcore.dll", "leechcore_driver.dll",
        "FTD3XX.dll", "FTD3XXWU.dll", "dbghelp.dll", "symsrv.dll",
        "tinylz4.dll", "vcruntime140.dll"
    };

    private static readonly string[] PreloadOrder =
    {
        "vcruntime140.dll",
        "FTD3XXWU.dll",
        "FTD3XX.dll",
        "tinylz4.dll",
        "dbghelp.dll",
        "symsrv.dll",
        "leechcore_driver.dll",
        "leechcore.dll",
        "vmm.dll"
    };

    public DmaTestService(LogService log) => _log = log;

    private void EnsureDllsExtracted()
    {
        if (_extracted && _dmaDir != null && Directory.Exists(_dmaDir))
            return;

        _dmaDir = Path.Combine(Path.GetTempPath(), $"nf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dmaDir);

        var assembly = Assembly.GetExecutingAssembly();
        int count = 0;

        foreach (var fileName in EmbeddedDlls)
        {
            var destPath = Path.Combine(_dmaDir, fileName);
            ResourceCrypto.ExtractResource(assembly, fileName, destPath);
            count++;
        }

        _dmaDirStatic = _dmaDir;

        int loaded = 0;
        foreach (var dll in PreloadOrder)
        {
            var fullPath = Path.Combine(_dmaDir, dll);
            if (File.Exists(fullPath))
            {
                var h = LoadLibraryW(fullPath);
                if (h != IntPtr.Zero) loaded++;
            }
        }

        lock (_resolverLock)
        {
            if (!_resolverRegistered)
            {
                NativeLibrary.SetDllImportResolver(
                    typeof(VmmNative).Assembly, ResolveDmaLibrary);
                _resolverRegistered = true;
            }
        }

        _extracted = true;
        _log.Info($"DMA libraries extracted ({count} files, {loaded} preloaded)");
    }

    private static IntPtr ResolveDmaLibrary(
        string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (_dmaDirStatic == null) return IntPtr.Zero;

        var path = Path.Combine(_dmaDirStatic, libraryName);
        if (File.Exists(path))
        {
            try { return NativeLibrary.Load(path); } catch { }
        }

        path = Path.Combine(_dmaDirStatic, libraryName + ".dll");
        if (File.Exists(path))
        {
            try { return NativeLibrary.Load(path); } catch { }
        }

        return IntPtr.Zero;
    }

    public void Cleanup()
    {
        if (_dmaDir != null)
        {
            try { Directory.Delete(_dmaDir, true); } catch { }
            _dmaDir = null;
            _extracted = false;
        }
    }

    public Task<DmaTestResult> RunLatencyTestAsync(
        TimeSpan duration, IProgress<FlashProgress>? progress, CancellationToken ct) =>
        Task.Run(() => RunLatencyTest(duration, progress, ct), ct);

    public Task<DmaTestResult> RunThroughputTestAsync(
        TimeSpan duration, IProgress<FlashProgress>? progress, CancellationToken ct) =>
        Task.Run(() => RunThroughputTest(duration, progress, ct), ct);

    public Task<DmaTestResult> RunFullTestAsync(
        IProgress<FlashProgress>? progress, CancellationToken ct) =>
        Task.Run(() => RunFullTest(progress, ct), ct);

    private DmaTestResult RunLatencyTest(
        TimeSpan duration, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        var result = new DmaTestResult { TestType = "Latency" };
        var overallSw = Stopwatch.StartNew();
        IntPtr hVMM = IntPtr.Zero;

        try
        {
            progress?.Report(Prog("Connecting", 10, "Connecting to DMA device..."));
            hVMM = ConnectDma();
            var hLC = GetLeechCoreHandle(hVMM);

            progress?.Report(Prog("Mapping", 20, "Getting memory map..."));
            var pages = BuildMemoryMap(hVMM);
            if (pages.Length == 0) { result.ErrorMessage = "No valid physical memory pages found"; return result; }

            _log.Info($"Running Latency Test for {duration.TotalSeconds:0}s ({pages.Length} pages)...");
            progress?.Report(Prog("Testing", 30, $"Running latency test ({duration.TotalSeconds:0}s)..."));

            RunLatencyLoop(hLC, pages, duration, ct, result);
            result.Success = true;
            result.OverallRating = result.LatencyRating;

            _log.Info($"Latency Test: {result.LatencyRps:N0} RPS, {result.LatencyAvgUs:N0} us avg, {result.LatencyMinUs:N0} us min, {result.LatencyMaxUs:N0} us max");
            _log.Info($"  Reads: {result.LatencyTotalReads:N0} total, {result.LatencyFailedReads:N0} failed — {result.LatencyRating}");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = SanitizeError(ex.Message);
            _log.Error($"Latency test failed: {result.ErrorMessage}");
        }
        finally
        {
            if (hVMM != IntPtr.Zero) VmmNative.VMMDLL_Close(hVMM);
            overallSw.Stop();
            result.Duration = overallSw.Elapsed;
            progress?.Report(Prog(
                result.Success ? "Complete" : "Failed",
                result.Success ? 100 : 0,
                result.Success ? $"Latency: {result.LatencyRps:N0} RPS — {result.LatencyRating}" : result.ErrorMessage));
        }
        return result;
    }

    private DmaTestResult RunThroughputTest(
        TimeSpan duration, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        var result = new DmaTestResult { TestType = "Throughput" };
        var overallSw = Stopwatch.StartNew();
        IntPtr hVMM = IntPtr.Zero;

        try
        {
            progress?.Report(Prog("Connecting", 10, "Connecting to DMA device..."));
            hVMM = ConnectDma();
            var hLC = GetLeechCoreHandle(hVMM);

            progress?.Report(Prog("Mapping", 20, "Getting memory map..."));
            const uint readSize = 0x1000000;
            var pages = BuildMemoryMap(hVMM, pageCount: 1000, minContiguous: readSize);
            if (pages.Length == 0) { result.ErrorMessage = "No contiguous 16MB memory regions found"; return result; }

            _log.Info($"Running Throughput Test for {duration.TotalSeconds:0}s ({pages.Length} regions)...");
            progress?.Report(Prog("Testing", 30, $"Running throughput test ({duration.TotalSeconds:0}s)..."));

            RunThroughputLoop(hLC, pages, duration, ct, result);
            result.Success = true;
            result.OverallRating = result.ThroughputRating;

            _log.Info($"Throughput Test: {result.ThroughputMBps:F2} MB/s");
            _log.Info($"  Reads: {result.ThroughputTotalReads:N0} total, {result.ThroughputFailedReads:N0} failed — {result.ThroughputRating}");
            if (result.ThroughputMBps < 45f)
                _log.Warn("Low throughput indicates USB 2.0 connection. Check port/cable.");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = SanitizeError(ex.Message);
            _log.Error($"Throughput test failed: {result.ErrorMessage}");
        }
        finally
        {
            if (hVMM != IntPtr.Zero) VmmNative.VMMDLL_Close(hVMM);
            overallSw.Stop();
            result.Duration = overallSw.Elapsed;
            progress?.Report(Prog(
                result.Success ? "Complete" : "Failed",
                result.Success ? 100 : 0,
                result.Success ? $"Throughput: {result.ThroughputMBps:F1} MB/s — {result.ThroughputRating}" : result.ErrorMessage));
        }
        return result;
    }

    private DmaTestResult RunFullTest(
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        var result = new DmaTestResult { TestType = "Full" };
        var overallSw = Stopwatch.StartNew();
        IntPtr hVMM = IntPtr.Zero;

        try
        {
            progress?.Report(Prog("Connecting", 5, "Connecting to DMA device..."));
            hVMM = ConnectDma();
            var hLC = GetLeechCoreHandle(hVMM);

            progress?.Report(Prog("Process", 10, "Looking up system processes..."));
            RunProcessLookup(hVMM, result);

            progress?.Report(Prog("Mapping", 20, "Getting memory map..."));
            var allPages = BuildMemoryMap(hVMM);
            if (allPages.Length == 0) { result.ErrorMessage = "No valid physical memory pages found"; return result; }

            _log.Info("Running Latency Test (5s)...");
            progress?.Report(Prog("Latency", 30, "Running latency test (5s)..."));
            RunLatencyLoop(hLC, allPages, TimeSpan.FromSeconds(5), ct, result);
            _log.Info($"Latency: {result.LatencyRps:N0} RPS, {result.LatencyAvgUs:N0} us avg — {result.LatencyRating}");

            progress?.Report(Prog("Throughput", 60, "Running throughput test (5s)..."));
            _log.Info("Running Throughput Test (5s)...");
            const uint tputReadSize = 0x1000000;
            var tputPages = BuildMemoryMap(hVMM, pageCount: 1000, minContiguous: tputReadSize);

            if (tputPages.Length > 0)
            {
                RunThroughputLoop(hLC, tputPages, TimeSpan.FromSeconds(5), ct, result);
                _log.Info($"Throughput: {result.ThroughputMBps:F2} MB/s — {result.ThroughputRating}");
                if (result.ThroughputMBps < 45f)
                    _log.Warn("Low throughput indicates USB 2.0 connection.");
            }
            else
            {
                _log.Warn("No contiguous 16MB regions — skipping throughput test");
                result.ThroughputRating = "SKIP";
            }

            result.Success = true;
            if (result.LatencyRating == "FAIL" || result.ThroughputRating == "FAIL")
                result.OverallRating = "FAIL";
            else
            {
                int lp = GetRatingPriority(result.LatencyRating);
                int tp = GetRatingPriority(result.ThroughputRating);
                result.OverallRating = lp <= tp ? result.LatencyRating : result.ThroughputRating;
            }
            _log.Info($"Full Test Overall: {result.OverallRating}");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = SanitizeError(ex.Message);
            _log.Error($"Full test failed: {result.ErrorMessage}");
        }
        finally
        {
            if (hVMM != IntPtr.Zero) VmmNative.VMMDLL_Close(hVMM);
            overallSw.Stop();
            result.Duration = overallSw.Elapsed;
            progress?.Report(Prog(
                result.Success ? "Complete" : "Failed",
                result.Success ? 100 : 0,
                result.Success ? $"Overall: {result.OverallRating}" : result.ErrorMessage));
        }
        return result;
    }

    private static void RunLatencyLoop(
        IntPtr hLC, PhysMemPage[] pages, TimeSpan duration,
        CancellationToken ct, DmaTestResult result)
    {
        const uint readSize = 0x1000;
        IntPtr pb = Marshal.AllocHGlobal((int)readSize);
        try
        {
            long totalCount = 0, failedCount = 0;
            TimeSpan minRead = TimeSpan.MaxValue, maxRead = TimeSpan.MinValue;
            var readSw = new Stopwatch();
            var testSw = Stopwatch.StartNew();

            while (testSw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                readSw.Restart();
                if (VmmNative.LcRead(hLC, pages[Random.Shared.Next(pages.Length)].PageBase, readSize, pb))
                {
                    var speed = readSw.Elapsed;
                    if (speed < minRead) minRead = speed;
                    if (speed > maxRead) maxRead = speed;
                }
                else { failedCount++; }
                totalCount++;
            }

            PopulateLatencyResult(result, totalCount, failedCount, testSw.Elapsed, minRead, maxRead);
        }
        finally { Marshal.FreeHGlobal(pb); }
    }

    private static void RunThroughputLoop(
        IntPtr hLC, PhysMemPage[] pages, TimeSpan duration,
        CancellationToken ct, DmaTestResult result)
    {
        const uint readSize = 0x1000000;
        IntPtr pb = Marshal.AllocHGlobal((int)readSize);
        try
        {
            long totalCount = 0, failedCount = 0;
            var testSw = Stopwatch.StartNew();

            while (testSw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                if (!VmmNative.LcRead(hLC, pages[Random.Shared.Next(pages.Length)].PageBase, readSize, pb))
                    failedCount++;
                totalCount++;
            }

            PopulateThroughputResult(result, totalCount, failedCount, testSw.Elapsed);
        }
        finally { Marshal.FreeHGlobal(pb); }
    }

    private IntPtr ConnectDma()
    {
        EnsureDllsExtracted();

        _log.Info("Connecting to FPGA DMA device...");
        var args = new[] { "-device", "fpga", "-norefresh", "-waitinitialize" };
        var hVMM = VmmNative.VMMDLL_Initialize(args.Length, args);
        if (hVMM == IntPtr.Zero)
            throw new InvalidOperationException(
                "Could not open DMA device. Check: " +
                "1) Card in PCIe slot with power, " +
                "2) Firmware loaded (flash + cold reboot), " +
                "3) DATA USB 3.0 cable connected, " +
                "4) FTDI driver installed.");
        _log.Info("DMA device connected");
        return hVMM;
    }

    private IntPtr GetLeechCoreHandle(IntPtr hVMM)
    {
        if (!VmmNative.VMMDLL_ConfigGet(hVMM, VmmNative.OPT_CORE_LEECHCORE_HANDLE, out ulong hLC) || hLC == 0)
            throw new InvalidOperationException("Failed to get LeechCore handle from VMM");
        return (IntPtr)(long)hLC;
    }

    private PhysMemPage[] BuildMemoryMap(
        IntPtr hVMM, int pageCount = 100000, uint minContiguous = 0x1000)
    {
        if (!VmmNative.VMMDLL_Map_GetPhysMem(hVMM, out IntPtr pMap) || pMap == IntPtr.Zero)
            throw new InvalidOperationException("Failed to retrieve physical memory map");

        try
        {
            uint cMap = (uint)Marshal.ReadInt32(pMap, 24);
            if (cMap == 0)
                throw new InvalidOperationException("Physical memory map is empty");

            const int headerSize = 32;
            const int entrySize  = 16;

            var pages = new List<PhysMemPage>();
            for (int i = 0; i < (int)cMap; i++)
            {
                IntPtr pEntry = pMap + headerSize + i * entrySize;
                ulong pa = (ulong)Marshal.ReadInt64(pEntry, 0);
                ulong cb = (ulong)Marshal.ReadInt64(pEntry, 8);

                for (ulong p = pa, rem = cb; rem > 0x1000; p += 0x1000, rem -= 0x1000)
                    pages.Add(new PhysMemPage { PageBase = p, RemainingBytes = rem });
            }

            var filtered = pages
                .Where(p => p.RemainingBytes >= minContiguous)
                .Take(pageCount)
                .ToArray();

            Random.Shared.Shuffle(filtered);
            _log.Info($"Memory map: {cMap} regions, {filtered.Length} usable pages");
            return filtered;
        }
        finally { VmmNative.VMMDLL_MemFree(pMap); }
    }

    private void RunProcessLookup(IntPtr hVMM, DmaTestResult result)
    {
        try
        {
            const string target = "smss.exe";
            if (VmmNative.VMMDLL_PidGetFromName(hVMM, target, out uint pid))
            {
                result.ProcessFound = true;
                result.ProcessInfo = $"{target} @ PID {pid}";
                _log.Info($"Process found: {target} (PID {pid})");
            }
            else
            {
                result.ProcessInfo = "smss.exe not found";
                _log.Warn("Could not locate smss.exe");
            }
        }
        catch (Exception ex)
        {
            result.ProcessInfo = "Process lookup failed";
            _log.Warn($"Process lookup error: {SanitizeError(ex.Message)}");
        }
    }

    private static void PopulateLatencyResult(
        DmaTestResult result, long totalCount, long failedCount,
        TimeSpan testDuration, TimeSpan minRead, TimeSpan maxRead)
    {
        long ok = totalCount - failedCount;
        double avgSec = ok == 0 ? 0 : testDuration.TotalSeconds / ok;
        int rps   = avgSec <= 0 ? 0 : (int)Math.Round(1.0 / avgSec);
        int avgUs = ok == 0 ? 0 : (int)Math.Round(testDuration.TotalMicroseconds / ok);
        int minUs = minRead == TimeSpan.MaxValue ? 0 : (int)Math.Round(minRead.TotalMicroseconds);
        int maxUs = maxRead == TimeSpan.MinValue ? 0 : (int)Math.Round(maxRead.TotalMicroseconds);
        double failPct = totalCount == 0 ? 0 : (failedCount / (double)totalCount) * 100.0;

        result.LatencyRps        = rps;
        result.LatencyMinUs      = minUs;
        result.LatencyMaxUs      = maxUs;
        result.LatencyAvgUs      = avgUs;
        result.LatencyTotalReads  = totalCount;
        result.LatencyFailedReads = failedCount;

        result.LatencyRating = failPct >= 1.0 ? "FAIL" : rps switch
        {
            >= 18000 => "PERFECT",
            >= 6000  => "EXCELLENT",
            >= 4000  => "GOOD",
            >= 3000  => "ACCEPTABLE",
            _        => "FAIL"
        };
    }

    private static void PopulateThroughputResult(
        DmaTestResult result, long totalCount, long failedCount, TimeSpan testDuration)
    {
        long ok = totalCount - failedCount;
        ulong bytesRead = (ulong)ok * 0x1000000;
        float mbps = testDuration.TotalSeconds <= 0
            ? 0 : (float)(bytesRead / 1024.0 / 1024.0 / testDuration.TotalSeconds);
        float failPct = totalCount == 0 ? 0f : (failedCount / (float)totalCount) * 100f;

        result.ThroughputMBps         = mbps;
        result.ThroughputTotalReads   = totalCount;
        result.ThroughputFailedReads  = failedCount;

        result.ThroughputRating = failPct > 0 ? "FAIL" : mbps switch
        {
            >= 600f => "PERFECT",
            >= 200f => "EXCELLENT",
            >= 150f => "GOOD",
            >= 100f => "ACCEPTABLE",
            _       => "FAIL"
        };
    }

    private static int GetRatingPriority(string r) => r switch
    {
        "PERFECT" => 5, "EXCELLENT" => 4, "GOOD" => 3,
        "ACCEPTABLE" => 2, "FAIL" => 1, _ => 0
    };

    private static string SanitizeError(string msg)
    {
        var s = Regex.Replace(msg, @"[A-Za-z]:\\[^\s""']+", "[path]");
        return Regex.Replace(s, @"/tmp/[^\s""']+", "[path]");
    }

    private static FlashProgress Prog(string stage, int pct, string msg) =>
        new() { Stage = stage, Percentage = pct, Message = msg };
}
