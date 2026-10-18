using System;
using System.Threading.Tasks;
using Vht.Ui.Model;
using Vht.Ui.Mvvm;

namespace Vht.Ui.ViewModels;

/// <summary>
/// One row of the server window's exported-device list: nickname, [vid:pid], attached-client
/// count, capture verdict, and the four per-device actions the card requires.
/// </summary>
public sealed class DeviceRowViewModel : ObservableObject
{
    private readonly IServerDeviceActions _actions;
    private DeviceSnapshot _snapshot;

    public DeviceRowViewModel(DeviceSnapshot snapshot, IServerDeviceActions actions)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));

        KickCommand = new AsyncRelayCommand(() => _actions.KickAsync(this), () => _actions.CanAct && AttachedClients > 0);
        ToggleIgnoreCommand = new AsyncRelayCommand(() => _actions.ToggleIgnoreAsync(this), () => _actions.CanAct);
        NicknameCommand = new AsyncRelayCommand(() => _actions.EditNicknameAsync(this), () => _actions.CanAct);
        PasswordCommand = new AsyncRelayCommand(() => _actions.EditPasswordAsync(this), () => _actions.CanAct);
    }

    public DeviceSnapshot Snapshot => _snapshot;

    public string DisplayName => _snapshot.DisplayName;

    public string Identity => _snapshot.Identity;

    public string BusId => _snapshot.BusId ?? "-";

    public string VendorProductText => _snapshot.VendorProductText;

    public int AttachedClients => _snapshot.AttachedClients;

    public string AttachedText => _snapshot.AttachedText;

    public string StatusText => _snapshot.StatusText;

    /// <summary>
    /// The per-device capture/rebind outcome ("not elevated", "P2 rebind failed: ...",
    /// "rebound ... a reboot is required ..."), empty when nothing was attempted yet.
    /// </summary>
    public string OutcomeText => _snapshot.OutcomeText;

    /// <summary>Status tooltip: the full capture reason plus the outcome of the last attempt.</summary>
    public string DetailText => _snapshot.DetailText;

    public bool Exported => _snapshot.Exported;

    public bool Exportable => _snapshot.Exportable;

    public bool Ignored => _snapshot.Ignored;

    public bool PasswordProtected => _snapshot.PasswordProtected;

    public string PasswordText => _snapshot.PasswordProtected ? "set" : "none";

    public string IgnoreButtonText => _snapshot.Ignored ? "Unignore" : "Ignore";

    public string ReasonText => CaptureText.DescribeReason(_snapshot.CaptureReason);

    /// <summary>
    /// Stable per-row automation ids so UI Automation (screen readers, and the screenshot
    /// driver in harness/Vht.Ui.UiDriver) can reach a specific device's action buttons.
    /// </summary>
    public string RowAutomationKey => _snapshot.BusId ?? _snapshot.Key;

    public string KickAutomationId => "kick:" + RowAutomationKey;

    public string IgnoreAutomationId => "ignore:" + RowAutomationKey;

    public string NicknameAutomationId => "nickname:" + RowAutomationKey;

    public string PasswordAutomationId => "password:" + RowAutomationKey;

    /// <summary>Automation id of this row's capture/rebind outcome text, for the harness.</summary>
    public string OutcomeAutomationId => "outcome:" + RowAutomationKey;

    public AsyncRelayCommand KickCommand { get; }

    public AsyncRelayCommand ToggleIgnoreCommand { get; }

    public AsyncRelayCommand NicknameCommand { get; }

    public AsyncRelayCommand PasswordCommand { get; }

    /// <summary>Refreshes this row in place so the list keeps selection and scroll position.</summary>
    public void Update(DeviceSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Raise(string.Empty);
        Raise(nameof(Snapshot));
        Raise(nameof(DisplayName));
        Raise(nameof(Identity));
        Raise(nameof(BusId));
        Raise(nameof(VendorProductText));
        Raise(nameof(AttachedClients));
        Raise(nameof(AttachedText));
        Raise(nameof(StatusText));
        Raise(nameof(OutcomeText));
        Raise(nameof(DetailText));
        Raise(nameof(Exported));
        Raise(nameof(Exportable));
        Raise(nameof(Ignored));
        Raise(nameof(PasswordProtected));
        Raise(nameof(PasswordText));
        Raise(nameof(IgnoreButtonText));
        Raise(nameof(ReasonText));
        Raise(nameof(RowAutomationKey));
        Raise(nameof(KickAutomationId));
        Raise(nameof(IgnoreAutomationId));
        Raise(nameof(NicknameAutomationId));
        Raise(nameof(PasswordAutomationId));
        Raise(nameof(OutcomeAutomationId));
        KickCommand.RaiseCanExecuteChanged();
        ToggleIgnoreCommand.RaiseCanExecuteChanged();
    }
}
