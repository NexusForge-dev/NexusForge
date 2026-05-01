using System.Reflection;

namespace NexusForge.Models;

public class AppSettings
{
    /// <summary>
    /// Reads the version from the assembly so it always matches the running binary.
    /// Hardcoding it (the previous behavior) caused the auto-updater to think every
    /// newer-than-1.1.3 build was "still 1.1.3 — please update", looping forever:
    /// download → replace exe → restart → download again. Reading from the assembly
    /// kills that loop.
    /// </summary>
    public string Version { get; set; } = ResolveAssemblyVersion();

    public string FpgaPart { get; set; } = "xc7a75tfgg484";
    public string SpiFlashPart { get; set; } = "is25lp128f";
    public string ExpectedIdCode { get; set; } = "0x0362d093";
    public int FlashTimeoutSeconds { get; set; } = 300;
    public bool VerifyAfterFlash { get; set; } = true;
    public string LastFirmwarePath { get; set; } = string.Empty;
    public string LogLevel { get; set; } = "Info";

    private static string ResolveAssemblyVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // Prefer InformationalVersion (csproj sets "1.1.3"), fall back to AssemblyVersion.
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (info != null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
            {
                // Strip any "+commit" build metadata from SemVer.
                var v = info.InformationalVersion;
                var plus = v.IndexOf('+');
                if (plus >= 0) v = v.Substring(0, plus);
                return v;
            }
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }
}
