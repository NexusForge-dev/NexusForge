using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NexusForge.Helpers;

internal static partial class AntiTamper
{
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsDebuggerPresent();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CheckRemoteDebuggerPresent(
        nint hProcess, [MarshalAs(UnmanagedType.Bool)] out bool isDebuggerPresent);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        nint processHandle, int processInformationClass,
        out nint processInformation, int processInformationLength, out int returnLength);

    private static readonly byte[] _h = { 0x4E, 0x46, 0x2D, 0x76, 0x31 };
    private static readonly int[] _z = { 7, 31, 0x1F, 0x0007, 7 };

    public static void Verify()
    {
        var rеsult = false;
        rеsult |= P1();
        rеsult |= P2();
        rеsult |= P3();
        if (rеsult) Bail();
        StartWatchdog();
    }

    private static bool P1()
    {
        if (Debugger.IsAttached) return true;
        try { if (IsDebuggerPresent()) return true; } catch { }
        try
        {
            if (CheckRemoteDebuggerPresent(Process.GetCurrentProcess().Handle, out bool r) && r)
                return true;
        }
        catch { }
        try
        {
            int s = NtQueryInformationProcess(
                Process.GetCurrentProcess().Handle, _z[0],
                out nint dp, nint.Size, out _);
            if (s == 0 && dp != 0) return true;
        }
        catch { }
        return false;
    }

    private static bool P2()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            long а = 0;
            for (int i = 0; i < 1000; i++) а += i * i;
            sw.Stop();
            if (sw.ElapsedMilliseconds > 500) return true;
            GC.KeepAlive(а);
        }
        catch { }
        return false;
    }

    private static bool P3()
    {
        try
        {
            string[] m =
            {
                "gqVs|", "LOVs|", "grwGvp", "fkgde",
                "z64gej", "z65gej", "roolgej",
            };
            var ps = Process.GetProcesses();
            foreach (var p in ps)
            {
                try
                {
                    string n = p.ProcessName.ToLowerInvariant();
                    foreach (var е in m)
                    {
                        string d = new(е.Select(c => (char)(c - 3)).ToArray());
                        if (n.Contains(d, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private static void StartWatchdog()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(10_000);
                try
                {
                    if (P1() || P3()) Bail();
                }
                catch { }
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        t.Start();
    }

    private static void Bail()
    {
        try { Environment.Exit(0); } catch { }
        try { Process.GetCurrentProcess().Kill(); } catch { }
    }
}
