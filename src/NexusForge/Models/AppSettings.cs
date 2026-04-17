namespace NexusForge.Models;

public class AppSettings
{
    public string Version { get; set; } = "1.0.0";
    public string FpgaPart { get; set; } = "xc7a75tfgg484";
    public string SpiFlashPart { get; set; } = "is25lp128f";
    public string ExpectedIdCode { get; set; } = "0x0362d093";
    public int FlashTimeoutSeconds { get; set; } = 300;
    public bool VerifyAfterFlash { get; set; } = true;
    public string LastFirmwarePath { get; set; } = string.Empty;
    public string LogLevel { get; set; } = "Info";
}
