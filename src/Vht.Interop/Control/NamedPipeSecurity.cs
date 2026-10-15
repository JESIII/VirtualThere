using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Vht.Interop.Control;

/// <summary>
/// What the kernel reports about a control pipe's security, read back from the live object rather
/// than remembered from what was asked for.
/// </summary>
/// <remarks>
/// <see cref="Descriptor"/> and <see cref="IntegrityLabel"/> come from <c>GetSecurityInfo</c> on the
/// pipe handle, so a caller (and the acceptance suite) can assert the property the object really
/// carries. <see cref="Problem"/> is non-null when the pipe had to be created without the
/// descriptor it wanted, or when its mandatory label is not medium - the two states in which an
/// unelevated window cannot write to its own worker.
/// </remarks>
public sealed record PipeSecurityReport(string Descriptor, string IntegrityLabel, string? Problem)
{
    /// <summary>The mandatory label the object must carry for a medium-integrity client to write.</summary>
    public const string MediumIntegrityLabel = "S:(ML;;NW;;;ME)";

    /// <summary>The label text as the kernel reports it, i.e. inside any ACE flags it adds.</summary>
    public const string MediumIntegrityLabelAce = "(ML;;NW;;;ME)";

    /// <summary>True when the object carries the medium no-write-up mandatory label.</summary>
    public bool MediumIntegrityLabelPresent =>
        IntegrityLabel.Contains(MediumIntegrityLabelAce, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the DACL has an allow ACE for the interactive user.</summary>
    public bool InteractiveUserHasAnAce =>
        Descriptor.Contains(";;IU)", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Creates the control-plane named pipe that <b>both</b> halves of a role can use: the elevated
/// worker that owns it, and the unelevated window that drives it over JSON lines.
/// </summary>
/// <remarks>
/// <para>
/// A pipe created with no security descriptor inherits two things from the creating process that
/// break that pair as soon as the worker is elevated (docs/UI.md section 1):
/// </para>
/// <list type="bullet">
/// <item><description>
/// the <b>default DACL</b> of the token, which grants the operator's own account full control but
/// grants nothing at all to the interactive user as such, and
/// </description></item>
/// <item><description>
/// the <b>mandatory integrity label</b> of the token. A non-medium creator gets an explicit label
/// in the object's SACL - a High-integrity worker's pipe is labelled High - and no-write-up then
/// denies every write from the Medium-integrity window, DACL or no DACL. This is an access
/// <i>denial</i>, which is why it must not read as "the service is not running".
/// </description></item>
/// </list>
/// <para>
/// So the descriptor is explicit and the label is lowered to Medium:
/// <c>D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;&lt;owner&gt;)(A;;0x12019f;;;IU)S:(ML;;NW;;;ME)</c>.
/// The owner and the built-in Administrators/System keep full control; the interactive user gets
/// exactly read/write on the pipe (0x12019f = FILE_GENERIC_READ|FILE_GENERIC_WRITE), which is what
/// the control protocol needs and no more.
/// </para>
/// <para>
/// Nothing here may abort pipe setup. Writing a SACL at all requires <c>SeSecurityPrivilege</c>,
/// which an unelevated worker does not hold, so the descriptor is used when the kernel accepts it
/// and the pipe falls back to the plain constructor when it does not - an unelevated worker's
/// object is Medium anyway. Whatever happens, the label is read back afterwards and reported, and
/// the pipe keeps working.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class NamedPipeSecurity
{
    private const uint SDDL_REVISION_1 = 1;

    // PIPE_ACCESS_DUPLEX is PipeDirection.InOut; the PipeOptions bits are the same values as
    // FILE_FLAG_WRITE_THROUGH/FILE_FLAG_OVERLAPPED, so they can be OR'd straight into dwOpenMode.
    private const int PIPE_UNLIMITED_INSTANCES = 255;

    private const int OWNER_SECURITY_INFORMATION = 0x00000001;
    private const int GROUP_SECURITY_INFORMATION = 0x00000002;
    private const int DACL_SECURITY_INFORMATION = 0x00000004;
    private const int LABEL_SECURITY_INFORMATION = 0x00000010;

    private const int SE_KERNEL_OBJECT = 6;

    /// <summary>The access <see cref="ReadPipe"/> needs to ask an existing pipe for its descriptor.</summary>
    private const uint READ_CONTROL = 0x00020000;
    private const uint OPEN_EXISTING = 3;

    /// <summary>
    /// FILE_GENERIC_READ | FILE_GENERIC_WRITE, as an explicit mask: what a pipe client needs, and
    /// the tightest grant that still lets the window drive its worker.
    /// </summary>
    private const string PipeReadWriteMask = "0x12019f";

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateNamedPipe(
        string lpName,
        uint dwOpenMode,
        uint dwPipeMode,
        uint nMaxInstances,
        uint nOutBufferSize,
        uint nInBufferSize,
        uint nDefaultTimeOut,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
    private static partial nint LocalFree(nint hMem);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor, uint stringSdRevision, out nint securityDescriptor, out uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertSecurityDescriptorToStringSecurityDescriptorW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSecurityDescriptorToStringSecurityDescriptor(
        nint securityDescriptor, uint stringSdRevision, int securityInformation,
        out nint stringSecurityDescriptor, out uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityInfo", SetLastError = true)]
    private static partial int GetSecurityInfo(
        nint handle, int objectType, int securityInformation,
        out nint owner, out nint group, out nint dacl, out nint sacl, out nint securityDescriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "SetSecurityInfo", SetLastError = true)]
    private static partial int SetSecurityInfo(
        nint handle, int objectType, int securityInformation,
        nint owner, nint group, nint dacl, nint sacl);

    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorSacl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorSacl(
        nint securityDescriptor, out int saclPresent, out nint sacl, out int saclDefaulted);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    /// <summary>
    /// The security descriptor every VirtualThere control pipe is created with. Public so a
    /// diagnostic and the acceptance suite can state what was asked for next to what the kernel
    /// reports.
    /// </summary>
    public static string PipeDescriptorSddl()
    {
        string owner = CurrentUserSid().Value;

        return $"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{owner})(A;;{PipeReadWriteMask};;;IU)" +
            PipeSecurityReport.MediumIntegrityLabel;
    }

    /// <summary>
    /// Creates the pipe with the explicit descriptor, reporting what the object ended up carrying.
    /// The control plane's own handle has the access <see cref="GetSecurityInfo"/> needs, so the
    /// report is read from the object the server will actually serve on.
    /// </summary>
    internal static NamedPipeServerStream CreateServerPipe(
        string pipeName,
        PipeDirection direction,
        int maxNumberOfServerInstances,
        PipeTransmissionMode transmissionMode,
        PipeOptions options,
        out PipeSecurityReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        string sddl = PipeDescriptorSddl();
        string? problem = null;
        NamedPipeServerStream? pipe = null;

        if (TryCreateHandle(pipeName, direction, maxNumberOfServerInstances, transmissionMode, options, sddl,
                out nint handle, out string? whyNot))
        {
            pipe = new NamedPipeServerStream(
                direction,
                isAsync: (options & PipeOptions.Asynchronous) != 0,
                isConnected: false,
                safePipeHandle: new Microsoft.Win32.SafeHandles.SafePipeHandle(handle, ownsHandle: true));
        }
        else
        {
            problem = whyNot;

            // The descriptor or the SACL was refused (an unelevated worker holds neither
            // SeSecurityPrivilege nor a reason to need either). Fall back to the plain constructor:
            // an unelevated worker's object is Medium by default, so the pair still works.
            pipe = new NamedPipeServerStream(
                pipeName,
                direction,
                maxNumberOfServerInstances,
                transmissionMode,
                options);
        }

        (string descriptor, string label, string? labelProblem) = ReadBack(pipe.SafePipeHandle.DangerousGetHandle());

        if (labelProblem is not null && problem is null)
            problem = labelProblem;

        report = new PipeSecurityReport(descriptor, label, problem);
        return pipe;
    }

    private static bool TryCreateHandle(
        string pipeName,
        PipeDirection direction,
        int maxNumberOfServerInstances,
        PipeTransmissionMode transmissionMode,
        PipeOptions options,
        string sddl,
        out nint handle,
        out string? problem)
    {
        handle = 0;
        problem = null;

        if (transmissionMode != PipeTransmissionMode.Byte)
        {
            // The managed pipe stream only supports byte mode; message mode would be a wire change.
            problem = $"the pipe transmission mode must be Byte, not {transmissionMode}";
            return false;
        }

        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, SDDL_REVISION_1, out nint sd, out _))
        {
            problem = $"the security descriptor '{sddl}' was refused: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return false;
        }

        var attributes = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = sd,
            bInheritHandle = 0,
        };

        try
        {
            uint openMode = (uint)direction | (uint)options;
            int instances = maxNumberOfServerInstances < 1
                ? PIPE_UNLIMITED_INSTANCES
                : maxNumberOfServerInstances;

            // PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT is all zeroes: the default the
            // managed constructor uses, which is the shape the JSON-lines protocol needs.
            nint created = CreateNamedPipe(
                $@"\\.\pipe\{pipeName}", openMode, 0, (uint)instances, 0, 0, 0, ref attributes);

            if (created == 0 || created == -1)
            {
                problem = $"the pipe could not be created with its security descriptor: " +
                    $"{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                return false;
            }

            handle = created;
            return true;
        }
        finally
        {
            LocalFree(sd);
        }
    }

    /// <summary>
    /// Reads the object's owner, group, DACL and mandatory label back out of the kernel. The full
    /// SACL is deliberately not requested: that needs SeSecurityPrivilege, while
    /// <c>LABEL_SECURITY_INFORMATION</c> - the only part of it anything here cares about - is
    /// readable by the owner without any privilege.
    /// </summary>
    private static (string Descriptor, string Label, string? Problem) ReadBack(nint handle)
    {
        string descriptor = string.Empty;
        string label = string.Empty;
        string? problem = null;

        int parts = OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION
            | DACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION;

        int rc = GetSecurityInfo(handle, SE_KERNEL_OBJECT, parts, out _, out _, out _, out _, out nint sd);
        if (rc == 0)
        {
            descriptor = ToSddl(sd, parts);
            Free(sd);
        }
        else
        {
            problem = $"the pipe's security descriptor could not be read back: {new Win32Exception(rc).Message}";
        }

        int labelRc = GetSecurityInfo(handle, SE_KERNEL_OBJECT, LABEL_SECURITY_INFORMATION,
            out _, out _, out _, out _, out nint labelSd);
        if (labelRc == 0)
        {
            label = ToSddl(labelSd, LABEL_SECURITY_INFORMATION);
            Free(labelSd);
        }
        else
        {
            problem ??= $"the pipe's integrity label could not be read back: {new Win32Exception(labelRc).Message}";
        }

        if (label.Contains(PipeSecurityReport.MediumIntegrityLabelAce, StringComparison.OrdinalIgnoreCase))
            return (descriptor, label, problem);

        // The label the descriptor asked for did not stick. Apply it to the live object instead,
        // which is the other documented route to the same ACE, and say so if that fails too.
        string? fixup = ApplyMediumLabel(handle);
        if (fixup is null)
        {
            labelRc = GetSecurityInfo(handle, SE_KERNEL_OBJECT, LABEL_SECURITY_INFORMATION,
                out _, out _, out _, out _, out nint after);
            if (labelRc == 0)
            {
                label = ToSddl(after, LABEL_SECURITY_INFORMATION);
                Free(after);
            }
        }
        else
        {
            problem ??= fixup;
        }

        if (!label.Contains(PipeSecurityReport.MediumIntegrityLabelAce, StringComparison.OrdinalIgnoreCase))
        {
            problem ??= string.IsNullOrEmpty(label)
                ? "the pipe carries no mandatory integrity label, so it inherits the process's " +
                  "integrity level: an elevated worker's pipe would be High and refuse the window's writes"
                : $"the pipe's mandatory integrity label is '{label}', not Medium";
        }

        return (descriptor, label, problem);
    }

    /// <summary>
    /// Sets the pipe's mandatory label to Medium on the live object. Returns null on success, or
    /// the reason it could not be applied.
    /// </summary>
    private static string? ApplyMediumLabel(nint handle)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                PipeSecurityReport.MediumIntegrityLabel, SDDL_REVISION_1, out nint sd, out _))
        {
            return $"the Medium integrity label descriptor was refused: " +
                $"{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
        }

        try
        {
            if (!GetSecurityDescriptorSacl(sd, out _, out nint sacl, out _))
            {
                return $"the Medium integrity label descriptor has no SACL: " +
                    $"{new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            }

            int rc = SetSecurityInfo(handle, SE_KERNEL_OBJECT, LABEL_SECURITY_INFORMATION,
                0, 0, 0, sacl);

            return rc == 0
                ? null
                : $"the Medium integrity label could not be applied to the pipe: {new Win32Exception(rc).Message}";
        }
        finally
        {
            LocalFree(sd);
        }
    }

    private static string ToSddl(nint securityDescriptor, int parts) =>
        ConvertSecurityDescriptorToStringSecurityDescriptor(securityDescriptor, SDDL_REVISION_1, parts,
            out nint text, out _)
            ? Marshal.PtrToStringUni(text) ?? string.Empty
            : string.Empty;

    private static void Free(nint pointer)
    {
        if (pointer != 0)
            LocalFree(pointer);
    }

    /// <summary>
    /// Opens <c>\\.\pipe\&lt;pipeName&gt;</c> for READ_CONTROL and reports what the object carries -
    /// the independent read of a live pipe, which is what a test (or an operator with a debugger)
    /// uses to check a *running* worker rather than the code that created it.
    /// </summary>
    /// <remarks>
    /// The server itself cannot do this: opening its own pipe by name would consume one of its
    /// instances. It reads its descriptor off the handle it already holds instead.
    /// </remarks>
    internal static PipeSecurityReport ReadPipe(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        nint handle = CreateFile($@"\\.\pipe\{pipeName}", READ_CONTROL, 0, 0, OPEN_EXISTING, 0, 0);
        if (handle == -1)
        {
            int error = Marshal.GetLastWin32Error();
            string reason = $"the pipe could not be opened for READ_CONTROL: {new Win32Exception(error).Message}";
            return new PipeSecurityReport(string.Empty, string.Empty, reason);
        }

        try
        {
            (string descriptor, string label, string? problem) = ReadBack(handle);
            return new PipeSecurityReport(descriptor, label, problem);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>The rights field of an allow ACE for <paramref name="sid"/> in an SDDL descriptor.</summary>
    internal static string RightsOfAce(string descriptor, string sid)
    {
        foreach (string ace in descriptor.Split('('))
        {
            // The ACE ends at ')'; what follows may be another ACE or the label's own "S:AI(".
            string[] fields = ace.Split(')')[0].Split(';');
            if (fields.Length < 6 || !fields[0].StartsWith('A'))
            {
                continue;
            }

            if (fields[5].Equals(sid, StringComparison.OrdinalIgnoreCase))
            {
                return fields[2];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// An SDDL access mask as an access mask. An unrecognised spelling throws rather than silently
    /// passing a test: SDDL can render one grant in several ways, and a new spelling has to be
    /// taught here instead of being ignored.
    /// </summary>
    internal static uint RightsMask(string rights) => rights.ToUpperInvariant() switch
    {
        "FA" or "GA" => 0x001F01FF,   // FILE_ALL_ACCESS
        "FR" or "GR" => 0x00120089,   // FILE_GENERIC_READ
        "FW" or "GW" => 0x00120116,   // FILE_GENERIC_WRITE
        _ => rights.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.Parse(rights[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"unrecognised SDDL access mask '{rights}'"),
    };

    /// <summary>FILE_GENERIC_WRITE: what "this client may write to the pipe" means at mask level.</summary>
    internal const uint FileGenericWrite = 0x00120116;

    private static SecurityIdentifier CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();

        // The interactive user is the account that started the worker; if the identity has no SID
        // (impossible for a normal logon, but this must not throw during pipe setup) fall back to
        // the INTERACTIVE well-known group, which is what the ACE list grants anyway.
        return identity.User ?? new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
    }
}
