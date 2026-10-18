using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Vht.Ui.Services;

namespace Vht.Ui.Hosting;

/// <summary>
/// What to launch when a window has to bring its privileged side up: the *same* executable, with
/// the worker switch instead of the GUI path.
/// </summary>
/// <remarks>
/// <para>
/// The GUI is unelevated and stays unelevated (docs/UI.md section 1); the privileged half is this
/// launch, and Windows shows exactly one consent prompt for it because it is exactly one process
/// started with <c>Verb=runas</c>.
/// </para>
/// <para>
/// Nothing ties the worker's lifetime to the window's: the command line is only the worker switch,
/// the pipe and (server only) the port. A worker whose window is gone keeps serving, and the next
/// launch of the window attaches to it (<see cref="WorkerLifetime"/>).
/// </para>
/// </remarks>
public sealed class WorkerLaunchSpec
{
    /// <summary>The executable to start: the running one, resolved from the process path.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Worker arguments, in order (already split, quoted on the way to the OS).</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Working directory for the worker: the folder the executables live in.</summary>
    public string WorkingDirectory { get; init; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>True to raise the single UAC prompt; false only for the harness (<c>--no-elevate</c>).</summary>
    public bool Elevate { get; init; } = true;

    /// <summary>
    /// Where the window keeps the durable record of this launch: one <c>worker-&lt;pid&gt;.log</c>
    /// per worker (see <see cref="WorkerOutputLog"/>), in the default log directory the role
    /// executable resolved - the server runtime log's <c>&lt;resolved config directory&gt;\logs</c>,
    /// so both records land in one place. Handed in through <see cref="AppOptions"/> rather than
    /// resolved here: the product-wide path rule lives with the role executables, and the window
    /// must not grow a second copy of it. Null (nothing supplied, or a harness test) means nothing
    /// is persisted and the launch behaves exactly as it did before.
    /// </summary>
    public string? OutputLogDirectory { get; init; }

    /// <summary>The command line as one string, for <see cref="System.Diagnostics.ProcessStartInfo.Arguments"/>.</summary>
    public string ArgumentsText()
    {
        var text = new StringBuilder();
        foreach (string argument in Arguments)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append(Quote(argument));
        }

        return text.ToString();
    }

    /// <summary>Builds the launch spec for one role from the switches the window was started with.</summary>
    public static WorkerLaunchSpec ForRole(UiRole role, AppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("the running executable could not be resolved");

        var arguments = new List<string> { UiHost.WorkerSwitch };

        // --pipe is honoured by both roles; --port only means anything to the server. The pipe is
        // always passed explicitly so the worker cannot resolve a different one from a config file.
        arguments.Add("--pipe");
        arguments.Add(options.PipeName);

        if (role == UiRole.Server && options.Port is { } port)
        {
            arguments.Add("--port");
            arguments.Add(port.ToString(CultureInfo.InvariantCulture));
        }

        return new WorkerLaunchSpec
        {
            ExecutablePath = executable,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            Elevate = !options.NoElevate,
            OutputLogDirectory = options.WorkerOutputLogDirectory,
        };
    }

    private static string Quote(string argument) =>
        argument.Length == 0 || argument.IndexOf(' ', StringComparison.Ordinal) >= 0
            ? "\"" + argument + "\""
            : argument;
}
