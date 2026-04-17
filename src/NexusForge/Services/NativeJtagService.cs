using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using NexusForge.Helpers;
using NexusForge.Models;

namespace NexusForge.Services;

public class NativeJtagService : IDisposable
{
    private readonly LogService _logService;
    private readonly AppSettings _settings;
    private string? _toolDir;
    private bool _extracted;

    public NativeJtagService(LogService logService, AppSettings settings)
    {
        _logService = logService;
        _settings = settings;
    }

    private void EnsureToolsExtracted()
    {
        if (_extracted && _toolDir != null && Directory.Exists(_toolDir))
            return;

        _toolDir = Path.Combine(Path.GetTempPath(), $"nf_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_toolDir);

        var assembly = Assembly.GetExecutingAssembly();
        var resources = new[]
        {
            "openocd.exe", "libhidapi-0.dll", "libusb-1.0.dll",
            "ch347.cfg", "xilinx-xc7.cfg", "xilinx-dna.cfg", "jtagspi.cfg",
            "bscan_spi_xc7a35t.bit", "bscan_spi_xc7a50t.bit",
            "bscan_spi_xc7a75t.bit", "bscan_spi_xc7a100t.bit",
            "bscan_spi_xc7a200t.bit",
        };

        foreach (var fileName in resources)
        {
            var destPath = Path.Combine(_toolDir, fileName);
            ResourceCrypto.ExtractResource(assembly, fileName, destPath);
        }

        _extracted = true;
    }

    private (bool success, string output, string error) RunOpenOcd(
        string commands, int timeoutMs = 15000, string[]? extraCfgFiles = null)
    {
        EnsureToolsExtracted();

        var openocdPath = Path.Combine(_toolDir!, "openocd.exe");
        var ch347Cfg = Path.Combine(_toolDir!, "ch347.cfg");
        var xc7Cfg = Path.Combine(_toolDir!, "xilinx-xc7.cfg");

        var cfgArgs = $"-f \"{ch347Cfg}\" -f \"{xc7Cfg}\"";
        if (extraCfgFiles != null)
        {
            foreach (var cfg in extraCfgFiles)
                cfgArgs += $" -f \"{Path.Combine(_toolDir!, cfg)}\"";
        }

        var args = $"-s \"{_toolDir}\" {cfgArgs} -c \"{commands}; exit\"";

        var psi = new ProcessStartInfo
        {
            FileName = openocdPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _toolDir!,
        };

        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();

        using var process = new Process();
        process.StartInfo = psi;

        try { process.Start(); }
        catch (Exception ex)
        {
            _logService.Error($"JTAG engine failed to start: {ex.Message}");
            return (false, "", ex.Message);
        }

        var outputTask = Task.Run(() =>
        {
            while (!process.StandardOutput.EndOfStream)
            {
                var line = process.StandardOutput.ReadLine();
                if (line != null) outputBuilder.AppendLine(line);
            }
        });

        var errorTask = Task.Run(() =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = process.StandardError.ReadLine();
                if (line != null) errorBuilder.AppendLine(line);
            }
        });

        bool exited = process.WaitForExit(timeoutMs);
        if (!exited)
        {
            try { process.Kill(true); } catch { }
            _logService.Error("JTAG operation timed out.");
            return (false, outputBuilder.ToString(), "Timed out");
        }

