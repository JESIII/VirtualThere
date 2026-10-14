using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Vht.Ui.Model;
using Vht.Ui.Mvvm;

namespace Vht.Ui.ViewModels;

/// <summary>One discovered (or manually added) server in the client window.</summary>
public sealed class PeerRowViewModel : ObservableObject
{
    private PeerSnapshot _snapshot;

    public PeerRowViewModel(PeerSnapshot snapshot, ClientViewModel owner)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        AttachAllCommand = new AsyncRelayCommand(
            () => Owner.AttachAllAsync(this),
            () => Owner.CanControl && Devices.Count > 0);
    }

    public ClientViewModel Owner { get; }

    public string Id => _snapshot.Id;

    public string DisplayName => _snapshot.DisplayName;

    public string AddressText => _snapshot.AddressText;

    public string StatusText => _snapshot.StatusText;

    public bool Online => _snapshot.Online;

    public bool Manual => _snapshot.Manual;

    public string SourceText => _snapshot.Manual ? "added manually" : "discovered";

    public ObservableCollection<RemoteDeviceRowViewModel> Devices { get; } = new();

    public AsyncRelayCommand AttachAllCommand { get; }

    public void Update(PeerSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < snapshot.Devices.Count; i++)
        {
            RemoteDeviceSnapshot device = snapshot.Devices[i];
            seen.Add(device.BusId);

            RemoteDeviceRowViewModel? existing = null;
            foreach (RemoteDeviceRowViewModel row in Devices)
            {
                if (string.Equals(row.BusId, device.BusId, StringComparison.Ordinal))
                {
                    existing = row;
                    break;
                }
            }

            if (existing is null)
            {
                Devices.Insert(Math.Min(i, Devices.Count), new RemoteDeviceRowViewModel(device, Id, Owner));
            }
            else
            {
                existing.Update(device);
            }
        }

        for (int i = Devices.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Devices[i].BusId))
            {
                Devices.RemoveAt(i);
            }
        }

        Raise(nameof(DisplayName));
        Raise(nameof(AddressText));
        Raise(nameof(StatusText));
        Raise(nameof(Online));
        Raise(nameof(SourceText));
        AttachAllCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>One exported device on a remote server, with its attach controls.</summary>
public sealed class RemoteDeviceRowViewModel : ObservableObject
{
    private RemoteDeviceSnapshot _snapshot;
    private bool _autoAttach;
    private bool _suppressAutoAttachWrite;

    public RemoteDeviceRowViewModel(RemoteDeviceSnapshot snapshot, string peerId, ClientViewModel owner)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        PeerId = peerId;
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _autoAttach = snapshot.AutoAttach;

        AttachCommand = new AsyncRelayCommand(() => Owner.AttachAsync(this), () => Owner.CanControl && !Attached);
        DetachCommand = new AsyncRelayCommand(() => Owner.DetachAsync(this), () => Owner.CanControl && Attached);
    }

    public string PeerId { get; }

    public ClientViewModel Owner { get; }

    public string BusId => _snapshot.BusId;

    public string DisplayName => _snapshot.DisplayName;

    public string Identity => _snapshot.Identity;

    public bool Attached => _snapshot.Attached;

    public bool PasswordRequired => _snapshot.PasswordRequired;

    public string StatusText => _snapshot.StatusText;

    public string AttachButtonText => Attached ? "Detach" : "Attach";

    /// <summary>Stable per-row automation ids for UI Automation and the screenshot driver.</summary>
    public string ToggleAutomationId => "toggle:" + PeerId + ":" + BusId;

    public string AutoAttachAutomationId => "auto:" + PeerId + ":" + BusId;

    public AsyncRelayCommand AttachCommand { get; }

    public AsyncRelayCommand DetachCommand { get; }

    /// <summary>
    /// The per-device auto-attach grant. Setting it writes through to the service; the value
    /// is then re-read from the next snapshot, so the checkbox reflects service state rather
    /// than optimistic UI state.
    /// </summary>
    public bool AutoAttach
    {
        get => _autoAttach;
        set
        {
            if (!Set(ref _autoAttach, value) || _suppressAutoAttachWrite)
            {
                return;
            }

            _ = Owner.SetAutoAttachAsync(this, value);
        }
    }

    /// <summary>Single command used by both the row button and the toolbar.</summary>
    public AsyncRelayCommand ToggleAttachCommand => Attached ? DetachCommand : AttachCommand;

    public void Update(RemoteDeviceSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

        _suppressAutoAttachWrite = true;
        try
        {
            AutoAttach = snapshot.AutoAttach;
        }
        finally
        {
            _suppressAutoAttachWrite = false;
        }

        Raise(nameof(DisplayName));
        Raise(nameof(Identity));
        Raise(nameof(Attached));
        Raise(nameof(StatusText));
        Raise(nameof(AttachButtonText));
        Raise(nameof(ToggleAutomationId));
        Raise(nameof(AutoAttachAutomationId));
        Raise(nameof(ToggleAttachCommand));
        AttachCommand.RaiseCanExecuteChanged();
        DetachCommand.RaiseCanExecuteChanged();
    }
}
