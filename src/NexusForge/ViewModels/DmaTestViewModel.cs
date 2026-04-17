using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using NexusForge.Models;
using NexusForge.Services;

namespace NexusForge.ViewModels;

public class DmaTestViewModel : BaseViewModel
{
    private readonly FtdiDriverService _ftdiService;
    private readonly DmaTestService _dmaTestService;
    private readonly LogService _logService;

    private bool _isFtdiBusy;
    private bool _isFtdiChecked;
    private bool _isFtdiDriverOk;
    private bool _isFtdiDeviceDetected;
    private string _ftdiStatusText = "Not checked";
    private string _ftdiStatusColor = "#6E7681";
    private string _ftdiVersionText = "—";
    private string _ftdiDeviceName = "—";
    private string _ftdiDriverType = "—";
    private string _ftdiBusyMessage = "";
    private string _ftdiInfPath = "";

    private bool _isTesting;
    private bool _hasResults;
    private int _testPercentage;
    private string _testMessage = "";
    private string _testRating = "—";
    private string _testRps = "—";
    private string _testLatency = "—";
    private string _testStatus = "Not tested";
    private string _testThroughput = "—";
    private string _testMinRead = "—";
    private string _testMaxRead = "—";
    private string _testFailed = "—";

    public bool IsFtdiBusy { get => _isFtdiBusy; set { SetProperty(ref _isFtdiBusy, value); ((AsyncRelayCommand)CheckFtdiCommand).NotifyCanExecuteChanged(); ((AsyncRelayCommand)InstallFtdiCommand).NotifyCanExecuteChanged(); ((AsyncRelayCommand)UninstallFtdiCommand).NotifyCanExecuteChanged(); } }
    public bool IsFtdiChecked { get => _isFtdiChecked; set { if (SetProperty(ref _isFtdiChecked, value)) { OnPropertyChanged(nameof(ShowFtdiNotConnected)); OnPropertyChanged(nameof(ShowFtdiDriverMissing)); } } }
    public bool IsFtdiDriverOk { get => _isFtdiDriverOk; set { if (SetProperty(ref _isFtdiDriverOk, value)) OnPropertyChanged(nameof(ShowFtdiDriverMissing)); } }
    public bool IsFtdiDeviceDetected { get => _isFtdiDeviceDetected; set { if (SetProperty(ref _isFtdiDeviceDetected, value)) { OnPropertyChanged(nameof(ShowFtdiNotConnected)); OnPropertyChanged(nameof(ShowFtdiDriverMissing)); } } }
    public string FtdiStatusText { get => _ftdiStatusText; set => SetProperty(ref _ftdiStatusText, value); }
    public string FtdiStatusColor { get => _ftdiStatusColor; set => SetProperty(ref _ftdiStatusColor, value); }
    public string FtdiVersionText { get => _ftdiVersionText; set => SetProperty(ref _ftdiVersionText, value); }
    public string FtdiDeviceName { get => _ftdiDeviceName; set => SetProperty(ref _ftdiDeviceName, value); }
    public string FtdiDriverType { get => _ftdiDriverType; set => SetProperty(ref _ftdiDriverType, value); }
    public string FtdiBusyMessage { get => _ftdiBusyMessage; set => SetProperty(ref _ftdiBusyMessage, value); }

    public bool ShowFtdiNotConnected => IsFtdiChecked && !IsFtdiDeviceDetected;
    public bool ShowFtdiDriverMissing => IsFtdiChecked && IsFtdiDeviceDetected && !IsFtdiDriverOk;

    public bool IsTesting { get => _isTesting; set { SetProperty(ref _isTesting, value); ((AsyncRelayCommand)RunSpeedTestCommand)?.NotifyCanExecuteChanged(); } }
    public bool HasResults { get => _hasResults; set => SetProperty(ref _hasResults, value); }
    public int TestPercentage { get => _testPercentage; set => SetProperty(ref _testPercentage, value); }
    public string TestMessage { get => _testMessage; set => SetProperty(ref _testMessage, value); }
    public string TestRating { get => _testRating; set => SetProperty(ref _testRating, value); }
    public string TestRps { get => _testRps; set => SetProperty(ref _testRps, value); }
    public string TestLatency { get => _testLatency; set => SetProperty(ref _testLatency, value); }
    public string TestStatus { get => _testStatus; set => SetProperty(ref _testStatus, value); }
    public string TestThroughput { get => _testThroughput; set => SetProperty(ref _testThroughput, value); }
    public string TestMinRead { get => _testMinRead; set => SetProperty(ref _testMinRead, value); }
    public string TestMaxRead { get => _testMaxRead; set => SetProperty(ref _testMaxRead, value); }
    public string TestFailed { get => _testFailed; set => SetProperty(ref _testFailed, value); }

    private int _selectedTestType = 0;
    public int SelectedTestType { get => _selectedTestType; set => SetProperty(ref _selectedTestType, value); }
    public string[] TestTypes => new[] { "Full Test", "Latency Test", "Throughput Test" };

