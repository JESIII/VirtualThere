namespace Vht.Core.Hooks;

/// <summary>Result of launching a hook script.</summary>
public readonly struct ProcessOutcome
{
    public bool Started { get; init; }
    public int? ExitCode { get; init; }
    public string StandardOutput { get; init; }
    public string StandardError { get; init; }
    public bool TimedOut { get; init; }
    public string FailureDetail { get; init; }

    public string CombinedOutput
    {
        get
        {
            string so = StandardOutput?.Trim() ?? string.Empty;
            string se = StandardError?.Trim() ?? string.Empty;
            if (so.Length == 0) return se;
            if (se.Length == 0) return so;
            return so + " | " + se;
        }
    }
}

/// <summary>
/// Launches a hook script. Abstracted so the hook ABI, the substitution rules, the timeout
/// behaviour and the refusal paths are unit-testable without spawning processes on the
/// machine running the tests.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="commandLine"/> through the shell, killing it if it exceeds
    /// <paramref name="timeout"/>.
    /// </summary>
    Task<ProcessOutcome> RunAsync(string commandLine, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// The production runner: <c>cmd.exe /C &lt;script&gt;</c>, exactly the invocation the
/// reference product uses (RECON_AND_SPEC.md §1.2), with a hard timeout that kills the
/// process tree.
/// </summary>
public sealed class ShellProcessRunner : IProcessRunner
{
    private readonly string _comspec;
    private readonly int _outputCap;

    public ShellProcessRunner(string? comspec = null, int outputCap = 64 * 1024)
    {
        _comspec = comspec ?? "cmd.exe";
        _outputCap = outputCap;
    }

    public async Task<ProcessOutcome> RunAsync(string commandLine, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = _comspec,
            Arguments = "/C " + commandLine,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return new ProcessOutcome { Started = false, FailureDetail = "process did not start" };
        }
        catch (Exception ex)
        {
            return new ProcessOutcome { Started = false, FailureDetail = ex.Message };
        }

        Task<string> stdout = ReadCappedAsync(process.StandardOutput, _outputCap);
        Task<string> stderr = ReadCappedAsync(process.StandardError, _outputCap);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return new ProcessOutcome
            {
                Started = true,
                TimedOut = true,
                FailureDetail = $"hook exceeded the {timeout.TotalSeconds:0.#}s timeout and was killed",
                StandardOutput = await Safe(stdout).ConfigureAwait(false),
                StandardError = await Safe(stderr).ConfigureAwait(false),
            };
        }

        string so = await Safe(stdout).ConfigureAwait(false);
        string se = await Safe(stderr).ConfigureAwait(false);

        return new ProcessOutcome
        {
            Started = true,
            ExitCode = process.ExitCode,
            StandardOutput = so,
            StandardError = se,
        };
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // A hook that outlives the kill attempt is logged by the caller; nothing to do here.
        }
    }

    private static async Task<string> Safe(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, int cap)
    {
        var buffer = new char[4096];
        var builder = new System.Text.StringBuilder();
        int n;
        while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (builder.Length < cap)
                builder.Append(buffer, 0, Math.Min(n, cap - builder.Length));
        }
        return builder.ToString();
    }
}
