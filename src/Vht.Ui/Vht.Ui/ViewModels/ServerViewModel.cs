using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Vht.Ui.ControlPlane;
using Vht.Ui.Hosting;
using Vht.Ui.Mvvm;

namespace Vht.Ui.ViewModels;

/// <summary>
/// Per-device management actions the server window offers. Implemented by
/// <see cref="ServerViewModel"/>; the row view model never talks to the control plane itself,
/// which is what keeps every row command testable through the same seam.
/// </summary>
public interface IServerDeviceActions
{
    Task KickAsync(DeviceRowViewModel device);

    Task ToggleIgnoreAsync(DeviceRowViewModel device);

    Task EditNicknameAsync(DeviceRowViewModel device);

    Task EditPasswordAsync(DeviceRowViewModel device);

    bool CanAct { get; }
}

public sealed class ServerViewModel : ObservableObject, IServerDeviceActions, IDisposable
{
    private const int DefaultPollSeconds = 2;

    private readonly VhtServiceClient _client;
    private readonly IPromptService _prompts;
    private readonly IWorkerSupervisor? _workers;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer? _timer;
    private bool _polling;
    private bool _disposed;
    private bool _serviceReachable;
    private bool _workerBusy;
    private ServerStatusSnapshot _status = new();
    private string _connectionText = "Contacting the " + Branding.ProductName + " service...";

