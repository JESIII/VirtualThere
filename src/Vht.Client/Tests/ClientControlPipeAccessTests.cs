using System.IO.Pipes;
using Vht.Client.Control;
using Vht.Interop.Control;
using Vht.Ui.ControlPlane;

namespace Vht.Client.Tests;

/// <summary>
/// The client role's control pipe is the same object as the server's, created the same way, and it
/// failed the same way: an elevated <c>vht-client.exe --worker</c> owns it and the unelevated
/// <c>vht-client.exe</c> window drives it, while a pipe with no security descriptor carries the
/// creating token's default DACL (in which an elevated token's owner is the Administrators group,
/// not the operator) and no mandatory integrity label of its own. The DACL is what refused the
/// window here; the explicit Medium label is a requirement of this code (an object that carries a
/// label is subject to no-write-up, which is also what keeps a Low-integrity client out).
/// </summary>
/// <remarks>
/// <para>
/// The assertions here are symmetric with the server's
/// (<c>tests/Vht.Acceptance.Tests/ControlPipeAccessTests</c>): the live pipe is opened <b>by name</b>
/// and the kernel is asked what the object carries, rather than the test trusting anything the code
/// under test reports about itself. The DACL must have an allow ACE for the interactive user that
/// carries write access, the mandatory label must be Medium with no-write-up, the pipe must open for
/// writing, and the window's own client must get a real answer through it.
/// </para>
/// <para>
/// The elevated half of the story still cannot be asserted from a test run: the test process is
/// Medium and cannot raise itself to High without an interactive consent prompt. What is asserted is
/// the property that makes the elevated case work - that an object created here does carry the
/// explicit descriptor and the Medium label at all, which the previous implementation did not.
/// </para>
/// </remarks>
public sealed class ClientControlPipeAccessTests
{
    [Fact]
    public async Task TheClientControlPipeIsWritableByTheInteractiveUserAndCarriesAMediumIntegrityLabel()
    {
        string pipeName = "vht-client-test-" + Guid.NewGuid().ToString("N");

        await using var fixture = new ClientTestFixture();
        await using var control = new ClientControlServer(
            fixture.Peers, fixture.Catalog, fixture.Manager, fixture.Policy, fixture.Credentials, fixture.Log,
            pipeName);

        control.Start();

        PipeSecurityReport report = await WaitForPipeAsync(pipeName);

        Assert.True(
            report.MediumIntegrityLabelPresent,
            $"the client control pipe's mandatory integrity label is '{report.IntegrityLabel}', not Medium " +
            "with no-write-up; an object that carries a High label would then refuse the window every write");

        string rights = NamedPipeSecurity.RightsOfAce(report.Descriptor, "IU");
        Assert.True(
            rights.Length > 0,
            $"the client control pipe's DACL has no ACE for the interactive user (IU): {report.Descriptor}");

        uint mask = NamedPipeSecurity.RightsMask(rights);
        Assert.True(
            (mask & NamedPipeSecurity.FileGenericWrite) == NamedPipeSecurity.FileGenericWrite,
            $"the interactive user's ACE grants '{rights}' on the client control pipe, which has no write access in it");

        // Same property behaviourally, then through the client the window actually uses.
        await ConnectForWriteAsync(pipeName);

        ControlPlaneResponse response = await UiStatusWithRetryAsync(pipeName);
        Assert.True(
            response.IsOk,
            "the window's client did not reach the client worker: " + (response.Error?.Message ?? response.Status.ToString()));
    }

    private static async Task<PipeSecurityReport> WaitForPipeAsync(string pipeName)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            PipeSecurityReport report = NamedPipeSecurity.ReadPipe(pipeName);
            if (report.Descriptor.Length > 0)
            {
                return report;
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
            $"the client control pipe \\\\.\\pipe\\{pipeName} could not be opened for write within 5 s", last);
    }

    private static async Task<ControlPlaneResponse> UiStatusWithRetryAsync(string pipeName)
    {
        ControlPlaneResponse last = ControlPlaneResponse.Unavailable("peers.list", "no attempt was made");

        for (int attempt = 0; attempt < 50; attempt++)
        {
            var client = new ControlPlaneClient(pipeName, TimeSpan.FromSeconds(2));
            last = await client.SendAsync("peers.list");
            if (last.IsOk)
            {
                return last;
            }

            await Task.Delay(100);
        }

        return last;
    }
}
