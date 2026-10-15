using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Vht.Ui.ControlPlane;
using Vht.Ui.Hosting;
using Vht.Ui.Model;
using Vht.Ui.Mvvm;

namespace Vht.Ui.ViewModels;

/// <summary>
/// The client window: pairs of "server + its exported devices", with attach/detach, the
/// per-device auto-attach grant, connection status and the shared log viewer.
/// </summary>
public sealed class ClientViewModel : ObservableObject, IDisposable
{
    private readonly VhtServiceClient _client;
    private readonly IPromptService _prompts;
    private readonly IWorkerSupervisor? _workers;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer? _timer;
    private bool _polling;
    private bool _disposed;
    private bool _serviceReachable;
    private bool _workerBusy;
    private PeerRowViewModel? _selectedPeer;
    private RemoteDeviceRowViewModel? _selectedDevice;
    private string _connectionText = "Contacting the " + Branding.ProductName + " client service...";

    public ClientViewModel(
        VhtServiceClient client,
        IPromptService prompts,
        TimeSpan? pollInterval = null,
        IWorkerSupervisor? workers = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _workers = workers;

        Log = new LogViewModel(client);

        AttachSelectedCommand = new AsyncRelayCommand(
            () => AttachAsync(SelectedDevice),
            () => CanControl && SelectedDevice is { Attached: false });
        DetachSelectedCommand = new AsyncRelayCommand(
            () => DetachAsync(SelectedDevice),
            () => CanControl && SelectedDevice is { Attached: true });
        AddPeerCommand = new AsyncRelayCommand(AddPeerAsync, () => CanControl);

        // Refresh is the client window's way in when nothing answers on the pipe: it is the only
        // control that stays enabled with no service, and pressing it starts the elevated worker
        // the same way the server window's Start does.
        RefreshCommand = new AsyncRelayCommand(RefreshWithWorkerAsync, () => !_workerBusy);
        MinimizeCommand = new RelayCommand(() => MinimizeRequested?.Invoke(this, EventArgs.Empty));
        AboutCommand = new RelayCommand(() => AboutRequested?.Invoke(this, EventArgs.Empty));
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        ToggleLogCommand = new RelayCommand(() => Log.IsVisible = !Log.IsVisible);

        if (pollInterval is not null)
        {
            _timer = new DispatcherTimer(pollInterval.Value, DispatcherPriority.Background, OnTick, Dispatcher.CurrentDispatcher);
        }
    }

    public string Title => Branding.ClientTitle;

    public ObservableCollection<PeerRowViewModel> Peers { get; } = new();

    public LogViewModel Log { get; }

    public AsyncRelayCommand AttachSelectedCommand { get; }

    public AsyncRelayCommand DetachSelectedCommand { get; }

    public AsyncRelayCommand AddPeerCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand MinimizeCommand { get; }

    public RelayCommand AboutCommand { get; }

    public RelayCommand ExitCommand { get; }

    public RelayCommand ToggleLogCommand { get; }

    public bool HasPeers => Peers.Count > 0;

    public bool EmptyStateVisible => !HasPeers;