    public ICommand CheckFtdiCommand { get; }
    public ICommand InstallFtdiCommand { get; }
    public ICommand UninstallFtdiCommand { get; }
    public ICommand RunSpeedTestCommand { get; }

    public DmaTestViewModel(FtdiDriverService ftdiService, DmaTestService dmaTestService, LogService logService)
    {
        _ftdiService = ftdiService;
        _dmaTestService = dmaTestService;
        _logService = logService;

        CheckFtdiCommand = new AsyncRelayCommand(CheckFtdiAsync, () => !IsFtdiBusy);
        InstallFtdiCommand = new AsyncRelayCommand(InstallFtdiAsync, () => !IsFtdiBusy);
        UninstallFtdiCommand = new AsyncRelayCommand(UninstallFtdiAsync, () => !IsFtdiBusy);
        RunSpeedTestCommand = new AsyncRelayCommand(RunSpeedTestAsync, () => !IsTesting);
    }

    private async Task CheckFtdiAsync()
    {
        IsFtdiBusy = true;
        FtdiBusyMessage = "Checking FTDI driver...";
        try
        {
            var info = await _ftdiService.CheckDriverAsync();
            IsFtdiDeviceDetected = info.IsDeviceDetected;
            IsFtdiDriverOk = info.IsDriverOk;
            FtdiStatusText = info.Status;
            FtdiStatusColor = info.IsDriverOk ? "#3FB950" : (info.IsDeviceDetected ? "#F85149" : "#6E7681");
            FtdiVersionText = info.Version;
            FtdiDeviceName = info.DeviceName;
            FtdiDriverType = info.DriverType;
            _ftdiInfPath = info.InfPath;
            IsFtdiChecked = true;
        }
        finally { IsFtdiBusy = false; FtdiBusyMessage = ""; }
    }

    private async Task InstallFtdiAsync()
    {
        IsFtdiBusy = true;
        FtdiBusyMessage = "Installing FTDI driver...";
        try
        {
            await _ftdiService.InstallDriverAsync();
            await CheckFtdiAsync();
        }
        finally { IsFtdiBusy = false; FtdiBusyMessage = ""; }
    }

    private async Task UninstallFtdiAsync()
    {
        IsFtdiBusy = true;
        FtdiBusyMessage = "Removing FTDI driver...";
        try
        {
            await _ftdiService.UninstallDriverAsync(_ftdiInfPath);
            await CheckFtdiAsync();
        }
        finally { IsFtdiBusy = false; FtdiBusyMessage = ""; }
    }

    private async Task RunSpeedTestAsync()
    {
        IsTesting = true;
        HasResults = false;
        ResetResults();

        try
        {
            var progress = new Progress<FlashProgress>(p =>
            {
                TestPercentage = p.Percentage;
                TestMessage = p.Message;
            });

            DmaTestResult result = SelectedTestType switch
            {
                0 => await _dmaTestService.RunFullTestAsync(progress, CancellationToken.None),
                1 => await _dmaTestService.RunLatencyTestAsync(TimeSpan.FromSeconds(30), progress, CancellationToken.None),
                2 => await _dmaTestService.RunThroughputTestAsync(TimeSpan.FromSeconds(15), progress, CancellationToken.None),
                _ => await _dmaTestService.RunFullTestAsync(progress, CancellationToken.None)
            };

            ApplyResults(result);
        }
        catch (Exception ex)
        {
            _logService.Error($"Speed test error: {ex.Message}");
            TestStatus = "Error";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void ResetResults()
    {
        TestPercentage = 0;
        TestMessage = "Starting...";
        TestStatus = "Running";
        TestRating = "—";
        TestRps = "—";
        TestLatency = "—";
        TestThroughput = "—";
        TestMinRead = "—";
        TestMaxRead = "—";
        TestFailed = "—";
    }

    private void ApplyResults(DmaTestResult result)
    {
        if (!result.Success)
        {
            TestRating = "FAIL";
            TestStatus = "Failed";
            HasResults = true;
            return;
        }

        TestStatus = result.OverallRating == "FAIL" ? "Fail" : "Pass";
        TestRating = result.OverallRating;

        if (result.LatencyRps > 0)
        {
            TestRps = $"{result.LatencyRps:N0}";
            TestLatency = $"{result.LatencyAvgUs:N0} us";
            TestMinRead = $"{result.LatencyMinUs:N0} us";
            TestMaxRead = $"{result.LatencyMaxUs:N0} us";
        }

        if (result.ThroughputMBps > 0)
            TestThroughput = $"{result.ThroughputMBps:F1} MB/s";
        else if (result.ThroughputRating == "SKIP")
            TestThroughput = "Skipped";

        long totalFailed = result.LatencyFailedReads + result.ThroughputFailedReads;
        TestFailed = $"{totalFailed:N0}";

        HasResults = true;
    }
}
