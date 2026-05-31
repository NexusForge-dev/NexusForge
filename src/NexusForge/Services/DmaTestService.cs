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

    // LcCommand: send a special command to LeechCore (e.g., read PCIe config space).
    // ppbDataOut buffer is allocated by LeechCore and must be freed via LcMemFree.
    [DllImport(LcDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool LcCommand(IntPtr hLC, ulong fOption, uint cbDataIn, IntPtr pbDataIn,
                                        out IntPtr ppbDataOut, out uint pcbDataOut);

    [DllImport(LcDll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void LcMemFree(IntPtr pvMem);

    public const ulong OPT_CORE_LEECHCORE_HANDLE = 0x4000001000000000;

    // LeechCore command IDs (from leechcore.h).
    // FPGA_PCIECFGSPACE_RD reads the FPGA endpoint's own 4 KB PCIe config space
    // via Type 0 config TLPs — works regardless of P2P routing or memory map.
    public const ulong LC_CMD_FPGA_PCIECFGSPACE_RD = 0x0000010300000000;
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

    // Stress (lone-style long-duration mixed-size stability test)
    public TimeSpan StressDuration { get; set; }
    public long StressTotalReads { get; set; }
    public long StressFailedReads { get; set; }
    public long StressMaxConsecFails { get; set; }
    public float StressFailPct { get; set; }
    public string StressRating { get; set; } = "—";

    public string OverallRating { get; set; } = "—";
    public TimeSpan Duration { get; set; }
}

public readonly struct PhysMemPage
{
    public ulong PageBase { get; init; }
    public ulong RemainingBytes { get; init; }
}

public class MmapResult
{
    public bool Success { get; set; }
    public string ErrorMessage { get; set; } = "";
    public int RegionCount { get; set; }
    public double TotalRamGb { get; set; }
    public string Content { get; set; } = "";
}

public class DmaTestService
{
    private readonly LogService _log;
    private string? _dmaDir;
    private bool _extracted;

    private static string? _dmaDirStatic;
    private static bool _resolverRegistered;
    private static readonly object _resolverLock = new();

    // Canonical mmap.txt location — written on generate, read on every connect + test.
    public static string MmapCachePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NexusForge", "mmap.txt");

    public static bool HasCachedMmap() => File.Exists(MmapCachePath);

    public static DateTime? GetMmapCacheAge() =>
        File.Exists(MmapCachePath) ? File.GetLastWriteTime(MmapCachePath) : null;

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

    /// <summary>
    /// Public entry point so other services (e.g. BarProbeService) can ensure
    /// the embedded leechcore/vmm/FTDI DLLs are extracted and the PInvoke
    /// resolver is registered before they call into VmmNative themselves.
    /// Idempotent — safe to call repeatedly.
    /// </summary>
    public void EnsureLibraries() => EnsureDllsExtracted();

    private void EnsureDllsExtracted()
    {
        if (_extracted && _dmaDir != null && Directory.Exists(_dmaDir))
            return;

        // Suppress the Microsoft Internet Symbol Store EULA dialog that symsrv.dll
        // (bundled with MemProcFS 5.17.7) shows on first symbol-server use. Two
        // belt-and-suspenders mechanisms — either alone is enough on most PCs,
        // both together is defensive against future symsrv changes:
        //
        //   1. _NT_SYMBOL_PATH=""  Tells symsrv "no symbol path at all" — no
        //      downloads attempted, no EULA prompt path reached. VMMDLL still
        //      initialises (LeechCore reads don't need kernel symbols); only
        //      PidGetFromName-style introspection is degraded, which we only
        //      use in Full Test and can fall back gracefully.
        //
        //   2. HKCU\Software\Microsoft\Symbol Server\EULA = 1  The documented
        //      symsrv registry flag that records "user accepted EULA". Set
        //      proactively in case some future symsrv variant ignores the
        //      env var.
        SuppressSymbolStoreEula();

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

    /// <summary>
    /// Suppress the Microsoft Internet Symbol Store EULA prompt that symsrv.dll
    /// pops up when VMMDLL_Initialize tries to fetch kernel PDBs. Runs before
    /// any DLL load so the env var is in place when symsrv first reads it.
    /// </summary>
    private void SuppressSymbolStoreEula()
    {
        // (1) Primary: empty _NT_SYMBOL_PATH — symsrv has no server to query,
        // so the EULA-prompt code path is never reached. Process scope is
        // enough; child processes (none here) and other dbghelp consumers in
        // the process inherit it.
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", "", EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable("_NT_ALT_SYMBOL_PATH", "", EnvironmentVariableTarget.Process);
        }
        catch { /* env var write essentially can't fail */ }

        // (2) Backup: HKCU\Software\Microsoft\Symbol Server\EULA = 1 (DWORD),
        // the documented value the dialog's "Yes" button writes. Idempotent.
        // HKCU = no admin required.
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser
                .CreateSubKey(@"Software\Microsoft\Symbol Server");
            if (key != null)
            {
                var existing = key.GetValue("EULA");
                if (!(existing is int v && v == 1))
                    key.SetValue("EULA", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
        }
        catch { /* best effort */ }
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

    public Task<DmaTestResult> RunStressTestAsync(
        TimeSpan duration, IProgress<FlashProgress>? progress, CancellationToken ct) =>
        Task.Run(() => RunStressTest(duration, progress, ct), ct);

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

    private DmaTestResult RunStressTest(
        TimeSpan duration, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        var result = new DmaTestResult { TestType = "Stress" };
        var overallSw = Stopwatch.StartNew();
        IntPtr hVMM = IntPtr.Zero;

        try
        {
            progress?.Report(Prog("Connecting", 5, "Connecting to DMA device..."));
            hVMM = ConnectDma();
            var hLC = GetLeechCoreHandle(hVMM);

            progress?.Report(Prog("Mapping", 10, "Getting memory map..."));
            // Pages usable for small 4K reads (anywhere)
            var smallPages = BuildMemoryMap(hVMM, pageCount: 100000, minContiguous: 0x1000);
            // Pages usable for 16M reads (contiguous region required)
            var bigPages   = BuildMemoryMap(hVMM, pageCount: 1000, minContiguous: 0x1000000);
            if (smallPages.Length == 0)
            {
                result.ErrorMessage = "No valid physical memory pages found";
                return result;
            }

            _log.Info($"Running Stress Test for {duration.TotalMinutes:F1} min ({smallPages.Length} small pages, {bigPages.Length} 16MB regions)...");
            progress?.Report(Prog("Stress", 15, $"Stress soak running ({duration.TotalMinutes:F1} min)..."));

            RunStressLoop(hLC, smallPages, bigPages, duration, progress, ct, result);
            result.Success = true;
            result.OverallRating = result.StressRating;

            _log.Info($"Stress Test: {result.StressTotalReads:N0} reads in {result.StressDuration.TotalSeconds:F0}s, " +
                      $"{result.StressFailedReads:N0} failed ({result.StressFailPct:F3}%), " +
                      $"max consecutive failures = {result.StressMaxConsecFails} — {result.StressRating}");
        }
        catch (Exception ex)
        {
            result.ErrorMessage = SanitizeError(ex.Message);
            _log.Error($"Stress test failed: {result.ErrorMessage}");
        }
        finally
        {
            if (hVMM != IntPtr.Zero) VmmNative.VMMDLL_Close(hVMM);
            overallSw.Stop();
            result.Duration = overallSw.Elapsed;
            progress?.Report(Prog(
                result.Success ? "Complete" : "Failed",
                result.Success ? 100 : 0,
                result.Success
                    ? $"Stress: {result.StressFailedReads:N0} fails / {result.StressTotalReads:N0} reads — {result.StressRating}"
                    : result.ErrorMessage));
        }
        return result;
    }

    private static void RunStressLoop(
        IntPtr hLC, PhysMemPage[] smallPages, PhysMemPage[] bigPages,
        TimeSpan duration, IProgress<FlashProgress>? progress,
        CancellationToken ct, DmaTestResult result)
    {
        const uint smallSize = 0x1000;       // 4 KB
        const uint bigSize   = 0x1000000;    // 16 MB
        // Ratio: 256 small reads per 1 big read. Same workload mix as lone-DMA
        // stability soak: lots of metadata-sized reads with periodic bulk reads
        // to keep both code paths exercised.
        const int  bigEvery  = 256;

        IntPtr pbSmall = Marshal.AllocHGlobal((int)smallSize);
        IntPtr pbBig   = bigPages.Length > 0 ? Marshal.AllocHGlobal((int)bigSize) : IntPtr.Zero;
        try
        {
            long total = 0, fails = 0;
            long maxConsec = 0, curConsec = 0;
            var sw = Stopwatch.StartNew();
            var lastProgress = Stopwatch.StartNew();
            int iter = 0;

            while (sw.Elapsed < duration && !ct.IsCancellationRequested)
            {
                bool ok;
                if (pbBig != IntPtr.Zero && (iter % bigEvery) == 0 && bigPages.Length > 0)
                {
                    ok = VmmNative.LcRead(hLC, bigPages[Random.Shared.Next(bigPages.Length)].PageBase, bigSize, pbBig);
                }
                else
                {
                    ok = VmmNative.LcRead(hLC, smallPages[Random.Shared.Next(smallPages.Length)].PageBase, smallSize, pbSmall);
                }

                if (ok)
                {
                    curConsec = 0;
                }
                else
                {
                    fails++;
                    curConsec++;
                    if (curConsec > maxConsec) maxConsec = curConsec;
                }
                total++;
                iter++;

                // Report progress every 500ms with running stats
                if (lastProgress.ElapsedMilliseconds >= 500)
                {
                    int pct = (int)Math.Min(99, 15 + 84.0 * sw.Elapsed.TotalSeconds / duration.TotalSeconds);
                    float pctFail = total == 0 ? 0f : (fails / (float)total) * 100f;
                    progress?.Report(Prog(
                        "Stress",
                        pct,
                        $"{sw.Elapsed.TotalSeconds:F0}s / {duration.TotalSeconds:F0}s — " +
                        $"{total:N0} reads, {fails:N0} fail ({pctFail:F3}%), max-streak {maxConsec}"));
                    lastProgress.Restart();
                }
            }

            float failPct = total == 0 ? 0f : (fails / (float)total) * 100f;
            result.StressDuration      = sw.Elapsed;
            result.StressTotalReads    = total;
            result.StressFailedReads   = fails;
            result.StressMaxConsecFails = maxConsec;
            result.StressFailPct       = failPct;

            // Rating: same spirit as lone-DMA's PASS/FAIL.
            // PASS-class outcomes require ZERO failures during the soak (any
            // failure under sustained load points at a regression — wedge,
            // backpressure, classifier kick). One-off transient fails are
            // ACCEPTABLE if rare (<0.01% of total). Anything above is FAIL.
            if (fails == 0)
                result.StressRating = sw.Elapsed.TotalMinutes >= 5 ? "PERFECT"
                                    : sw.Elapsed.TotalMinutes >= 2 ? "EXCELLENT"
                                    :                                "GOOD";
            else if (failPct < 0.01f && maxConsec < 3)
                result.StressRating = "ACCEPTABLE";
            else
                result.StressRating = "FAIL";
        }
        finally
        {
            if (pbSmall != IntPtr.Zero) Marshal.FreeHGlobal(pbSmall);
            if (pbBig   != IntPtr.Zero) Marshal.FreeHGlobal(pbBig);
        }
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
        var argList = new List<string> { "-device", "fpga", "-norefresh", "-waitinitialize" };
        if (HasCachedMmap())
        {
            argList.Add("-memmap");
            argList.Add(MmapCachePath);
            _log.Info($"Using cached mmap ({Path.GetFileName(MmapCachePath)})");
        }
        else
        {
            _log.Warn("No mmap.txt cached — VBS-fenced pages will appear as read failures on IOMMU systems. Use 'Generate mmap' first.");
        }
        var args = argList.ToArray();
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

                // >= not > so the last page of each region is included
                for (ulong p = pa, rem = cb; rem >= 0x1000; p += 0x1000, rem -= 0x1000)
                    pages.Add(new PhysMemPage { PageBase = p, RemainingBytes = rem });
            }

            // Load mmap intervals to exclude VBS-fenced pages that LcRead returns false for.
            var mmapIntervals = LoadMmapIntervals();

            // Shuffle BEFORE Take so the pool is a random sample from all valid pages,
            // not just the lowest addresses in physical memory order.
            var allValid = pages.ToArray();
            Random.Shared.Shuffle(allValid);

            var filtered = allValid
                .Where(p => p.RemainingBytes >= minContiguous
                         && IsInMmap(p.PageBase, mmapIntervals))
                .Take(pageCount)
                .ToArray();

            _log.Info($"Memory map: {cMap} regions, {filtered.Length} usable pages" +
                      (mmapIntervals.Length > 0 ? $" (mmap-filtered, {mmapIntervals.Length} intervals)" : " (no mmap filter)"));
            return filtered;
        }
        finally { VmmNative.VMMDLL_MemFree(pMap); }
    }

    private static (ulong Start, ulong End)[] LoadMmapIntervals()
    {
        if (!HasCachedMmap()) return Array.Empty<(ulong, ulong)>();
        try
        {
            var intervals = new List<(ulong, ulong)>();
            foreach (var line in File.ReadLines(MmapCachePath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#') || trimmed.Length == 0) continue;
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // Format: "NNNN XXXXXXXXXXXXXXXX - YYYYYYYYYYYYYYYY"
                if (parts.Length < 4) continue;
                int idx = parts[0].Length == 4 && !parts[0].StartsWith("0x") ? 1 : 0;
                if (idx + 2 >= parts.Length) continue;
                if (!ulong.TryParse(parts[idx],     System.Globalization.NumberStyles.HexNumber, null, out ulong start)) continue;
                if (!ulong.TryParse(parts[idx + 2], System.Globalization.NumberStyles.HexNumber, null, out ulong end)) continue;
                if (end >= start) intervals.Add((start, end));
            }
            return intervals.ToArray();
        }
        catch { return Array.Empty<(ulong, ulong)>(); }
    }

    private static bool IsInMmap(ulong pageBase, (ulong Start, ulong End)[] intervals)
    {
        if (intervals.Length == 0) return true; // no filter — pass everything through
        foreach (var (start, end) in intervals)
            if (pageBase >= start && pageBase <= end) return true;
        return false;
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

    /// <summary>
    /// Connects to the FPGA, reads the physical memory map of the connected PC,
    /// and returns the mmap content as a string ready to save.
    /// The caller is responsible for writing the file wherever needed.
    /// </summary>
    public Task<MmapResult> GenerateMmapAsync(
        IProgress<FlashProgress>? progress,
        CancellationToken ct) =>
        Task.Run(() => GenerateMmap(progress, ct), ct);

    private MmapResult GenerateMmap(
        IProgress<FlashProgress>? progress,
        CancellationToken ct)
    {
        var result = new MmapResult();
        IntPtr hVMM = IntPtr.Zero;
        IntPtr pMap = IntPtr.Zero;

        try
        {
            progress?.Report(Prog("Connecting", 10, "Connecting to DMA device..."));
            hVMM = ConnectDma();

            progress?.Report(Prog("Reading", 40, "Reading physical memory map..."));
            if (!VmmNative.VMMDLL_Map_GetPhysMem(hVMM, out pMap) || pMap == IntPtr.Zero)
                throw new InvalidOperationException("Failed to retrieve physical memory map.");

            uint cMap = (uint)Marshal.ReadInt32(pMap, 24);
            if (cMap == 0)
                throw new InvalidOperationException("Physical memory map is empty.");

            const int headerSize = 32;
            const int entrySize  = 16;

            var lines = new List<string>
            {
                "# Physical memory map generated by NexusForge.",
                "# Save as mmap.txt (or memmap.txt) in your radar/DMA tool folder.",
                "# Required on AMD and Intel with IOMMU active to prevent link wedge.",
                "# Re-generate if you change RAM or move to a different target PC.",
                ""
            };

            var regions = new List<(ulong Start, ulong End)>();
            for (int i = 0; i < (int)cMap; i++)
            {
                IntPtr pEntry = pMap + headerSize + i * entrySize;
                ulong pa = (ulong)Marshal.ReadInt64(pEntry, 0);
                ulong cb = (ulong)Marshal.ReadInt64(pEntry, 8);
                if (cb == 0) continue;
                regions.Add((pa, pa + cb - 1));
            }

            for (int i = 0; i < regions.Count; i++)
                lines.Add($"{i:D4} {regions[i].Start:X016} - {regions[i].End:X016}");

            result.RegionCount = regions.Count;
            result.TotalRamGb  = regions.Sum(r => (double)(r.End - r.Start + 1)) / (1024.0 * 1024 * 1024);
            result.Content     = string.Join("\n", lines);
            result.Success     = true;

            // Auto-save to canonical cache location so ConnectDma() + BuildMemoryMap() pick it up.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MmapCachePath)!);
                File.WriteAllText(MmapCachePath, result.Content);
                _log.Info($"mmap auto-saved to cache: {MmapCachePath}");
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not auto-save mmap cache: {ex.Message}");
            }

            _log.Info($"Memory map ready: {result.RegionCount} regions, {result.TotalRamGb:F1} GB.");
            progress?.Report(Prog("Ready", 100,
                $"Ready — {result.RegionCount} regions, {result.TotalRamGb:F1} GB. Choose where to save."));
        }
        catch (Exception ex)
        {
            result.ErrorMessage = SanitizeError(ex.Message);
            _log.Error($"mmap generation failed: {result.ErrorMessage}");
        }
        finally
        {
            if (pMap != IntPtr.Zero) VmmNative.VMMDLL_MemFree(pMap);
            if (hVMM != IntPtr.Zero) VmmNative.VMMDLL_Close(hVMM);
            if (!result.Success)
                progress?.Report(Prog("Failed", 0, result.ErrorMessage));
        }
        return result;
    }
}
