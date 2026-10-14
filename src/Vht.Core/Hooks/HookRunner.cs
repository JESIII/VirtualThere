using Vht.Core.Logging;

namespace Vht.Core.Hooks;

/// <summary>Hook configuration: which event runs which script, plus the safety knobs.</summary>
public sealed class HookOptions
{
    /// <summary>Per-hook timeout. A hook that exceeds it is killed (ARCHITECTURE.md risk 9).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// When non-empty, only scripts under one of these directories may be executed. An empty
    /// list means "any path", which is the permissive default an operator opts into by
    /// leaving it empty; the docs warn about it.
    /// </summary>
    public List<string> AllowedScriptRoots { get; } = new();

    /// <summary>Script per hook event, keyed by the vocabulary name.</summary>
    public Dictionary<string, string> Scripts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sets the script for an event.</summary>
    public HookOptions Set(HookEvent hook, string scriptPath)
    {
        Scripts[hook.Name()] = scriptPath;
        return this;
    }

    /// <summary>Returns the configured script for an event, or <c>null</c>.</summary>
    public string? ScriptFor(HookEvent hook) =>
        Scripts.TryGetValue(hook.Name(), out string? s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}

/// <summary>
/// Runs hook scripts with the exit-code ABI the reference product uses:
/// exit <c>1</c> = yes/allow, exit <c>0</c> = no, anything else is logged as unknown and the
/// event is treated as allowed (a broken hook must not silently lock the operator out of
/// their own devices).
/// </summary>
/// <remarks>
/// Hardened relative to the reference behaviour, which is a bare
/// <c>cmd.exe /C &lt;script&gt;</c> with no timeout (ARCHITECTURE.md risk 9):
/// <list type="bullet">
///   <item>a path allow-list, checked before the script is launched;</item>
///   <item>a per-hook timeout with a process-tree kill;</item>
///   <item>refusal to run identity-dependent hooks for a peer that has not authenticated.</item>
/// </list>
/// The hooks still run as the service account; the config documents that in bold.
/// </remarks>
public sealed class HookRunner
{
    private readonly HookOptions _options;
    private readonly IProcessRunner _runner;
    private readonly ILogger _log;

    public HookRunner(HookOptions options, IProcessRunner runner, ILogger? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _log = log ?? new Logging.StructuredLogger(LogLevel.Critical);
    }

    /// <summary>Number of hooks actually executed, for tests and diagnostics.</summary>
    public int ExecutionCount => _executions;
    private int _executions;

    /// <summary>
    /// Runs the hook for <paramref name="hook"/>. Returns
    /// <see cref="HookResult.NotConfigured"/> when no script is configured for the event.
    /// </summary>
    public async Task<HookResult> RunAsync(HookEvent hook, HookContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? script = _options.ScriptFor(hook);
        if (script is null)
            return HookResult.NotConfigured;

        if (hook.RequiresAuthenticatedPeer() && !context.PeerAuthenticated)
        {
            const string why =
                "refused: this hook depends on client identity and the connection is not authenticated";
            _log.Write(LogLevel.Warn, LogEvent.HookRefused, why,
                new Dictionary<string, object?>
                {
                    ["hook"] = hook.Name(),
                    ["client_ip"] = context.ClientIp,
                });
            return HookResult.Refused(why);
        }

        if (!IsAllowedPath(script, out string pathReason))
        {
            _log.Write(LogLevel.Warn, LogEvent.HookRefused, pathReason,
                new Dictionary<string, object?> { ["hook"] = hook.Name(), ["script"] = script });
            return HookResult.Refused(pathReason);
        }

        string commandLine = Substitute(script, context);
        var started = DateTime.UtcNow;
        ProcessOutcome outcome = await _runner.RunAsync(commandLine, _options.Timeout, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _executions);

        var fields = new Dictionary<string, object?>
        {
            ["hook"] = hook.Name(),
            ["exit_code"] = outcome.ExitCode,
            ["timed_out"] = outcome.TimedOut,
            ["elapsed_ms"] = (int)(DateTime.UtcNow - started).TotalMilliseconds,
            ["client_id"] = context.ClientId,
            ["client_ip"] = context.ClientIp,
        };

        if (!outcome.Started)
        {
            _log.Write(LogLevel.Error, LogEvent.HookFailed,
                $"hook {hook.Name()} did not start: {outcome.FailureDetail}", fields);
            return new HookResult { Allow = true, Skipped = true, Detail = outcome.FailureDetail };
        }

        if (outcome.TimedOut)
        {
            _log.Write(LogLevel.Error, LogEvent.HookTimedOut,
                $"hook {hook.Name()} {outcome.FailureDetail}", fields);
            return new HookResult
            {
                Allow = true,
                Skipped = true,
                Output = outcome.CombinedOutput,
                Detail = outcome.FailureDetail,
            };
        }

        int exit = outcome.ExitCode ?? 0;
        bool unknown = exit is not (HookVocabulary.ExitYes or HookVocabulary.ExitNo);

        // A broken or unexpected hook must not silently lock the operator out of their own
        // devices, so anything that is not an explicit "no" is treated as allowed — which is
        // what the class-level contract says and what the reference behaviour implies.
        bool allow = unknown || exit == HookVocabulary.ExitYes;

        _log.Write(
            unknown ? LogLevel.Warn : LogLevel.Info,
            unknown ? LogEvent.HookUnknownExitCode : LogEvent.HookCompleted,
            unknown
                ? $"hook {hook.Name()} returned an unexpected code {exit}; treating the event as allowed"
                : $"hook {hook.Name()} answered {(allow ? "yes" : "no")}",
            fields);

        return new HookResult
        {
            Allow = allow,
            ExitCode = exit,
            UnknownExitCode = unknown,
            Output = outcome.CombinedOutput,
            Detail = unknown ? $"unexpected exit code {exit}" : string.Empty,
        };
    }

    /// <summary>
    /// Applies the <c>$VARIABLE$</c> substitutions fixed in ARCHITECTURE.md Appendix C.
    /// </summary>
    public static string Substitute(string script, HookContext context)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(context);
        return script
            .Replace("$CLIENT_ID$", context.ClientId, StringComparison.Ordinal)
            .Replace("$CLIENT_IP$", context.ClientIp, StringComparison.Ordinal)
            .Replace("$NICKNAME$", context.Nickname, StringComparison.Ordinal)
            .Replace("$NEW_NICKNAME$", context.NewNickname, StringComparison.Ordinal)
            .Replace("$DEVPATH$", context.DevicePath, StringComparison.Ordinal)
            .Replace("$VENDOR_ID$", context.VendorId, StringComparison.Ordinal)
            .Replace("$PRODUCT_ID$", context.ProductId, StringComparison.Ordinal);
    }

    private bool IsAllowedPath(string script, out string reason)
    {
        reason = string.Empty;
        if (_options.AllowedScriptRoots.Count == 0)
        {
            reason = string.Empty;
            return true;
        }

        // The script field may carry arguments; the path is the token before the first space.
        string candidate = script.Trim();
        int space = candidate.IndexOf(' ');
        if (space > 0) candidate = candidate[..space];

        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex)
        {
            reason = $"refused: hook script path '{candidate}' is not a valid path ({ex.Message})";
            return false;
        }

        foreach (string root in _options.AllowedScriptRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string fullRoot;
            try
            {
                fullRoot = Path.GetFullPath(root);
            }
            catch
            {
                continue;
            }

            string withSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            if (full.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(full, fullRoot, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        reason = $"refused: hook script '{full}' is outside every configured allowed root";
        return false;
    }
}