    public ServerViewModel(
        VhtServiceClient client,
        IPromptService prompts,
        TimeSpan? pollInterval = null,
        IWorkerSupervisor? workers = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _workers = workers;

        Log = new LogViewModel(client);

        // Start is offered whenever the server is not up - including when nothing answers on the
        // pipe yet, which is the portable case: pressing it starts the elevated worker.
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning && !_workerBusy);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning || (_workers?.OwnsWorker ?? false));
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), () => CanControl);
        MinimizeCommand = new RelayCommand(() => MinimizeRequested?.Invoke(this, EventArgs.Empty));
        AboutCommand = new RelayCommand(() => AboutRequested?.Invoke(this, EventArgs.Empty));
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        ToggleLogCommand = new RelayCommand(() => Log.IsVisible = !Log.IsVisible);

        if (pollInterval is not null)
        {
            _timer = new DispatcherTimer(pollInterval.Value, DispatcherPriority.Background, OnTick, Dispatcher.CurrentDispatcher);
        }
    }

    public string Title => Branding.ServerTitle;

    public ObservableCollection<DeviceRowViewModel> Devices { get; } = new();

    public LogViewModel Log { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand MinimizeCommand { get; }

    public RelayCommand AboutCommand { get; }

    public RelayCommand ExitCommand { get; }

    public RelayCommand ToggleLogCommand { get; }

    public bool HasDevices => Devices.Count > 0;

    public bool EmptyStateVisible => !HasDevices;

    public string EmptyStateText => ServiceReachable
        ? "No USB devices are currently enumerated on this machine."
        : "Not running. Click Start to begin.";

    public event EventHandler? MinimizeRequested;

    public event EventHandler? AboutRequested;

    public event EventHandler? ExitRequested;

    public string PipeName => _client.PipeName;

    public bool ServiceReachable
    {
        get => _serviceReachable;
        private set
        {
            if (Set(ref _serviceReachable, value))
            {
                Raise(nameof(CanControl));
                Raise(nameof(StateText));
                Raise(nameof(ConnectionText));
                RaiseCanExecute();
            }
        }
    }

    public bool IsRunning => _status.Running;

    public string StateText => !ServiceReachable
        ? "Not running"
        : IsRunning ? _status.Describe() : "Stopped";

    public string ConnectionText
    {
        get => _connectionText;
        private set => Set(ref _connectionText, value);
    }

    public string DeviceSummary
    {
        get
        {
            int attached = 0;
            int blocked = 0;
            foreach (DeviceRowViewModel device in Devices)
            {
                attached += device.AttachedClients;
                if (!device.Snapshot.Exportable)
                {
                    blocked++;
                }
            }

            return Devices.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " device(s) enumerated, " +
                   attached.ToString(System.Globalization.CultureInfo.InvariantCulture) + " attached, " +
                   blocked.ToString(System.Globalization.CultureInfo.InvariantCulture) + " not exportable";
        }
    }

    /// <summary>True when the service answered the last poll, so actions can be offered.</summary>
    public bool CanControl => ServiceReachable;

    public bool CanAct => ServiceReachable;

    /// <summary>Starts the polling timer (no-op when constructed without an interval).</summary>
    public void Start()
    {
        _timer?.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_polling || _disposed)
        {
            return;
        }

        _polling = true;
        try
        {
            StatusResult status = await _client.GetServerStatusAsync(_cts.Token).ConfigureAwait(true);
            if (status.Status != ControlPlaneStatus.Ok)
            {
                MarkUnreachable(status.Error?.Message);
                return;
            }

            _status = status.Snapshot ?? new ServerStatusSnapshot();
            ServiceReachable = true;

            (ControlPlaneStatus deviceStatus, var devices, ControlPlaneError? deviceError) =
                await _client.ListDevicesAsync(_cts.Token).ConfigureAwait(true);
            if (deviceStatus != ControlPlaneStatus.Ok)
            {
                MarkUnreachable(deviceError?.Message);
                return;
            }

            ApplyDevices(devices);
            await Log.PollAsync(_cts.Token).ConfigureAwait(true);

            ConnectionText = "Connected to \\\\.\\pipe\\" + _client.PipeName + " - updated " +
                DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            Raise(nameof(IsRunning));
            Raise(nameof(StateText));
            Raise(nameof(DeviceSummary));
            RaiseStateCounters();
            RaiseCanExecute();
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            MarkUnreachable(ex.Message);
        }
        finally
        {
            _polling = false;
        }
    }

    private void MarkUnreachable(string? reason)
    {
        ServiceReachable = false;
        ConnectionText = "No answer from \\\\.\\pipe\\" + _client.PipeName + " - " +
            (string.IsNullOrWhiteSpace(reason) ? "the service is not running." : reason);
        Devices.Clear();
        Raise(nameof(IsRunning));
        Raise(nameof(StateText));
        Raise(nameof(DeviceSummary));
        RaiseStateCounters();
        RaiseCanExecute();
    }

    private void RaiseStateCounters()
    {
        Raise(nameof(HasDevices));
        Raise(nameof(EmptyStateVisible));
        Raise(nameof(EmptyStateText));
    }

    private void ApplyDevices(System.Collections.Generic.IReadOnlyList<Model.DeviceSnapshot> devices)
    {
        // Reuse rows so selection, scroll position and in-flight row commands survive a poll.
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < devices.Count; i++)
        {
            Model.DeviceSnapshot snapshot = devices[i];
            seen.Add(snapshot.Key);

            DeviceRowViewModel? existing = FindRow(snapshot.Key);
            if (existing is null)
            {
                Devices.Insert(System.Math.Min(i, Devices.Count), new DeviceRowViewModel(snapshot, this));
            }
            else
            {
                existing.Update(snapshot);
            }
        }

        for (int i = Devices.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Devices[i].Snapshot.Key))
            {
                Devices.RemoveAt(i);
            }
        }

        RaiseStateCounters();
    }

    private DeviceRowViewModel? FindRow(string key)
    {
        foreach (DeviceRowViewModel row in Devices)
        {
            if (string.Equals(row.Snapshot.Key, key, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return null;
    }

    private async Task StartAsync()
    {
        if (ServiceReachable)
        {
            // Something already serves this pipe - the installed service, or a worker another
            // instance started. Attach to it and let it do the starting; nothing is spawned, so
            // this window owns nothing to stop later.
            ControlPlaneResponse response = await _client.StartServerAsync(_cts.Token).ConfigureAwait(true);
            Report("server.start", response);
            await RefreshAsync().ConfigureAwait(true);
            return;
        }

        if (_workers is null)
        {
            return;
        }

        _workerBusy = true;
        RaiseCanExecute();
        try
        {
            WorkerStartResult result = await _workers.EnsureRunningAsync(_cts.Token).ConfigureAwait(true);
            ReportStart(_workers, result, Log);
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            _workerBusy = false;
            RaiseCanExecute();
        }
    }

    private async Task StopAsync()
    {
        if (ServiceReachable)
        {
            ControlPlaneResponse response = await _client.StopServerAsync(_cts.Token).ConfigureAwait(true);
            Report("server.stop", response);
            await RefreshAsync().ConfigureAwait(true);
            return;
        }

        // No control plane answers, but this window started a worker: stop that worker.
        if (_workers is not null)
        {
            bool stopped = await _workers.StopOwnedWorkerAsync(_cts.Token).ConfigureAwait(true);
            Log.AppendLocal(stopped ? "worker stopped" : "no worker of this window's to stop");
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Wording for the window log; never claims something happened that did not.</summary>
    internal static string DescribeStart(WorkerStartResult result) => result switch
    {
        WorkerStartResult.AlreadyRunning => "a control plane already answered on the pipe; attached to it",
        WorkerStartResult.Started => "started the elevated worker; it bound the control pipe",
        WorkerStartResult.LaunchFailed => "the worker could not be started",
        WorkerStartResult.TimedOut => "the worker did not bind the control pipe in time",
        _ => "worker start returned " + result,
    };

    /// <summary>
    /// Writes the start outcome to the window log, and - when the worker did not come up - the
    /// detail and the worker's own last lines underneath it.
    /// </summary>
    /// <remarks>
    /// This is the whole point of keeping the worker's output: "the worker did not bind the control
    /// pipe in time" is not diagnosable, and an operator staring at a device that never appears has
    /// nothing else to go on. The lines below it are what the server itself said before it went.
    /// </remarks>
    internal static void ReportStart(IWorkerSupervisor? workers, WorkerStartResult result, LogViewModel log)
    {
        ArgumentNullException.ThrowIfNull(log);
        log.AppendLocal(DescribeStart(result));

        if (result is not (WorkerStartResult.TimedOut or WorkerStartResult.LaunchFailed))
        {
            return;
        }

        if (workers?.LastError is { Length: > 0 } detail)
        {
            log.AppendLocal(detail);
        }

        if (workers is null)
        {
            return;
        }

        foreach (string line in workers.LastWorkerOutput)
        {
            log.AppendLocal("worker: " + line);
        }
    }

    public async Task KickAsync(DeviceRowViewModel device)
    {
        ControlPlaneResponse response = await _client.KickAsync(device.Snapshot.Key, _cts.Token).ConfigureAwait(true);
        Report("devices.kick " + device.DisplayName, response);
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task ToggleIgnoreAsync(DeviceRowViewModel device)
    {
        bool target = !device.Snapshot.Ignored;
        ControlPlaneResponse response = await _client
            .SetIgnoredAsync(device.Snapshot.Key, target, _cts.Token)
            .ConfigureAwait(true);
        Report((target ? "devices.ignore " : "devices.unignore ") + device.DisplayName, response);
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task EditNicknameAsync(DeviceRowViewModel device)
    {
        string? nickname = _prompts.AskText(
            "Device nickname",
            "Nickname for " + device.DisplayName + " " + device.Identity,
            device.Snapshot.Nickname);

        if (nickname is null)
        {
            return;
        }

        ControlPlaneResponse response = await _client
            .SetNicknameAsync(device.Snapshot.Key, nickname, _cts.Token)
            .ConfigureAwait(true);
        Report("devices.nickname " + device.DisplayName + " -> " + nickname, response);
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task EditPasswordAsync(DeviceRowViewModel device)
    {
        SecretPromptResult prompt = _prompts.AskSecret(
            "Device password",
            "Password required by clients before " + device.DisplayName + " is exported"
                + (device.Snapshot.PasswordProtected ? " (clear the field to remove it)" : string.Empty));

        if (!prompt.Accepted)
        {
            return;
        }

        string? value = prompt.Remove || string.IsNullOrEmpty(prompt.Value) ? null : prompt.Value;
        ControlPlaneResponse response = await _client
            .SetPasswordAsync(device.Snapshot.Key, value, _cts.Token)
            .ConfigureAwait(true);
        Report("devices.password " + device.DisplayName + (value is null ? " (removed)" : " (set)"), response);
        await RefreshAsync().ConfigureAwait(true);
    }

    private void Report(string what, ControlPlaneResponse response)
    {
        Log.AppendLocal(response.IsOk
            ? what + " - ok"
            : what + " - " + response.Describe());
    }

    private void OnTick(object? sender, EventArgs e) => _ = RefreshAsync();

    private void RaiseCanExecute()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Stop();
        _cts.Cancel();
        _cts.Dispose();
    }
}
