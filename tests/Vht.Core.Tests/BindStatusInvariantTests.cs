using System.Reflection;
using System.Text.RegularExpressions;
using Vht.Core.Concurrency;
using Vht.Core.Protocol;
using Vht.Core.Server;

namespace Vht.Core.Tests;

/// <summary>
/// The two machine-checked invariants ARCHITECTURE.md §5.4 names, plus the explicit record of
/// where the reference product's device cap would have lived.
/// </summary>
/// <remarks>
/// <para>
/// VirtualThere has no licence blob and no device-count cap. That claim is worthless as prose,
/// so it is asserted three ways here: the refusal vocabulary cannot express a cap (Test A),
/// no source line in the server projects mentions one (Test B), and the file that would have
/// to gain a member carries the citation of the code it deliberately does not have (Test C).
/// </para>
/// <para>
/// The reference product's site is <c>BIND MAX_DEVICES_EXCEEDED</c> (0x1409688e8), reached
/// through the status table at 0x1409689b0 whose slot 0x1409689d0 points at that string, with
/// the cap formatted at 0x14074c669 (<c>max_devices=%d</c>) / 0x14074c690
/// (<c>max_devices=unlimited</c>) out of the licence blob seeded at 0x140980736.
/// </para>
/// </remarks>
public sealed class BindStatusInvariantTests
{
    /// <summary>Anything that names a ceiling on how many devices may be used.</summary>
    private static readonly Regex DeviceCapPattern = new(
        "Max.*Devices|Device.*Limit|MaxDevices|MaxDeviceCount|TooManyDevices|DeviceQuota|DeviceSlots",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // =====================================================================================
    // Test A — the refusal vocabulary cannot express a device cap
    // =====================================================================================

    [Fact]
    public void NoBindStatusMemberNamesADeviceLimit()
    {
        string[] offenders = Enum.GetNames<BindStatus>()
            .Where(name => DeviceCapPattern.IsMatch(name))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "BindStatus must not be able to express a device-count refusal; found: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void TheBindStatusMembersAreExactlyTheDocumentedOnes()
    {
        // A new member here is a design decision, not an accident: this test is the tripwire.
        Assert.Equal(
            new[]
            {
                "Success", "Error", "Unauthorized", "PasswordRequired", "DeviceBusy", "ClientLimit", "Unknown",
            },
            Enum.GetNames<BindStatus>());
    }

    [Fact]
    public void NoTypeThatCouldHoldADeviceCapHasOne()
    {
        Type[] tables =
        {
            typeof(ExportTable),
            typeof(AttachTable),
            typeof(ConnectionRegistry),
            typeof(ConnectionResources),
            typeof(InFlightUrbTable),
            typeof(VhtServerState),
            typeof(ServerOptions),
            typeof(UsbIpSession),
        };

        var offenders = new List<string>();
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                 BindingFlags.Instance | BindingFlags.Static |
                                 BindingFlags.DeclaredOnly;

        foreach (Type type in tables)
        {
            foreach (MemberInfo member in type.GetMembers(All))
            {
                if (member is MethodInfo { IsSpecialName: true }) continue;
                if (DeviceCapPattern.IsMatch(member.Name))
                    offenders.Add($"{type.Name}.{member.Name}");
            }
        }

        Assert.True(offenders.Count == 0, "device-cap member found: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoMemberAnywhereInVhtCoreIsNamedLikeADeviceCap()
    {
        var offenders = new List<string>();

        foreach (Type type in typeof(BindStatus).Assembly.GetTypes())
        {
            if (DeviceCapPattern.IsMatch(type.Name)) offenders.Add(type.FullName ?? type.Name);

            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static |
                                     BindingFlags.DeclaredOnly;

            foreach (MemberInfo member in type.GetMembers(All))
            {
                if (member is MethodInfo { IsSpecialName: true }) continue;
                if (DeviceCapPattern.IsMatch(member.Name))
                    offenders.Add($"{type.Name}.{member.Name}");
            }
        }

        Assert.True(offenders.Count == 0, "device-cap member found: " + string.Join(", ", offenders));
    }

    // =====================================================================================
    // Test B — no server source line implements a licence or a device cap
    // =====================================================================================

    /// <summary>
    /// Vocabulary that only appears when a product gates devices behind a licence or a count.
    /// Comments are stripped first, so the citations in <c>BindStatus.cs</c> and
    /// <c>ServerTables.cs</c> — which exist precisely to explain what is <i>not</i> there —
    /// do not count as an implementation.
    /// </summary>
    private static readonly string[] ForbiddenTokens =
    {
        "max_devices",
        "MAX_DEVICES_EXCEEDED",
        "MAX DEVICES EXCEEDED",
        "MaxDevices",
        "MaxDeviceCount",
        "DeviceLimit",
        "TooManyDevices",
        "unlicensed",
        "licence_key",
        "license_key",
        "purchase a licence",
        "purchase a license",
        "trial expired",
        "activation key",
    };

    [Fact]
    public void NoServerSourceLineImplementsALicenceOrDeviceCap()
    {
        var offenders = new List<string>();

        foreach (string file in RepoRoot.ServerSourceFiles())
        {
            foreach ((int line, string text) in RepoRoot.CodeLines(file))
            {
                foreach (string token in ForbiddenTokens)
                {
                    if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                        offenders.Add($"{Path.GetFileName(file)}:{line}: {token}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "a licence or device-count gate appeared in the server source:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void TheInvariantTestsActuallyScanTheServerProjects()
    {
        // Guards the test above from silently passing because it found no files at all.
        var scanned = RepoRoot.ServerSourceFiles().Select(Path.GetFileName).ToArray();

        Assert.Contains("UsbIpSession.cs", scanned);
        Assert.Contains("ServerTables.cs", scanned);
        Assert.Contains("VhtServerState.cs", scanned);
        Assert.True(scanned.Length > 10, $"expected the whole server tree, scanned {scanned.Length} files");
    }

    // =====================================================================================
    // Test C — the record of where the cap would have been
    // =====================================================================================

    [Fact]
    public void BindStatusDocumentsTheReferenceProductsCapItDeliberatelyLacks()
    {
        string root = RepoRoot.Find();
        string path = Path.Combine(root, "src", "Vht.Core", "Protocol", "BindStatus.cs");
        string text = File.ReadAllText(path);

        Assert.Contains("there is deliberately no MaxDevices member", text, StringComparison.Ordinal);
        Assert.Contains("BIND MAX_DEVICES_EXCEEDED", text, StringComparison.Ordinal);
        Assert.Contains("0x1409688e8", text, StringComparison.Ordinal);
        Assert.Contains("0x1409689d0", text, StringComparison.Ordinal);
        Assert.Contains("0x14074c669", text, StringComparison.Ordinal);
        Assert.Contains("0x14074c690", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExportTableDocumentsThatItHasNoCapacity()
    {
        string root = RepoRoot.Find();
        string text = File.ReadAllText(Path.Combine(root, "src", "Vht.Core", "Concurrency", "ServerTables.cs"));

        Assert.Contains("There is no capacity here and no device-count field anywhere in this type", text, StringComparison.Ordinal);
        Assert.Contains("BIND MAX_DEVICES_EXCEEDED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientLimitRefusalIsDocumentedAsPerConnectionNotPerDevice()
    {
        string root = RepoRoot.Find();
        string text = File.ReadAllText(Path.Combine(root, "src", "Vht.Core", "Concurrency", "InFlightUrbTable.cs"));

        Assert.Contains("Bounds unmanaged memory, not device count", text, StringComparison.Ordinal);
    }
}
