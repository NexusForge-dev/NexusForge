using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using NexusForge.Services;

namespace NexusForge.ViewModels;

public class BarProbeViewModel : BaseViewModel
{
    private readonly BarProbeService _probe;
    private readonly LogService _log;

    // Inputs
    private string _addressHex = "DCC001F0";   // default = ASMedia 1062 BAR + 0x1F0 (engine dbg counters)
    private string _length     = "16";          // bytes
    private string _periodMs   = "100";         // poll interval
    private string _logFile    = "";            // poll log path (auto-filled if empty)

    // State
    private bool _isProbing;
    private bool _isPolling;
    private string _lastReadHex = "—";
    private string _lastReadParsed = "—";
    private string _statusText = "Idle";
    private string _statusColor = "#6E7681";
    private long _pollSamples;
    private CancellationTokenSource? _pollCts;

    public string AddressHex
    {
        get => _addressHex;
        set => SetProperty(ref _addressHex, value);
    }

    public string Length
    {
        get => _length;
        set => SetProperty(ref _length, value);
    }

    public string PeriodMs
    {
        get => _periodMs;
        set => SetProperty(ref _periodMs, value);
    }

    public string LogFile
    {
        get => _logFile;
        set => SetProperty(ref _logFile, value);
    }

    public bool IsProbing
    {
        get => _isProbing;
        set
        {
            SetProperty(ref _isProbing, value);
            ((AsyncRelayCommand)ReadOnceCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)StartPollCommand).NotifyCanExecuteChanged();
        }
    }

    public bool IsPolling
    {
        get => _isPolling;
        set
        {
            SetProperty(ref _isPolling, value);
            ((AsyncRelayCommand)ReadOnceCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)StartPollCommand).NotifyCanExecuteChanged();
            ((RelayCommand)StopPollCommand).NotifyCanExecuteChanged();
        }
    }

    public string LastReadHex
    {
        get => _lastReadHex;
        set => SetProperty(ref _lastReadHex, value);
    }

    public string LastReadParsed
    {
        get => _lastReadParsed;
        set => SetProperty(ref _lastReadParsed, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    public long PollSamples
    {
        get => _pollSamples;
        set => SetProperty(ref _pollSamples, value);
    }

    public ICommand ReadOnceCommand   { get; }
    public ICommand StartPollCommand  { get; }
    public ICommand StopPollCommand   { get; }

    public BarProbeViewModel(BarProbeService probe, LogService log)
    {
        _probe = probe;
        _log   = log;

        ReadOnceCommand  = new AsyncRelayCommand(ReadOnceAsync,  () => !IsProbing && !IsPolling);
        StartPollCommand = new AsyncRelayCommand(StartPollAsync, () => !IsProbing && !IsPolling);
        StopPollCommand  = new RelayCommand(StopPoll,            () => IsPolling);
    }

    private async Task ReadOnceAsync()
    {
        if (!TryParseAddress(AddressHex, out var addr) || !TryParseLength(Length, out var len))
            return;

        IsProbing = true;
        SetStatus($"Reading 0x{addr:X16} ({len} bytes)...", "#58A6FF");
        try
        {
            var data = await Task.Run(() => _probe.ReadOnce(addr, len));
            LastReadHex = ToHex(data);
            LastReadParsed = ParseAsDwords(data);
            SetStatus($"Read OK at 0x{addr:X16}", "#3FB950");
            _log.Info($"BarProbe: read {data.Length} B at 0x{addr:X16}");
        }
        catch (Exception ex)
        {
            LastReadHex = "(read failed)";
            LastReadParsed = ex.Message;
            SetStatus("Read failed", "#F85149");
            _log.Error($"BarProbe read failed: {ex.Message}");
        }
        finally
        {
            IsProbing = false;
        }
    }

    private async Task StartPollAsync()
    {
        if (!TryParseAddress(AddressHex, out var addr) ||
            !TryParseLength(Length, out var len) ||
            !TryParsePeriod(PeriodMs, out var period))
            return;

        var path = string.IsNullOrWhiteSpace(LogFile)
            ? Path.Combine(Path.GetTempPath(), $"barprobe_{DateTime.Now:yyyyMMdd_HHmmss}.log")
            : LogFile;
        LogFile = path;

        IsPolling = true;
        PollSamples = 0;
        SetStatus($"Polling -> {Path.GetFileName(path)}", "#58A6FF");
        _log.Info($"BarProbe poll start: addr=0x{addr:X16} len={len} period={period}ms log={path}");

        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;

        // Background poll loop. UI updates via dispatcher inside the loop.
        var pollTask = Task.Run(async () =>
        {
            try
            {
                await _probe.PollAsync(addr, len, period, path, ct);
            }
            catch (Exception ex)
            {
                _log.Error($"BarProbe poll loop crashed: {ex.Message}");
            }
        });

        // UI sample counter — read the log file size every second as a rough proxy.
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var lines = 0L;
                        using var sr = new StreamReader(path);
                        while (await sr.ReadLineAsync() is not null) lines++;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => PollSamples = lines);
                    }
                }
                catch { /* file in use, skip */ }
                await Task.Delay(1000, ct).ContinueWith(_ => { });
            }
        });

        await pollTask;

        IsPolling = false;
        SetStatus($"Poll stopped. Samples={PollSamples}, file={Path.GetFileName(path)}", "#3FB950");
        _log.Info($"BarProbe poll stopped: {PollSamples} samples in {path}");
    }

    private void StopPoll()
    {
        _pollCts?.Cancel();
        _log.Info("BarProbe stop requested");
    }

    private void SetStatus(string text, string color)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            StatusText  = text;
            StatusColor = color;
        });
    }

    // ---- parsing helpers ---------------------------------------------------

    private bool TryParseAddress(string s, out ulong addr)
    {
        addr = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var trim = s.Trim();
        if (trim.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trim = trim[2..];
        if (!ulong.TryParse(trim, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr))
        {
            SetStatus("Address must be hex (e.g. DCC001F0 or 0xDCC001F0)", "#F85149");
            return false;
        }
        return true;
    }

    private bool TryParseLength(string s, out uint len)
    {
        len = 0;
        if (!uint.TryParse(s, out len) || len == 0 || len > 4096)
        {
            SetStatus("Length must be 1..4096 bytes", "#F85149");
            return false;
        }
        return true;
    }

    private bool TryParsePeriod(string s, out int period)
    {
        period = 0;
        if (!int.TryParse(s, out period) || period < 10 || period > 60_000)
        {
            SetStatus("Period must be 10..60000 ms", "#F85149");
            return false;
        }
        return true;
    }

    private static string ToHex(byte[] data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 3);
        for (int i = 0; i < data.Length; i++)
        {
            sb.Append(data[i].ToString("X2"));
            if (i + 1 < data.Length) sb.Append(' ');
            if ((i + 1) % 16 == 0 && i + 1 < data.Length) sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string ParseAsDwords(byte[] data)
    {
        if (data.Length % 4 != 0)
            return "(length not 4-byte aligned)";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < data.Length; i += 4)
        {
            uint dw = BitConverter.ToUInt32(data, i);
            sb.AppendLine($"+0x{i:X3}: 0x{dw:X8}  ({dw})");
        }
        return sb.ToString().TrimEnd();
    }
}
