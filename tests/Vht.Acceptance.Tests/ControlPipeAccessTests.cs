using System.IO.Pipes;
using System.Text.Json.Nodes;
using Vht.Acceptance;
using Vht.Core.Logging;
using Vht.Core.Model;
using Vht.Core.Server;
using Vht.Interop.Control;
using Vht.Server.Control;
using Vht.Ui.ControlPlane;

namespace Vht.Acceptance.Tests;

/// <summary>
/// The control pipe's accessibility to the process that does <b>not</b> own it.
/// </summary>
/// <remarks>
/// <para>
/// The reported failure was an elevated worker whose pipe refused the unelevated window: the pipe
/// was created with no security descriptor at all, so it inherited the owner's default DACL and -
/// fatally - the owner's mandatory integrity label. A High-integrity worker's pipe is labelled High,
/// and no-write-up then denies every write from the Medium-integrity window; the window reported
/// that as "no listener ... is the service running?".
/// </para>
/// <para>
/// This test asserts the object's real state by opening the live pipe <b>by name</b> and asking the
/// kernel for its security descriptor - not by trusting a report the server keeps about itself, and
/// not by asserting that some helper was called. Two properties are checked, and the old code fails
/// both: the DACL must contain an allow ACE for the interactive user (the old pipe carried the
/// process default, which grants the interactive user nothing of its own and grants Everyone and
/// ANONYMOUS read only), and the mandatory label must be Medium with no-write-up (the old pipe
/// carried no label ACE at all, which is precisely how the elevated case ends up High).
/// </para>
/// <para>
/// <b>What this cannot assert.</b> The test process is Medium, and a test run cannot elevate itself
/// (UAC consent is interactive), so it cannot create the High-integrity pipe that reproduced the bug
/// and cannot watch the fixed pipe deflect a real High-label denial. That half is reported NOT RUN
/// and has to be confirmed by starting the worker elevated; what is asserted here is the property
/// that makes it work, read from the live object.
/// </para>
/// </remarks>
public sealed class ControlPipeAccessTests
{
    [Fact]
    public async Task TheControlPipeIsWritableByTheInteractiveUserAndCarriesAMediumIntegrityLabel()
    {
        string pipeName = "vht-accept-pipe-" + Guid.NewGuid().ToString("N");

        var options = new ServerOptions
        {
            ServerName = "vht-accept",
            ControlPlanePipeName = pipeName,
            LogLevel = LogLevel.Detail,
        };

        var log = new AcceptanceRig.CapturingLogger();
        var capture = new SyntheticCaptureProvider(Array.Empty<UsbDevice>(), new SyntheticDeviceOptions());
        var state = new VhtServerState(options, capture, log);

        await using var control = new ControlPlaneServer(state, ring: null, stopServer: null);
        control.Start();

        (string descriptor, string label) = await WaitForPipeAsync(pipeName);

        // 1. The mandatory label. Without it the unelevated window is denied by no-write-up whenever
        //    the worker is elevated, whatever the DACL says.
        Assert.True(
            label.Contains("(ML;;NW;;;ME)", StringComparison.OrdinalIgnoreCase),
            $"the control pipe's mandatory integrity label is '{label}', not Medium with no-write-up; " +
            "an elevated worker's pipe would then refuse the window every write");

        // 2. The DACL: an allow ACE for the interactive user that really carries write access.
        string rights = NamedPipeSecurity.RightsOfAce(descriptor, "IU");
        Assert.True(
            rights.Length > 0,
            $"the control pipe's DACL has no ACE for the interactive user (IU): {descriptor}");

        uint mask = NamedPipeSecurity.RightsMask(rights);
        Assert.True(
            (mask & NamedPipeSecurity.FileGenericWrite) == NamedPipeSecurity.FileGenericWrite,
            $"the interactive user's ACE grants '{rights}' on the control pipe, which has no write access in it");

        // 3. The same property, stated behaviourally: this (Medium) process can open the pipe for
        //    writing, which is what sending a request line amounts to.
        await ConnectForWriteAsync(pipeName);

        // 4. Both real clients reach the service over it: the CLI's (vht status --pipe ...) and the
        //    one the window uses.
        using (var client = new ControlClient(pipeName, TimeSpan.FromSeconds(5)))
        {
            JsonObject status = await StatusWithRetryAsync(client, pipeName);
            Assert.True(status["ok"]?.GetValue<bool>() == true, "the CLI client got a refusal instead of a status");
            Assert.Equal("vht-accept", status["server_name"]?.GetValue<string>());
        }

        ControlPlaneResponse ui = await UiStatusWithRetryAsync(pipeName);
        Assert.True(
            ui.IsOk,
            "the window's client did not reach the service: " + (ui.Error?.Message ?? ui.Status.ToString()));
    }

    /// <summary>
    /// Polls the pipe by name until the server's accept loop has published an instance. Every
    /// successful open consumes one, which is why the clients below retry: the loop publishes the
    /// next instance as soon as a connection ends.
    /// </summary>
    private static async Task<(string Descriptor, string Label)> WaitForPipeAsync(string pipeName)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            PipeSecurityReport report = NamedPipeSecurity.ReadPipe(pipeName);
            if (report.Descriptor.Length > 0)
            {
                return (report.Descriptor, report.IntegrityLabel);
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException($"no control pipe named '{pipeName}' appeared within 5 s");
    }

    private static async Task ConnectForWriteAsync(string pipeName)
    {
        Exception? last = null;

        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var writeOnly = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                writeOnly.Connect(1000);
                return;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                last = ex;
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException(
            $"the control pipe \\\\.\\pipe\\{pipeName} could not be opened for write within 5 s", last);
    }

    private static async Task<JsonObject> StatusWithRetryAsync(ControlClient client, string pipeName)
    {
        ControlPlaneException? last = null;

        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                return client.Status();
            }
            catch (ControlPlaneException ex)
            {
                last = ex;
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException($"no answer from \\\\.\\pipe\\{pipeName} within 5 s", last);
    }

    private static async Task<ControlPlaneResponse> UiStatusWithRetryAsync(string pipeName)
    {
        ControlPlaneResponse last = ControlPlaneResponse.Unavailable("server.status", "no attempt was made");

        for (int attempt = 0; attempt < 50; attempt++)
        {
            var client = new ControlPlaneClient(pipeName, TimeSpan.FromSeconds(2));
            last = await client.SendAsync("server.status");
            if (last.IsOk)
            {
                return last;
            }

            await Task.Delay(100);
        }

        return last;
    }
}