        Task.WaitAll(outputTask, errorTask);
        return (process.ExitCode == 0, outputBuilder.ToString(), errorBuilder.ToString());
    }

    public BoardInfo DetectBoard()
    {
        var boardInfo = new BoardInfo();

        _logService.Info("Detecting FPGA board via JTAG...");

        var (success, output, error) = RunOpenOcd("init; scan_chain");
        var combined = output + "\n" + error;

        if (!success && combined.Contains("no device found"))
        {
            _logService.Error("CH347 adapter not found.");
            _logService.Warn("Connect the JTAG USB cable and ensure the WCH driver is installed.");
            return boardInfo;
        }

        var idcodeMatch = Regex.Match(combined, @"found:\s*0x([0-9a-fA-F]{8})", RegexOptions.IgnoreCase);
        if (!idcodeMatch.Success)
            idcodeMatch = Regex.Match(combined, @"0x([0-9a-fA-F]{8})", RegexOptions.IgnoreCase);

        if (!idcodeMatch.Success)
        {
            if (combined.Contains("CH347 Open Succ", StringComparison.OrdinalIgnoreCase))
            {
                _logService.Error("JTAG adapter connected but FPGA not responding.");
                _logService.Warn("Replug the USB cable and ensure the board has power.");
            }
            else
            {
                _logService.Error("Could not detect FPGA on JTAG chain.");
                _logService.Warn("Check USB cable connection and WCH driver status.");
            }
            return boardInfo;
        }

        string idcodeHex = "0x" + idcodeMatch.Groups[1].Value.ToLowerInvariant();
        uint idcode = Convert.ToUInt32(idcodeMatch.Groups[1].Value, 16);
        uint idcodeMasked = idcode & 0x0FFFFFFF;

        string deviceName = idcodeMasked switch
        {
            0x0362E093 => "XC7A15T (Artix-7)",
            0x0362D093 => "XC7A35T (Artix-7)",
            0x0362C093 => "XC7A50T (Artix-7)",
            0x03632093 => "XC7A75T (Artix-7)",
            0x03631093 => "XC7A100T (Artix-7)",
            0x03636093 => "XC7A200T (Artix-7)",
            0x03647093 => "XC7K325T (Kintex-7)",
            0x0364C093 => "XC7K355T (Kintex-7)",
            0x03651093 => "XC7K410T (Kintex-7)",
            _ => $"Unknown Xilinx ({idcodeHex})"
        };

        _logService.Info($"FPGA detected: {deviceName}");
        _logService.Info($"IDCODE: {idcodeHex}");

        boardInfo.IsDetected = true;
        boardInfo.DeviceName = deviceName;
        boardInfo.IdCode = idcodeHex;
        boardInfo.Package = _settings.FpgaPart;
        boardInfo.JtagCable = "WCH CH347 (USB JTAG)";
        boardInfo.DetectedAt = DateTime.Now;

        _logService.Info("Reading Device DNA...");
        var (_, dnaOutput, dnaError) = RunOpenOcd(
            "init; " +
            "irscan xc7.tap 0x10; runtest 64; " +
            "irscan xc7.tap 0x17; " +
            "set raw [drscan xc7.tap 64 0]; " +
            "echo RAW_DNA=$raw; " +
            "runtest 64; " +
            "irscan xc7.tap 0x16; runtest 64");

        var dnaCombined = dnaOutput + "\n" + dnaError;
        var rawDnaMatch = Regex.Match(dnaCombined, @"RAW_DNA=([0-9a-fA-F]+)", RegexOptions.IgnoreCase);

        if (rawDnaMatch.Success)
        {
            string rawHex = rawDnaMatch.Groups[1].Value.PadLeft(16, '0');
            ulong rawVal = Convert.ToUInt64(rawHex, 16);
            uint word0 = (uint)(rawVal & 0xFFFFFFFF);
            uint word1 = (uint)(rawVal >> 32);
            ulong rawResult = ((ulong)ReverseBits32(word0) << 32) | ReverseBits32(word1);
            ulong dna = rawResult >> 7;

            string dnaHex = dna.ToString("x16");
            boardInfo.Dna = dnaHex;
            boardInfo.DnaFormatted = dnaHex;
            _logService.Info($"DNA: {dnaHex}");
        }
        else
        {
            _logService.Warn("Could not read Device DNA.");
        }

        return boardInfo;
    }

    private string? GetBscanProxy(uint idcodeMasked)
    {
        return idcodeMasked switch
        {
            0x0362D093 => "bscan_spi_xc7a35t.bit",
            0x0362C093 => "bscan_spi_xc7a50t.bit",
            0x03632093 => "bscan_spi_xc7a75t.bit",
            0x03631093 => "bscan_spi_xc7a100t.bit",
            0x03636093 => "bscan_spi_xc7a200t.bit",
            0x0362E093 => "bscan_spi_xc7a35t.bit",
            _ => null
        };
    }

    public FlashResult FlashFirmware(
        string firmwarePath,
        IProgress<FlashProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var fileName = Path.GetFileName(firmwarePath);

        _logService.Info($"Flashing firmware: {fileName}");
        progress?.Report(new FlashProgress { Stage = "Initializing", Percentage = 5, Message = "Connecting to JTAG..." });

        EnsureToolsExtracted();

        string ext = Path.GetExtension(firmwarePath).ToLowerInvariant();
        bool isBin = ext == ".bin";
        bool isBit = ext == ".bit";

        if (!isBin && !isBit)
        {
            _logService.Error($"Unsupported file type: {ext}. Use .bit or .bin files.");
            return new FlashResult { Success = false, ErrorMessage = $"Unsupported: {ext}" };
        }

        string firmwareOcd = firmwarePath.Replace("\\", "/");

        if (isBit)
        {
            _logService.Info("Mode: Direct JTAG programming (.bit → FPGA fabric)");
            _logService.Info("Note: This is volatile — firmware is lost on power cycle.");
            progress?.Report(new FlashProgress { Stage = "Programming", Percentage = 20, Message = $"Sending {fileName} to FPGA..." });

            string commands = $"init; xc7_program xc7.tap; pld load 0 {{{firmwareOcd}}}";
            var (success, output, error) = RunOpenOcd(commands, _settings.FlashTimeoutSeconds * 1000);
            sw.Stop();

            var combined = output + "\n" + error;
            bool ok = success ||
                      combined.Contains("loaded file", StringComparison.OrdinalIgnoreCase);

            if (ok)
            {
                _logService.Info($"Flash complete in {sw.Elapsed.TotalSeconds:F1}s");
                progress?.Report(new FlashProgress { Stage = "Complete", Percentage = 100, Message = "FPGA programmed!" });
                return new FlashResult { Success = true, Duration = sw.Elapsed };
            }

            return ReportFlashError(combined, sw.Elapsed, progress);
        }
        else
        {
            return FlashSpiWithProgress(firmwarePath, firmwareOcd, fileName, sw, progress, cancellationToken);
        }
    }

    public async Task<bool> HotResetFpgaAsync(IProgress<FlashProgress>? progress)
    {
        _logService.Info("Hot Reload: PCIe bus reset (no JTAG, no reboot)...");
        progress?.Report(new FlashProgress { Stage = "Finding", Percentage = 10, Message = "Finding DMA PCIe device..." });

        const string findDeviceScript = @"
$pciDevs = Get-PnpDevice -Class 'System','SCSIAdapter','HDC','Net','Display','Unknown','Multifunction' `
           -ErrorAction SilentlyContinue |
           Where-Object { $_.InstanceId -match '^PCI\\' -and $_.Status -eq 'OK' }

# Also check for any PCI device with error state (freshly flashed DMA might be in error)
$errDevs = Get-PnpDevice -ErrorAction SilentlyContinue |
           Where-Object { $_.InstanceId -match '^PCI\\' -and $_.Status -ne 'OK' -and $_.Class -ne 'System' }

# Find the PCIe root port or bridge that is a parent of our DMA device
# For now, output all PCI devices so user can see what's on the bus
$allPci = Get-PnpDevice -ErrorAction SilentlyContinue |
          Where-Object { $_.InstanceId -match '^PCI\\' }

foreach ($d in $allPci) {
    $parent = (Get-PnpDeviceProperty -InstanceId $d.InstanceId `
               -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data
    Write-Output ('{0}|{1}|{2}|{3}' -f $d.InstanceId, $d.FriendlyName, $d.Status, $parent)
}
";
        var (_, devOut, _) = await RunPowerShellAsync(findDeviceScript);

        string? dmaInstanceId = null;
        string? dmaName = null;

        foreach (var line in devOut.Split('\n'))
        {
            var parts = line.Trim().Split('|');
            if (parts.Length < 4) continue;

            var instId = parts[0].Trim();
            var name = parts[1].Trim();
            var status = parts[2].Trim();

            if (name.Contains("Root Port", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Host Bridge", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("PCI Express", StringComparison.OrdinalIgnoreCase) && name.Contains("Root", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ISA Bridge", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("SMBus", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("RAM ", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("LPC ", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(name))
                continue;

            if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("AMD Radeon", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Intel", StringComparison.OrdinalIgnoreCase) && name.Contains("Graphics", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("USB ", StringComparison.OrdinalIgnoreCase) && name.Contains("Host", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("xHCI", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
                continue;

            dmaInstanceId = instId;
            dmaName = name;
        }

        if (dmaInstanceId == null)
        {
            _logService.Warn("Could not auto-detect the DMA PCIe device.");
            _logService.Info("Falling back to full PCIe bus rescan...");
            progress?.Report(new FlashProgress { Stage = "Rescan", Percentage = 30, Message = "Rescanning PCIe bus..." });

            await RunPnpUtil("/scan-devices");
            await Task.Delay(3000);

            _logService.Info("PCIe bus rescan complete.");
            progress?.Report(new FlashProgress { Stage = "Complete", Percentage = 100, Message = "Bus rescan complete" });
            return true;
        }

        _logService.Info($"Found DMA device: {dmaName}");
        progress?.Report(new FlashProgress { Stage = "Disabling", Percentage = 25, Message = $"Disabling: {dmaName}..." });

        string disableScript = $@"
$dev = Get-PnpDevice | Where-Object {{ $_.InstanceId -eq '{dmaInstanceId.Replace("'", "''")}' }}
if ($dev) {{
    Disable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output 'DISABLED'
}}
";
        var (_, disOut, _) = await RunPowerShellAsync(disableScript);
        bool disabled = disOut.Contains("DISABLED");

        if (disabled)
            _logService.Info("PCIe device disabled. Link is down.");
        else
            _logService.Warn("Could not disable device — trying direct bus rescan instead.");

        progress?.Report(new FlashProgress { Stage = "Waiting", Percentage = 45, Message = "Waiting for link to settle..." });
        await Task.Delay(2000);

        if (disabled)
        {
            _logService.Info("Re-enabling PCIe device...");
            progress?.Report(new FlashProgress { Stage = "Enabling", Percentage = 60, Message = "Re-enabling PCIe device..." });

            string enableScript = $@"
$dev = Get-PnpDevice | Where-Object {{ $_.InstanceId -eq '{dmaInstanceId.Replace("'", "''")}' }}
if ($dev) {{
    Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output 'ENABLED'
}}
";
            var (_, enOut, _) = await RunPowerShellAsync(enableScript);
            if (enOut.Contains("ENABLED"))
                _logService.Info("PCIe device re-enabled. Link retraining...");
        }

        progress?.Report(new FlashProgress { Stage = "Training", Percentage = 75, Message = "PCIe link training..." });
        await Task.Delay(3000);

        _logService.Info("Rescanning PCIe bus...");
        progress?.Report(new FlashProgress { Stage = "Rescan", Percentage = 85, Message = "Rescanning bus..." });
        await RunPnpUtil("/scan-devices");
        await Task.Delay(2000);

        progress?.Report(new FlashProgress { Stage = "Stabilizing", Percentage = 95, Message = "Stabilizing..." });
        await Task.Delay(1000);

        _logService.Info("Hot Reload complete.");
        _logService.Info("DMA device has been reset via PCIe bus (no JTAG, no reboot).");
        _logService.Info("The firmware loaded from SPI flash should now be active.");
        progress?.Report(new FlashProgress { Stage = "Complete", Percentage = 100, Message = "Hot Reload complete!" });
        return true;
    }

    private async Task RunPnpUtil(string args)
    {
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName               = "pnputil.exe",
                Arguments              = args,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            });
            if (proc != null) await proc.WaitForExitAsync();
        }
        catch { }
    }

    private static async Task<(int exitCode, string output, string error)> RunPowerShellAsync(
        string script, int timeoutMs = 15000)
    {
        var scriptFile = Path.Combine(Path.GetTempPath(), $"nf_{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptFile, script);

        var psi = new ProcessStartInfo
        {
            FileName               = "powershell.exe",
            Arguments              = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptFile}\"",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        using var proc = new Process { StartInfo = psi };
        var sb  = new System.Text.StringBuilder();
        var sbe = new System.Text.StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data != null) sbe.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var exited = await Task.Run(() => proc.WaitForExit(timeoutMs));
        if (!exited) { try { proc.Kill(true); } catch { } }

        try { File.Delete(scriptFile); } catch { }
        return (proc.ExitCode, sb.ToString(), sbe.ToString());
    }

    private FlashResult FlashSpiWithProgress(
        string firmwarePath, string firmwareOcd, string fileName,
        Stopwatch sw, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        _logService.Info("Mode: SPI flash programming (.bin → SPI flash)");
        progress?.Report(new FlashProgress { Stage = "Detecting", Percentage = 5, Message = "Identifying FPGA..." });

        var (_, dOut, dErr) = RunOpenOcd("init; scan_chain");
        var idMatch = Regex.Match(dOut + "\n" + dErr, @"found:\s*0x([0-9a-fA-F]{8})", RegexOptions.IgnoreCase);
        if (!idMatch.Success)
        {
            _logService.Error("Could not detect FPGA.");
            progress?.Report(new FlashProgress { Stage = "Failed", Percentage = 0, Message = "FPGA not detected" });
            return new FlashResult { Success = false, ErrorMessage = "FPGA not detected", Duration = sw.Elapsed };
        }

        uint idcode = Convert.ToUInt32(idMatch.Groups[1].Value, 16) & 0x0FFFFFFF;
        string? bscanFile = GetBscanProxy(idcode);
        if (bscanFile == null)
        {
            _logService.Error("No BSCAN proxy for this FPGA.");
            progress?.Report(new FlashProgress { Stage = "Failed", Percentage = 0, Message = "Unsupported FPGA" });
            return new FlashResult { Success = false, ErrorMessage = "Unsupported FPGA", Duration = sw.Elapsed };
        }

        long firmwareSize = 0;
        try { firmwareSize = new FileInfo(firmwarePath).Length; } catch { }
        int totalSectorsEstimate = Math.Max(1, (int)((firmwareSize + 65535) / 65536));

        _logService.Info("FPGA identified. Loading BSCAN SPI bridge...");
        progress?.Report(new FlashProgress { Stage = "Bridge", Percentage = 10, Message = "Loading BSCAN SPI bridge..." });

        EnsureToolsExtracted();

        var openocdPath = Path.Combine(_toolDir!, "openocd.exe");
        var ch347Cfg = Path.Combine(_toolDir!, "ch347.cfg");
        var xc7Cfg = Path.Combine(_toolDir!, "xilinx-xc7.cfg");
        var spiCfg = Path.Combine(_toolDir!, "jtagspi.cfg");

        string commands =
            $"init; " +
            $"jtagspi_init 0 {{{bscanFile}}}; " +
            $"jtagspi_program {{{firmwareOcd}}} 0x0; " +
            $"xc7_program xc7.tap";

        var args = $"-s \"{_toolDir}\" -f \"{ch347Cfg}\" -f \"{xc7Cfg}\" -f \"{spiCfg}\" -c \"{commands}; exit\"";

        var psi = new ProcessStartInfo
        {
            FileName = openocdPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _toolDir!,
        };

        var allOutput = new System.Text.StringBuilder();
        bool wrote = false, verified = false;
        int sectorsTotal = totalSectorsEstimate;
        int sectorsErased = 0;
        bool erasePhase = false;
        long totalPages = firmwareSize / 256;
        long pagesWritten = 0;
        DateTime lastWriteUpdate = DateTime.MinValue;

        using var process = new Process { StartInfo = psi };
        try { process.Start(); }
        catch (Exception ex)
        {
            _logService.Error($"Failed to start JTAG engine: {ex.Message}");
            return new FlashResult { Success = false, ErrorMessage = "Engine start failed", Duration = sw.Elapsed };
        }

        void ProcessLine(string line)
        {
            allOutput.AppendLine(line);
            var t = line.Trim();
            if (string.IsNullOrEmpty(t)) return;

            if (t.Contains("loaded file", StringComparison.OrdinalIgnoreCase) ||
                (t.Contains("loaded", StringComparison.OrdinalIgnoreCase) && t.Contains("pld", StringComparison.OrdinalIgnoreCase)))
            {
                _logService.Info("BSCAN SPI bridge loaded.");
                progress?.Report(new FlashProgress { Stage = "Probing", Percentage = 12, Message = "Probing SPI flash chip..." });
            }

            if (t.Contains("Found flash device", StringComparison.OrdinalIgnoreCase))
            {
                var fm = Regex.Match(t, @"Found flash device '([^']+)'", RegexOptions.IgnoreCase);
                string flashName = fm.Success ? fm.Groups[1].Value : "SPI flash";
                _logService.Info($"SPI flash detected: {flashName}");
                progress?.Report(new FlashProgress { Stage = "Detected", Percentage = 15, Message = $"Flash: {flashName}" });
            }

            var eraseRangeMatch = Regex.Match(t, @"erasing\s+sectors?\s+(\d+)\s+through\s+(\d+)", RegexOptions.IgnoreCase);
            if (eraseRangeMatch.Success)
            {
                int first = int.Parse(eraseRangeMatch.Groups[1].Value);
                int last = int.Parse(eraseRangeMatch.Groups[2].Value);
                sectorsTotal = last + 1;
                erasePhase = true;
                _logService.Info($"Erasing sectors {first}–{last} ({sectorsTotal} total)...");
                progress?.Report(new FlashProgress { Stage = "Erasing", Percentage = 20, Message = $"Erasing 0/{sectorsTotal} sectors..." });
            }

            var sectorMatch = Regex.Match(t, @"sector\s+(\d+)\s+took\s+(\d+)\s*ms", RegexOptions.IgnoreCase);
            if (sectorMatch.Success)
            {
                erasePhase = true;
                int sectorNum = int.Parse(sectorMatch.Groups[1].Value);
                int ms = int.Parse(sectorMatch.Groups[2].Value);
                sectorsErased = sectorNum + 1;

                int total = sectorsTotal > 0 ? sectorsTotal : totalSectorsEstimate;

                int pct = 15 + (int)(40.0 * sectorsErased / total);
                pct = Math.Min(pct, 54);

                progress?.Report(new FlashProgress
                {
                    Stage = "Erasing",
                    Percentage = pct,
                    Message = $"Erasing sector {sectorsErased}/{total} ({ms}ms)..."
                });
            }

            if (t.Contains("done in", StringComparison.OrdinalIgnoreCase) && !wrote)
            {
                erasePhase = false;
                var tm = Regex.Match(t, @"done in ([\d.]+)", RegexOptions.IgnoreCase);
                string time = tm.Success ? $" in {tm.Groups[1].Value}s" : "";
                _logService.Info($"Erase complete{time}.");
                progress?.Report(new FlashProgress { Stage = "Erased", Percentage = 45, Message = "Erase complete. Writing..." });
            }

            var pageMatch = Regex.Match(t, @"^0x([0-9a-fA-F]{8})\.$");
            if (pageMatch.Success)
            {
                pagesWritten++;
                erasePhase = false;

                if (totalPages > 0)
                {
                    int pct = 55 + (int)(33.0 * pagesWritten / totalPages);
                    pct = Math.Min(pct, 88);

                    var now = DateTime.UtcNow;
                    if ((now - lastWriteUpdate).TotalMilliseconds > 200 || pagesWritten == totalPages)
                    {
                        lastWriteUpdate = now;
                        long kbWritten = (pagesWritten * 256) / 1024;
                        long kbTotal = (totalPages * 256) / 1024;
                        progress?.Report(new FlashProgress
                        {
                            Stage = "Writing",
                            Percentage = pct,
                            Message = $"Writing {kbWritten}/{kbTotal} KB..."
                        });
                    }
                }
                return;
            }

            if ((t.Contains("wrote", StringComparison.OrdinalIgnoreCase) && t.Contains("bytes", StringComparison.OrdinalIgnoreCase)) ||
                (t.Contains("Close", StringComparison.OrdinalIgnoreCase) && pagesWritten > 0 && !wrote))
            {
                wrote = true;
                long kbWritten = (pagesWritten * 256) / 1024;
                _logService.Info($"Write complete: {kbWritten} KB ({pagesWritten} pages).");
                progress?.Report(new FlashProgress { Stage = "Written", Percentage = 90, Message = "Write complete!" });
            }

            if (t.Contains("verified", StringComparison.OrdinalIgnoreCase))
            {
                verified = true;
                _logService.Info("Firmware verified.");
                progress?.Report(new FlashProgress { Stage = "Verified", Percentage = 95, Message = "Verification passed!" });
            }

            if (t.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            {
                var sanitized = Regex.Replace(t, @"[A-Za-z]:[/\\][^\s""']+", "[…]");
                sanitized = Regex.Replace(sanitized, @"/tmp/[^\s""']+", "[…]");
                _logService.Error(sanitized);
            }
        }

        var errTask = Task.Run(() =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = process.StandardError.ReadLine();
                if (line != null) ProcessLine(line);
            }
        });

        var outTask = Task.Run(() =>
        {
            while (!process.StandardOutput.EndOfStream)
            {
                var line = process.StandardOutput.ReadLine();
                if (line != null) ProcessLine(line);
            }
        });

        bool exited = process.WaitForExit(_settings.FlashTimeoutSeconds * 1000);
        if (!exited)
        {
            try { process.Kill(true); } catch { }
            _logService.Error("SPI flash programming timed out.");
            progress?.Report(new FlashProgress { Stage = "Failed", Percentage = 0, Message = "Timed out" });
            return new FlashResult { Success = false, ErrorMessage = "Timed out", Duration = sw.Elapsed };
        }

        Task.WaitAll(errTask, outTask);
        sw.Stop();

        bool success = process.ExitCode == 0;

        if (success && pagesWritten > 0)
            verified = true;

        if (success || wrote)
        {
            long kbTotal = (pagesWritten * 256) / 1024;
            _logService.Info($"SPI flash complete in {sw.Elapsed.TotalSeconds:F1}s");
            _logService.Info($"  {kbTotal} KB written to SPI flash.");
            if (verified)
            {
                _logService.Info("  Firmware verified OK.");
                progress?.Report(new FlashProgress { Stage = "Verified", Percentage = 97, Message = "Verified OK!" });
                System.Threading.Thread.Sleep(500);
            }
            _logService.Info("Power cycle the target PC to load the new firmware.");
            progress?.Report(new FlashProgress { Stage = "Complete", Percentage = 100, Message = "SPI flash programmed!" });
            return new FlashResult { Success = true, Duration = sw.Elapsed, Verified = verified };
        }

        return ReportFlashError(allOutput.ToString(), sw.Elapsed, progress);
    }

    private FlashResult ReportFlashError(string combined, TimeSpan duration, IProgress<FlashProgress>? progress)
    {
        string errorMsg = "Programming failed";

        if (combined.Contains("file not found", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("couldn't open", StringComparison.OrdinalIgnoreCase))
            errorMsg = "Firmware file not accessible.";
        else if (combined.Contains("no device found", StringComparison.OrdinalIgnoreCase))
            errorMsg = "CH347 adapter not found. Reconnect USB.";
        else if (combined.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            errorMsg = "Programming timed out. Try again.";
        else if (combined.Contains("verify failed", StringComparison.OrdinalIgnoreCase) ||
                 combined.Contains("mismatch", StringComparison.OrdinalIgnoreCase))
            errorMsg = "Verification failed — flash may be corrupted.";
        else if (combined.Contains("can't open", StringComparison.OrdinalIgnoreCase))
            errorMsg = "Could not access JTAG adapter.";

        foreach (var line in combined.Split('\n'))
        {
            var t = line.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (t.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("can't", StringComparison.OrdinalIgnoreCase))
            {
                var sanitized = Regex.Replace(t, @"[A-Za-z]:[/\\][^\s""']+", "[path]");
                sanitized = Regex.Replace(sanitized, @"/tmp/[^\s""']+", "[path]");
                _logService.Error($"  {sanitized}");
            }
        }

        _logService.Error($"Flash failed: {errorMsg}");
        progress?.Report(new FlashProgress { Stage = "Failed", Percentage = 0, Message = errorMsg });

        return new FlashResult { Success = false, ErrorMessage = errorMsg, Duration = duration };
    }

    public void Cleanup()
    {
        if (_toolDir != null && Directory.Exists(_toolDir))
        {
            try { Directory.Delete(_toolDir, true); } catch { }
            _toolDir = null;
            _extracted = false;
        }
    }

    public void Dispose() => Cleanup();

    private static uint ReverseBits32(uint value)
    {
        uint result = 0;
        for (int i = 0; i < 32; i++)
        {
            result <<= 1;
            result |= (value >> i) & 1;
        }
        return result;
    }
}
