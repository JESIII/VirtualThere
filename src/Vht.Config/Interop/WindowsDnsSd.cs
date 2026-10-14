using System.Net;
using System.Runtime.InteropServices;
using Vht.Config.Diagnostics;
using Vht.Config.Discovery;

namespace Vht.Config.Interop;

/// <summary>
/// Windows DNSAPI (mDNS/DNS-SD) bindings, kept in an Interop folder so the P/Invoke surface is
/// greppable (ARCHITECTURE.md 9). ARCHITECTURE.md 7 puts this in <c>Vht.Interop.DnsApi</c>; it lives
/// here instead because this card owns <c>src/Vht.Config/**</c> only, and the file is self-contained -
/// moving it to Vht.Interop later is a file move plus a namespace change, no code change. It is
/// additive: nothing else in Vht.Config references dnsapi.dll.
///
/// Why DNSAPI and not Bonjour: the reference product's auto-find hard-depends on Bonjour (its client says
/// "Auto-find functionality requires Bonjour"). Windows DNSAPI is in-box on Windows 10 1703+, so
/// discovery works on a clean machine.
///
/// Every one of the following was established by running the API on a real Windows 11 host (the probe
/// is reproducible with <c>VHT_DISCOVERY_SELFTEST=1 dotnet test tests/Vht.Config.Tests</c>); each one
/// was a live failure first, so do not "simplify" them away:
/// <list type="number">
/// <item><c>DNS_SERVICE_REGISTER_REQUEST</c> is <c>Version, InterfaceIndex, pServiceInstance,
/// pRegisterCompletionCallback, pQueryContext, hCredentials, unicastEnabled</c> and
/// <c>DNS_SERVICE_BROWSE_REQUEST</c> is <c>Version, InterfaceIndex, QueryName, pBrowseCallback,
/// pQueryContext</c>. Getting the order wrong makes the runtime read a null slot as a pointer and
/// fail with DNS_STATUS 87 (ERROR_INVALID_PARAMETER).</item>
/// <item>Success is <c>DNS_REQUEST_PENDING</c> (9506), not 0 - both calls are asynchronous.</item>
/// <item><c>pRegisterCompletionCallback</c> must be non-null for register, and <c>pCancel</c> must be
/// non-null for browse: passing null to either returns 87 instead of starting the operation. Register
/// accepts a null cancel; browse does not.</item>
/// <item>The browse callback receives a <c>DNS_RECORD</c> list (PTR + SRV + TXT + A/AAAA), not a
/// <c>DNS_SERVICE_INSTANCE</c>, and the caller owns it via <c>DnsRecordListFree</c>. Registration is the
/// only path that uses <c>DNS_SERVICE_INSTANCE</c>/<c>DnsServiceConstructInstance</c>.</item>
/// <item>Names are fully qualified: the registered instance is <c>NAME._vht._tcp.local</c> and the browse
/// query is <c>_vht._tcp.local</c>. A name without the <c>.local</c> suffix registers but is never
/// discoverable.</item>
/// </list>
/// </summary>
public sealed class WindowsDnsSdProvider : IServiceDiscoveryProvider
{
    private const string DnsApi = "dnsapi.dll";

    /// <summary>DNS_QUERY_REQUEST_VERSION1.</summary>
    private const uint QueryRequestVersion1 = 1;

    /// <summary>DNS_REQUEST_PENDING - what both calls return when the asynchronous request was accepted.</summary>
    private const int DnsRequestPending = 9506;

    /// <summary>DNS_FREE_TYPE.DnsFreeRecordList.</summary>
    private const int DnsFreeRecordList = 1;

    // DNS record types we care about (windns.h / RFC 1035).
    private const ushort TypeA = 1;
    private const ushort TypePtr = 12;
    private const ushort TypeTxt = 16;
    private const ushort TypeAaaa = 28;
    private const ushort TypeSrv = 33;

    // Offsets inside DNS_RECORDW on a 64-bit host: pNext, pszName, wType, wDataLength, Flags, dwTtl,
    // dwReserved, then the Data union at 32.
    private const int RecordNextOffset = 0;
    private const int RecordNameOffset = 8;
    private const int RecordTypeOffset = 16;
    private const int RecordDataOffset = 32;

