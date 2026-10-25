using Vht.Client;

namespace Vht.Client.Tests;

public sealed class WorkerInvocationTests
{
    [Fact]
    public void Client_worker_runs_the_service_and_keeps_a_runtime_log()
    {
        string[] arguments = Program.WorkerArguments(["--worker", "--pipe", "vht-client-test"]);

        Assert.Equal(["serve", "--pipe", "vht-client-test", "--log"], arguments);
    }
}