    public string EmptyStateText => ServiceReachable
        ? "No servers have been discovered yet. Use Add server... to connect by address."
        : "Waiting for the " + Branding.ProductName + " client service on \\\\.\\pipe\\" + _client.PipeName + ".";

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
                RaiseCanExecute();
            }
        }
    }

    public bool CanControl => ServiceReachable;

    public PeerRowViewModel? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (Set(ref _selectedPeer, value))
            {
                Raise(nameof(Devices));
                Raise(nameof(SelectedDevice));
                Raise(nameof(HasSelection));
                RaiseCanExecute();
            }
        }
    }

    public RemoteDeviceRowViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (Set(ref _selectedDevice, value))
            {
                Raise(nameof(ToggleAttachText));
                RaiseCanExecute();
            }
        }
    }

    /// <summary>Label for the toolbar attach/detach button, which follows the selection.</summary>
    public string ToggleAttachText => SelectedDevice is { Attached: true } ? "Detach selected" : "Attach selected";

    public bool HasSelection => SelectedPeer is not null;

    public ObservableCollection<RemoteDeviceRowViewModel>? Devices => SelectedPeer?.Devices;

    public string StateText => !ServiceReachable
        ? "Client service not reachable"
        : Peers.Count == 0 ? "Connected - no servers discovered yet" : "Connected";

    public string SummaryText
    {
        get
        {
            int devices = 0;
            int attached = 0;
            foreach (PeerRowViewModel peer in Peers)
            {
                foreach (RemoteDeviceRowViewModel device in peer.Devices)
                {
                    devices++;
                    if (device.Attached)
                    {
                        attached++;
                    }
                }
            }

            return Peers.Count.ToString(CultureInfo.InvariantCulture) + " server(s), " +
                   devices.ToString(CultureInfo.InvariantCulture) + " device(s), " +
                   attached.ToString(CultureInfo.InvariantCulture) + " attached";
        }
    }

    public string ConnectionText
    {
        get => _connectionText;
        private set => Set(ref _connectionText, value);
    }

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
            (ControlPlaneStatus status, var peers, ControlPlaneError? error) =
                await _client.ListPeersAsync(_cts.Token).ConfigureAwait(true);

            if (status != ControlPlaneStatus.Ok)
            {
                MarkUnreachable(error?.Message);
                return;
            }

            ServiceReachable = true;
            ApplyPeers(peers);
            await Log.PollAsync(_cts.Token).ConfigureAwait(true);

            ConnectionText = "Connected to \\\\.\\pipe\\" + _client.PipeName + " - updated " +
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            Raise(nameof(StateText));
            Raise(nameof(SummaryText));
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

    /// <summary>
    /// Refresh pressed by the user (window button or the notification-area Start item): brings the
    /// privileged worker up first when nothing answers on the pipe.
    /// </summary>
    private async Task RefreshWithWorkerAsync()
    {
        _workerBusy = true;
        RaiseCanExecute();
        try
        {
            if (!ServiceReachable && _workers is not null)
            {
                WorkerStartResult result = await _workers.EnsureRunningAsync(_cts.Token).ConfigureAwait(true);
                Log.AppendLocal(ServerViewModel.DescribeStart(result));
            }

            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            _workerBusy = false;
            RaiseCanExecute();
        }
    }

    /// <summary>
    /// True when a control plane answers; starts the elevated worker when it does not. Used by the
    /// device and server actions so "attach this device" also works from a cold window.
    /// </summary>
    private async Task<bool> EnsureServiceAsync()
    {
        if (ServiceReachable)
        {
            return true;
        }

        if (_workers is null)
        {
            return false;
        }

        WorkerStartResult result = await _workers.EnsureRunningAsync(_cts.Token).ConfigureAwait(true);
        Log.AppendLocal(ServerViewModel.DescribeStart(result));
        if (result is WorkerStartResult.LaunchFailed or WorkerStartResult.TimedOut)
        {
            return false;
        }

        await RefreshAsync().ConfigureAwait(true);
        return ServiceReachable;
    }

    private void MarkUnreachable(string? reason)
    {
        ServiceReachable = false;
        ConnectionText = "No answer from \\\\.\\pipe\\" + _client.PipeName + " - " +
            (string.IsNullOrWhiteSpace(reason) ? "the service is not running." : reason);
        Peers.Clear();
        Raise(nameof(StateText));
        Raise(nameof(SummaryText));
        RaiseStateCounters();
        RaiseCanExecute();
    }

    private void RaiseStateCounters()
    {
        Raise(nameof(HasPeers));
        Raise(nameof(EmptyStateVisible));
        Raise(nameof(EmptyStateText));
    }

    private void ApplyPeers(System.Collections.Generic.IReadOnlyList<PeerSnapshot> peers)
    {
        string? selectedId = SelectedPeer?.Id;
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < peers.Count; i++)
        {
            PeerSnapshot snapshot = peers[i];
            seen.Add(snapshot.Id);

            PeerRowViewModel? existing = null;
            foreach (PeerRowViewModel row in Peers)
            {
                if (string.Equals(row.Id, snapshot.Id, StringComparison.Ordinal))
                {
                    existing = row;
                    break;
                }
            }

            if (existing is null)
            {
                Peers.Insert(Math.Min(i, Peers.Count), new PeerRowViewModel(snapshot, this));
            }
            else
            {
                existing.Update(snapshot);
            }
        }

        for (int i = Peers.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Peers[i].Id))
            {
                Peers.RemoveAt(i);
            }
        }

        if (selectedId is not null)
        {
            foreach (PeerRowViewModel row in Peers)
            {
                if (string.Equals(row.Id, selectedId, StringComparison.Ordinal))
                {
                    SelectedPeer = row;
                    return;
                }
            }
        }

        if (SelectedPeer is null && Peers.Count > 0)
        {
            SelectedPeer = Peers[0];
        }

        RaiseStateCounters();
    }

    public async Task AttachAsync(RemoteDeviceRowViewModel? device)
    {
        if (device is null)
        {
            return;
        }

        if (!await EnsureServiceAsync().ConfigureAwait(true))
        {
            Log.AppendLocal("clients.attach " + device.DisplayName + " - no client service is available");
            return;
        }

        ControlPlaneResponse response = await _client
            .SetAttachmentAsync(device.PeerId, device.BusId, attach: true, _cts.Token)
            .ConfigureAwait(true);
        Report("clients.attach " + device.DisplayName, response);
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task DetachAsync(RemoteDeviceRowViewModel? device)
    {
        if (device is null)
        {
            return;
        }

        if (!await EnsureServiceAsync().ConfigureAwait(true))
        {
            Log.AppendLocal("clients.detach " + device.DisplayName + " - no client service is available");
            return;
        }

        ControlPlaneResponse response = await _client
            .SetAttachmentAsync(device.PeerId, device.BusId, attach: false, _cts.Token)
            .ConfigureAwait(true);
        Report("clients.detach " + device.DisplayName, response);
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Records or revokes the standing auto-attach grant for one device. Nothing is attached
    /// implicitly: the grant is what allows the service to attach it later.
    /// </summary>
    public async Task SetAutoAttachAsync(RemoteDeviceRowViewModel device, bool enabled)
    {
        if (!await EnsureServiceAsync().ConfigureAwait(true))
        {
            Log.AppendLocal("clients.attach (auto) " + device.DisplayName + " - no client service is available");
            return;
        }

        ControlPlaneResponse response = await _client
            .SetAutoAttachAsync(device.PeerId, device.BusId, enabled, _cts.Token)
            .ConfigureAwait(true);
        Report("clients.attach (auto=" + (enabled ? "true" : "false") + ") " + device.DisplayName, response);
        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task AddPeerAsync()
    {
        string? input = _prompts.AskText(
            "Add a server",
            "Host, or host:port (optionally \"name = host:port\")");

        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        if (!await EnsureServiceAsync().ConfigureAwait(true))
        {
            Log.AppendLocal("peers.add " + input + " - no client service is available");
            return;
        }

        string? name = null;
        string endpoint = input.Trim();
        int equals = endpoint.IndexOf('=', StringComparison.Ordinal);
        if (equals > 0)
        {
            name = endpoint[..equals].Trim();
            endpoint = endpoint[(equals + 1)..].Trim();
        }

        int? port = null;
        int colon = endpoint.LastIndexOf(':');
        if (colon > 0 && int.TryParse(endpoint[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPort))
        {
            port = parsedPort;
            endpoint = endpoint[..colon];
        }

        ControlPlaneResponse response = await _client
            .AddPeerAsync(endpoint, port, name, _cts.Token)
            .ConfigureAwait(true);
        Report("peers.add " + input, response);
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Attaches every export of the selected peer, for the "attach two devices" workflow.</summary>
    public async Task AttachAllAsync(PeerRowViewModel peer)
    {
        foreach (RemoteDeviceRowViewModel device in peer.Devices)
        {
            if (!device.Attached)
            {
                await AttachAsync(device).ConfigureAwait(true);
            }
        }
    }

    /// <summary>Detaches everything currently attached; used by the tray "Stop" action.</summary>
    public async Task DetachAllAsync()
    {
        foreach (PeerRowViewModel peer in Peers)
        {
            foreach (RemoteDeviceRowViewModel device in peer.Devices)
            {
                if (device.Attached)
                {
                    await DetachAsync(device).ConfigureAwait(true);
                }
            }
        }
    }

    private void Report(string what, ControlPlaneResponse response)
    {
        Log.AppendLocal(response.IsOk ? what + " - ok" : what + " - " + response.Describe());
    }

    private void OnTick(object? sender, EventArgs e) => _ = RefreshAsync();

    private void RaiseCanExecute()
    {
        AttachSelectedCommand.RaiseCanExecuteChanged();
        DetachSelectedCommand.RaiseCanExecuteChanged();
        AddPeerCommand.RaiseCanExecuteChanged();
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