    /// <summary>Kept in a static field so the delegate stays rooted for the process lifetime: otherwise
    /// the runtime may collect the thunk while DNSAPI still holds the pointer.</summary>
    private static readonly RegisterCompleteCallback NoOpRegisterCallback = (_, _, _) => { };

    private readonly List<DnsQueryContext> _registrations = new();
    private readonly Dictionary<string, BrowseSession> _browses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConfigDiagnostic> _diagnostics = new();
    private readonly object _gate = new();
    private bool _exportsChecked;
    private string? _unsupportedReason;

    public string Name => "windows-dnsapi";

    public bool IsSupported
    {
        get
        {
            EnsureProbed();
            return _unsupportedReason is null;
        }
    }

    public string? UnsupportedReason
    {
        get
        {
            EnsureProbed();
            return _unsupportedReason;
        }
    }

    /// <summary>Diagnostics explaining why discovery is unavailable (missing dnsapi exports, non-Windows
    /// host, registration failure). Empty when everything works.</summary>
    public IReadOnlyList<ConfigDiagnostic> Diagnostics => _diagnostics;

    /// <summary>The fully qualified instance name DNSAPI wants: <c>NAME._vht._tcp.local</c>.</summary>
    public static string QualifiedInstanceName(ServiceInstance instance) =>
        $"{instance.InstanceName}.{QualifiedServiceType(instance.ServiceType)}";

    /// <summary>The fully qualified browse query name: <c>_vht._tcp.local</c>.</summary>
    public static string QualifiedServiceType(string serviceType) =>
        serviceType.EndsWith("." + ServiceTypes.Domain, StringComparison.OrdinalIgnoreCase)
            ? serviceType
            : $"{serviceType}.{ServiceTypes.Domain}";

    /// <summary>DNSAPI rejects an address literal as a host name and wants the <c>.local</c> domain;
    /// fall back to the machine name when the caller handed us an IP.</summary>
    internal static string QualifiedHostName(string? hostName)
    {
        var value = hostName?.Trim();
        if (string.IsNullOrEmpty(value) || IPAddress.TryParse(value.Trim('[', ']'), out _))
        {
            value = Environment.MachineName;
        }

        return value.EndsWith("." + ServiceTypes.Domain, StringComparison.OrdinalIgnoreCase)
            ? value
            : $"{value}.{ServiceTypes.Domain}";
    }

    internal static bool IsSuccess(int status) => status is 0 or DnsRequestPending;

