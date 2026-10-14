using Vht.Core.Model;

namespace Vht.Acceptance.Tests;

public sealed class DiagnoseTls
{
    [Fact]
    public async Task Dump_Server_Log_On_Tls_Connect()
    {
        var devices = new[] { SyntheticDevices.Build(SyntheticDevices.All[0], 0) };
        await using AcceptanceRig rig = await AcceptanceRig.StartSyntheticAsync(
            devices, new SyntheticDeviceOptions { Compressible = false },
            Vht.Transport.TransportKind.Tls, policy: null, CancellationToken.None);

        string outcome;
        try
        {
            await using UsbIpClient client = await UsbIpClient.ConnectAsync(
                "127.0.0.1", rig.Port,
                AcceptanceRig.ClientTransportOptions(Vht.Transport.TransportKind.Tls), CancellationToken.None);
            outcome = "CONNECTED";
        }
        catch (Exception ex)
        {
            outcome = $"{ex.GetType().Name}: {ex.Message} | inner={ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}";
        }

        string log = rig.Log is AcceptanceRig.CapturingLogger cap
            ? string.Join("\n", cap.Snapshot())
            : "(no lines)";
        Assert.Fail($"outcome={outcome}\nport={rig.Port}\nlog:\n{log}");
    }
}
