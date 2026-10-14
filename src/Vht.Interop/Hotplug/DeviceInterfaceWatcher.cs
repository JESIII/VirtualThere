using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Vht.Interop.Hotplug;

/// <summary>
/// Watches a device-interface class for arrivals and removals using
/// <c>CM_Register_Notification</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the hotplug mechanism the reference product uses in its Windows service, and it is
/// the reason the server needs no window handle to see device changes
/// (<c>CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE</c>, ARCHITECTURE.md §4.2 / Appendix B).
/// </para>
/// <para>
/// The callback runs on a system thread and must return quickly; it therefore does nothing but
/// copy the symbolic link out of the event data and raise <see cref="Changed"/>. Subscribers
/// are responsible for any work that follows, and are expected to coalesce: a device arrival
/// produces several notifications in a row on real hardware.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DeviceInterfaceWatcher : IDisposable
{
    /// <summary>
    /// Offset of the symbolic link inside <c>CM_NOTIFY_EVENT_DATA</c> for a device-interface
    /// event: <c>FilterType</c> (int) + <c>Reserved</c> (int) + <c>ClassGuid</c> (16 bytes).
    /// </summary>
    private const int SymbolicLinkOffset = 24;

    private readonly Guid _interfaceClass;
    private readonly CfgMgr32.CM_NOTIFY_CALLBACK _callback;
    private nint _notifyContext;
    private int _started;
    private int _disposed;

    public DeviceInterfaceWatcher(Guid interfaceClass)
    {
        _interfaceClass = interfaceClass;
        // Rooted in a field: the OS keeps calling this delegate for as long as the
        // registration lives, so it must not be collectable.
        _callback = OnNotify;
    }

    /// <summary>
    /// Raised for every interface arrival (<c>true</c>) and removal (<c>false</c>) of the
    /// watched class. <c>SymbolicLink</c> is the device path
    /// (<c>\\?\usb#vid_1234&amp;pid_5678#...</c>).
    /// </summary>
    public event Action<string, bool>? Changed;

    /// <summary>
    /// The <c>CR_*</c> code the class-specific filter registration returned, or 0 when it
    /// succeeded. Non-zero means the all-interface-classes form is the live registration.
    /// </summary>
    public int ClassSpecificError { get; private set; }

    /// <summary>True when the registration lives on the all-interface-classes filter.</summary>
    public bool UsingAllInterfaceClasses => Volatile.Read(ref _allInterfaceClasses) != 0;
    private int _allInterfaceClasses;

    /// <summary>True while a registration is live.</summary>
    public bool IsRunning => Volatile.Read(ref _started) != 0;

    /// <summary>Number of notifications received, for diagnostics and tests.</summary>
    public long Notifications => Volatile.Read(ref _notifications);
    private long _notifications;

    /// <summary>
    /// Registers for notifications. Returns false and the <c>CR_*</c> error code when the
    /// registration fails.
    /// </summary>
    public bool Start(out int error)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        error = 0;

        if (Volatile.Read(ref _started) != 0)
            return true;

        var filter = new CfgMgr32.CM_NOTIFY_FILTER
        {
            cbSize = Marshal.SizeOf<CfgMgr32.CM_NOTIFY_FILTER>(),
            Flags = 0,
            FilterType = CfgMgr32.CM_NOTIFY_FILTER_TYPE.DeviceInterface,
            Reserved = 0,
        };
        CfgMgr32.CM_NOTIFY_FILTER_UNION union = filter.Union;
        union.ClassGuid = _interfaceClass;
        filter.Union = union;

        int cr = CfgMgr32.CM_Register_Notification(ref filter, 0, _callback, out nint context);

        // This host answers the class-specific filter with CR_INVALID_DEVICE_ID (31), while the
        // all-interface-classes form of the same filter registers successfully. Retrying with
        // that form is cheap and loses nothing: the event data carries the symbolic link, and
        // subscribers re-enumerate through SetupAPI, so an arrival from another interface class
        // is a no-op rather than a wrong answer.
        if (cr != CfgMgr32.CR_SUCCESS)
        {
            ClassSpecificError = cr;

            var allClasses = new CfgMgr32.CM_NOTIFY_FILTER
            {
                cbSize = Marshal.SizeOf<CfgMgr32.CM_NOTIFY_FILTER>(),
                Flags = CfgMgr32.CM_NOTIFY_FILTER_FLAG_ALL_INTERFACE_CLASSES,
                FilterType = CfgMgr32.CM_NOTIFY_FILTER_TYPE.DeviceInterface,
                Reserved = 0,
            };

            int retry = CfgMgr32.CM_Register_Notification(ref allClasses, 0, _callback, out context);
            if (retry == CfgMgr32.CR_SUCCESS)
            {
                _allInterfaceClasses = 1;
                cr = retry;
            }
            else
            {
                error = retry;
                return false;
            }
        }

        _notifyContext = context;
        Volatile.Write(ref _started, 1);
        return true;
    }

    /// <summary>Unregisters. Idempotent.</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return;

        nint context = _notifyContext;
        _notifyContext = 0;
        if (context != 0)
            CfgMgr32.CM_Unregister_Notification(context);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Stop();
    }

    private uint OnNotify(nint hNotify, nint context, CfgMgr32.CM_NOTIFY_ACTION action, nint eventData, uint eventDataSize)
    {
        try
        {
            Interlocked.Increment(ref _notifications);

            // Only arrival/removal matter here; query-remove and property changes are for
            // drivers that veto removal, which a user-mode USB/IP exporter never does.
            bool present = action switch
            {
                CfgMgr32.CM_NOTIFY_ACTION.DeviceInterfaceArrival => true,
                CfgMgr32.CM_NOTIFY_ACTION.DeviceInterfaceRemoval => false,
                _ => false,
            };

            if (action is not (CfgMgr32.CM_NOTIFY_ACTION.DeviceInterfaceArrival
                or CfgMgr32.CM_NOTIFY_ACTION.DeviceInterfaceRemoval))
                return 0;

            string link = eventData != 0 && eventDataSize > SymbolicLinkOffset
                ? Marshal.PtrToStringUni(eventData + SymbolicLinkOffset) ?? string.Empty
                : string.Empty;

            Changed?.Invoke(link, present);
        }
        catch
        {
            // A subscriber threw. The callback must never propagate into the OS.
        }

        return 0;
    }

    /// <summary>Extracts the symbolic link from raw event data; used by the unit tests.</summary>
    public static string ReadSymbolicLink(nint eventData, uint eventDataSize)
    {
        if (eventData == 0 || eventDataSize <= SymbolicLinkOffset)
            return string.Empty;

        var builder = new StringBuilder();
        for (int offset = SymbolicLinkOffset; offset + 1 < eventDataSize; offset += 2)
        {
            char c = (char)Marshal.ReadInt16(eventData, offset);
            if (c == '\0') break;
            builder.Append(c);
        }
        return builder.ToString();
    }
}
