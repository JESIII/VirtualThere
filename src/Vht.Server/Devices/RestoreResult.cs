namespace Vht.Server.Devices;

/// <summary>Result of one handback attempt: putting a device back on the driver it had.</summary>
/// <remarks>
/// <para>
/// <see cref="NothingToDo"/> is a first-class outcome, not a failure: the server is asked to hand
/// a device back on every release, and most releases are of devices it never moved. That must be
/// quiet, because a log line claiming a handback when nothing was moved is the same dishonesty
/// this pair of calls exists to remove.
/// </para>
/// <para>
/// A failure carries the operator's undo path in <see cref="Detail"/>, because a handback that
/// fails is the one case where the machine is left as this server made it.
/// </para>
/// </remarks>
public readonly struct RestoreResult
{
    public bool Ok { get; init; }

    /// <summary>True when there was no record for this device: nothing was moved, nothing to undo.</summary>
    public bool NothingToDo { get; init; }

    /// <summary>True when Windows wants a restart before the restored binding takes effect.</summary>
    public bool RebootRequired { get; init; }

    public string Detail { get; init; }
    public int Win32Error { get; init; }

    /// <summary>The device instance this attempt was about, when it was about one specific instance.</summary>
    public string InstanceId { get; init; }

    public static RestoreResult NoRecord(string hardwareId) => new()
    {
        Ok = true,
        NothingToDo = true,
        Detail = $"no prior-binding record for {hardwareId}: this server has not moved a device with it",
    };

    public static RestoreResult Success(string instanceId, string priorInfPath, bool rebootRequired) => new()
    {
        Ok = true,
        InstanceId = instanceId,
        RebootRequired = rebootRequired,
        Detail = rebootRequired
            ? $"{instanceId} was re-selected onto {priorInfPath}; a reboot is required before that takes effect"
            : $"{instanceId} is back on {priorInfPath}",
    };

    public static RestoreResult Failure(string detail, int win32Error = 0, string instanceId = "") => new()
    {
        Ok = false,
        InstanceId = instanceId,
        Win32Error = win32Error,
        Detail = detail,
    };
}