    private void EnsureProbed()
    {
        if (_exportsChecked)
        {
            return;
        }

        _exportsChecked = true;
        if (!OperatingSystem.IsWindows())
        {
            _unsupportedReason = $"the Windows DNSAPI provider only runs on Windows (this host reports {RuntimeInformation.OSDescription}); use the in-memory provider or a manual host entry";
            return;
        }

        if (!NativeLibrary.TryLoad(DnsApi, out var handle))
        {
            _unsupportedReason = "dnsapi.dll could not be loaded";
            return;
        }

        try
        {
            foreach (var export in new[] { "DnsServiceRegister", "DnsServiceDeRegister", "DnsServiceConstructInstance", "DnsServiceFreeInstance", "DnsServiceBrowse", "DnsRecordListFree" })
            {
                if (!NativeLibrary.TryGetExport(handle, export, out _))
                {
                    _unsupportedReason = $"dnsapi.dll does not export {export} on this build of Windows";
                    return;
                }
            }
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    public Task RegisterAsync(ServiceInstance instance, CancellationToken cancellationToken = default)
    {
        EnsureProbed();
        if (!IsSupported)
        {
            return Task.CompletedTask;
        }

        var qualifiedName = QualifiedInstanceName(instance);
        var native = DnsServiceInstance.Create(instance, qualifiedName);
        try
        {
            var request = new DnsServiceRegisterRequest
            {
                Version = QueryRequestVersion1,
                InterfaceIndex = 0,
                ServiceInstance = native.Pointer,
                RegisterCompletionRoutine = NoOpRegisterCallback,
                QueryContext = IntPtr.Zero,
                Credentials = IntPtr.Zero,
                UnicastEnabled = false,
            };

            var status = NativeMethods.DnsServiceRegister(ref request, IntPtr.Zero);
            if (!IsSuccess(status))
            {
                throw new InvalidOperationException($"DnsServiceRegister returned DNS_STATUS {status} (0x{status:x8}) for '{qualifiedName}'");
            }

            lock (_gate)
            {
                _registrations.Add(new DnsQueryContext(qualifiedName, instance.InstanceName, native));
            }
        }
        catch
        {
            native.Dispose();
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task UpdateAsync(ServiceInstance instance, CancellationToken cancellationToken = default)
    {
        await UnregisterAsync(instance.InstanceName, cancellationToken).ConfigureAwait(false);
        await RegisterAsync(instance, cancellationToken).ConfigureAwait(false);
    }

    public Task UnregisterAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        var key = instanceName?.Trim() ?? string.Empty;
        List<DnsQueryContext> toRemove;
        lock (_gate)
        {
            toRemove = _registrations.Where(r => Matches(r, key)).ToList();
            foreach (var context in toRemove)
            {
                _registrations.Remove(context);
            }
        }

        foreach (var context in toRemove)
        {
            var request = new DnsServiceRegisterRequest
            {
                Version = QueryRequestVersion1,
                InterfaceIndex = 0,
                ServiceInstance = context.Native.Pointer,
                QueryContext = IntPtr.Zero,
                Credentials = IntPtr.Zero,
                UnicastEnabled = false,
            };
            try
            {
                NativeMethods.DnsServiceDeRegister(ref request, IntPtr.Zero);
            }
            finally
            {
                context.Native.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Accepts the bare instance name, the qualified name, or the whole FQDN.</summary>
    private static bool Matches(DnsQueryContext context, string instanceName) =>
        string.Equals(context.InstanceName, instanceName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(context.QualifiedName, instanceName, StringComparison.OrdinalIgnoreCase)
        || context.QualifiedName.StartsWith(instanceName + ".", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Browse the LAN for a service type. DNSAPI has no "cancel browse" entry point, so the browse is
    /// long-lived: the first call for a service type starts it, and each call returns a snapshot of the
    /// results accumulated so far after <paramref name="duration"/> elapses. That is also what makes it
    /// safe - the callback, its GCHandle and the cancel buffer stay rooted on the provider for its
    /// lifetime instead of being freed while DNSAPI may still call into them.
    /// </summary>
    public async Task<IReadOnlyList<ServiceInstance>> BrowseAsync(string serviceType, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        EnsureProbed();
        if (!IsSupported)
        {
            return Array.Empty<ServiceInstance>();
        }

        BrowseSession session;
        lock (_gate)
        {
            if (!_browses.TryGetValue(serviceType, out var existing))
            {
                existing = StartBrowse(serviceType);
                _browses[serviceType] = existing;
            }

            session = existing;
        }

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // the browse window closed normally
        }

        lock (session.Gate)
        {
            return session.Results.Values.ToList();
        }
    }

    public async Task<ServiceInstance?> ResolveAsync(string serviceType, string instanceName, CancellationToken cancellationToken = default)
    {
        var full = ServiceTypes.InstanceFullName(instanceName, serviceType);
        var found = await BrowseAsync(serviceType, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        return found.FirstOrDefault(i =>
            string.Equals(i.FullName, full, StringComparison.OrdinalIgnoreCase)
            || string.Equals(i.InstanceName, instanceName, StringComparison.OrdinalIgnoreCase));
    }

    public ValueTask DisposeAsync()
    {
        List<DnsQueryContext> registrations;
        List<BrowseSession> browses;
        lock (_gate)
        {
            registrations = _registrations.ToList();
            _registrations.Clear();
            browses = _browses.Values.ToList();
            _browses.Clear();
        }

        foreach (var context in registrations)
        {
            context.Native.Dispose();
        }

        foreach (var session in browses)
        {
            session.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private BrowseSession StartBrowse(string serviceType)
    {
        var session = new BrowseSession(serviceType);

        // browse needs a non-null cancel buffer (null returns ERROR_INVALID_PARAMETER) that stays valid
        // for as long as the query lives, i.e. until this provider is disposed.
        session.CancelBuffer = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(session.CancelBuffer, IntPtr.Zero);

        var callback = new BrowseCallback((status, context, record) =>
        {
            try
            {
                if (status != 0 || record == IntPtr.Zero)
                {
                    return;
                }

                var collected = DnsRecordReader.Read(record, serviceType);
                if (collected.Count == 0)
                {
                    return;
                }

                lock (session.Gate)
                {
                    foreach (var instance in collected)
                    {
                        session.Results[instance.FullName] = instance;
                    }
                }
            }
            catch (Exception ex)
            {
                // A throw out of an unmanaged callback would terminate the process.
                lock (_diagnostics)
                {
                    _diagnostics.Add(new ConfigDiagnostic(
                        ConfigDiagnosticSeverity.Warning,
                        ConfigDiagnosticCode.ReadError,
                        $"a browse callback result could not be read and was dropped: {ex.Message}",
                        "discovery.service_type"));
                }
            }
            finally
            {
                NativeMethods.DnsRecordListFree(record, DnsFreeRecordList);
            }
        });

        session.CallbackHandle = GCHandle.Alloc(callback);
        session.Callback = callback;

        var request = new DnsServiceBrowseRequest
        {
            Version = QueryRequestVersion1,
            InterfaceIndex = 0,
            QueryName = QualifiedServiceType(serviceType),
            BrowseCallback = callback,
            QueryContext = GCHandle.ToIntPtr(session.CallbackHandle),
        };

        var status = NativeMethods.DnsServiceBrowse(ref request, session.CancelBuffer);
        if (!IsSuccess(status))
        {
            session.Dispose();
            throw new InvalidOperationException($"DnsServiceBrowse returned DNS_STATUS {status} (0x{status:x8}) for '{request.QueryName}'");
        }

        return session;
    }

    private sealed record DnsQueryContext(string QualifiedName, string InstanceName, DnsServiceInstance Native);

    /// <summary>A long-lived browse: its callback, its rooted GCHandle, the cancel buffer DNSAPI needs to
    /// stay valid, and the results accumulated so far.</summary>
    private sealed class BrowseSession : IDisposable
    {
        public BrowseSession(string serviceType) => ServiceType = serviceType;

        public string ServiceType { get; }

        public Dictionary<string, ServiceInstance> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

        public object Gate { get; } = new();

        public GCHandle CallbackHandle { get; set; }

        public BrowseCallback? Callback { get; set; }

        public IntPtr CancelBuffer { get; set; }

        public void Dispose()
        {
            if (CallbackHandle.IsAllocated)
            {
                CallbackHandle.Free();
            }

            if (CancelBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(CancelBuffer);
                CancelBuffer = IntPtr.Zero;
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void BrowseCallback(int status, IntPtr queryContext, IntPtr record);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RegisterCompleteCallback(int status, IntPtr queryContext, IntPtr instance);

    /// <summary>
    /// windns.h: ULONG Version; ULONG InterfaceIndex; PDNS_SERVICE_INSTANCE pServiceInstance;
    /// PDNS_SERVICE_REGISTER_COMPLETE pRegisterCompletionCallback; PVOID pQueryContext;
    /// HANDLE hCredentials; BOOL unicastEnabled.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceRegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public RegisterCompleteCallback? RegisterCompletionRoutine;
        public IntPtr QueryContext;
        public IntPtr Credentials;
        [MarshalAs(UnmanagedType.Bool)]
        public bool UnicastEnabled;
    }

    /// <summary>
    /// windns.h: ULONG Version; ULONG InterfaceIndex; PCWSTR QueryName; union { pBrowseCallback;
    /// pBrowseCallbackV2; }; PVOID pQueryContext.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DnsServiceBrowseRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string QueryName;
        public BrowseCallback? BrowseCallback;
        public IntPtr QueryContext;
    }

    /// <summary>
    /// Turns the DNS_RECORD chain a browse callback delivers into <see cref="ServiceInstance"/>s. The
    /// chain is flat and interleaved - for one service you get a PTR naming the instance, an SRV naming
    /// the host and port, a TXT with the properties, and A/AAAA records named by host - so the reader
    /// does a single pass to bucket the records by owner name and then joins them.
    /// </summary>
    private static class DnsRecordReader
    {
        public static List<ServiceInstance> Read(IntPtr record, string serviceType)
        {
            var instanceNames = new List<string>();
            var srv = new Dictionary<string, (string Target, int Port)>(StringComparer.OrdinalIgnoreCase);
            var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var addresses = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            var guard = 0;
            for (var node = record; node != IntPtr.Zero && guard++ < 4096; node = Marshal.ReadIntPtr(node, RecordNextOffset))
            {
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(node, RecordNameOffset));
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var type = (ushort)Marshal.ReadInt16(node, RecordTypeOffset);
                var data = node + RecordDataOffset;

                switch (type)
                {
                    case TypePtr:
                    {
                        var target = Marshal.PtrToStringUni(Marshal.ReadIntPtr(data));
                        if (!string.IsNullOrEmpty(target))
                        {
                            instanceNames.Add(target!);
                        }

                        break;
                    }

                    case TypeSrv:
                    {
                        // DNS_SRV_DATAW is { PWSTR pNameTarget; WORD wPriority; WORD wWeight; WORD wPort;
                        // WORD Pad; } - the port is the THIRD WORD, i.e. at data+12, not data+8 (which is
                        // wPriority, normally 0).
                        var target = Marshal.PtrToStringUni(Marshal.ReadIntPtr(data));
                        var port = (ushort)Marshal.ReadInt16(data, 12);
                        srv[name] = (target ?? string.Empty, port);
                        break;
                    }

                    case TypeTxt:
                    {
                        var count = Marshal.ReadInt32(data);
                        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

                        // DNS_TXT_DATAW is { DWORD dwStringCount; PWSTR pStringArray[1]; } - the string
                        // array is INLINE (it is not a pointer to an array), so the first PWSTR lives at
                        // data+8 and the i-th at data+8+i*sizeof(PWSTR). Treating the value at data+8 as
                        // an array base dereferences a string pointer as a pointer and faults.
                        for (var i = 0; i < count && i < 128; i++)
                        {
                            var text = Marshal.PtrToStringUni(Marshal.ReadIntPtr(data, 8 + (i * IntPtr.Size)));
                            if (text is null)
                            {
                                continue;
                            }

                            var equals = text.IndexOf('=');
                            if (equals > 0)
                            {
                                entries[text[..equals]] = text[(equals + 1)..];
                            }
                        }

                        txt[name] = entries;
                        break;
                    }

                    case TypeA:
                    {
                        var value = Marshal.ReadInt32(data);
                        if (value != 0)
                        {
                            Add(addresses, name, new IPAddress(value).ToString());
                        }

                        break;
                    }

                    case TypeAaaa:
                    {
                        var bytes = new byte[16];
                        Marshal.Copy(data, bytes, 0, 16);
                        if (!bytes.All(b => b == 0))
                        {
                            Add(addresses, name, new IPAddress(bytes).ToString());
                        }

                        break;
                    }
                }
            }

            var results = new List<ServiceInstance>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pointer in instanceNames)
            {
                var instanceName = StripServiceSuffix(pointer, serviceType);
                var fullName = ServiceTypes.InstanceFullName(instanceName, serviceType);
                if (!seen.Add(fullName))
                {
                    continue;
                }

                var hasSrv = srv.TryGetValue(pointer, out var srvValue);
                var host = hasSrv && srvValue.Target.Length > 0 ? srvValue.Target : pointer;
                var port = hasSrv ? srvValue.Port : 0;
                var properties = txt.TryGetValue(pointer, out var txtValue)
                    ? txtValue
                    : new Dictionary<string, string>(StringComparer.Ordinal);
                var hostAddresses = addresses.TryGetValue(host, out var hostAddressList)
                    ? hostAddressList
                    : new List<string>();

                results.Add(new ServiceInstance(
                    instanceName,
                    host,
                    port,
                    ServiceTextRecord.Parse(properties),
                    serviceType,
                    hostAddresses,
                    DiscoveryOriginKind.Discovered));
            }

            return results;
        }

        private static void Add(Dictionary<string, List<string>> addresses, string host, string value)
        {
            if (!addresses.TryGetValue(host, out var list))
            {
                list = new List<string>();
                addresses[host] = list;
            }

            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(value);
            }
        }

        /// <summary>DNSAPI reports the fully qualified instance name; the rest of the code (and the
        /// in-memory provider) uses the bare instance label.</summary>
        private static string StripServiceSuffix(string instanceName, string serviceType)
        {
            foreach (var suffix in new[] { "." + QualifiedServiceType(serviceType), "." + serviceType })
            {
                if (instanceName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return instanceName[..^suffix.Length];
                }
            }

            return instanceName;
        }
    }

    /// <summary>
    /// Owns the native DNS_SERVICE_INSTANCE built by DnsServiceConstructInstance for registration. Field
    /// order follows the documented Windows struct: instance name, host name, IPv4/IPv6 address arrays,
    /// port, priority, weight, then parallel key/value arrays for the TXT record.
    /// </summary>
    private sealed class DnsServiceInstance : IDisposable
    {
        private DnsServiceInstance(IntPtr pointer) => Pointer = pointer;

        public IntPtr Pointer { get; private set; }

        public static DnsServiceInstance Create(ServiceInstance instance, string qualifiedName)
        {
            var keys = instance.Txt.Entries.Keys.ToArray();
            var values = instance.Txt.Entries.Values.ToArray();
            var keyPointers = keys.Select(Marshal.StringToHGlobalUni).ToArray();
            var valuePointers = values.Select(Marshal.StringToHGlobalUni).ToArray();
            var keyArray = Marshal.AllocHGlobal(IntPtr.Size * Math.Max(1, keyPointers.Length));
            var valueArray = Marshal.AllocHGlobal(IntPtr.Size * Math.Max(1, valuePointers.Length));
            try
            {
                for (var i = 0; i < keyPointers.Length; i++)
                {
                    Marshal.WriteIntPtr(keyArray, i * IntPtr.Size, keyPointers[i]);
                }

                for (var i = 0; i < valuePointers.Length; i++)
                {
                    Marshal.WriteIntPtr(valueArray, i * IntPtr.Size, valuePointers[i]);
                }

                // pIp4/pIp6 are left null: DNSAPI then advertises the addresses the host has on the
                // interfaces it picks, which is what "discover me on the LAN" means.
                var pointer = NativeMethods.DnsServiceConstructInstance(
                    qualifiedName,
                    QualifiedHostName(instance.HostName),
                    IntPtr.Zero,
                    IntPtr.Zero,
                    (ushort)instance.Port,
                    0,
                    0,
                    keyPointers.Length,
                    keyArray,
                    valueArray);

                if (pointer == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"DnsServiceConstructInstance failed for '{qualifiedName}'");
                }

                return new DnsServiceInstance(pointer);
            }
            finally
            {
                foreach (var key in keyPointers)
                {
                    Marshal.FreeHGlobal(key);
                }

                foreach (var value in valuePointers)
                {
                    Marshal.FreeHGlobal(value);
                }

                Marshal.FreeHGlobal(keyArray);
                Marshal.FreeHGlobal(valueArray);
            }
        }

        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                NativeMethods.DnsServiceFreeInstance(Pointer);
                Pointer = IntPtr.Zero;
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport(DnsApi, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int DnsServiceRegister(ref DnsServiceRegisterRequest request, IntPtr cancel);

        [DllImport(DnsApi, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int DnsServiceDeRegister(ref DnsServiceRegisterRequest request, IntPtr cancel);

        [DllImport(DnsApi, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int DnsServiceBrowse(ref DnsServiceBrowseRequest request, IntPtr cancel);

        [DllImport(DnsApi, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern IntPtr DnsServiceConstructInstance(
            [MarshalAs(UnmanagedType.LPWStr)] string instanceName,
            [MarshalAs(UnmanagedType.LPWStr)] string hostName,
            IntPtr ip4Address,
            IntPtr ip6Address,
            ushort port,
            ushort priority,
            ushort weight,
            int propertyCount,
            IntPtr keys,
            IntPtr values);

        [DllImport(DnsApi, CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern void DnsServiceFreeInstance(IntPtr instance);

        [DllImport(DnsApi, ExactSpelling = true)]
        internal static extern void DnsRecordListFree(IntPtr record, int freeType);
    }
}
