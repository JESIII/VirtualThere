// SPDX-License-Identifier: Apache-2.0
//
// vht-client process entry point: one visible executable that opens the VirtualThere Client
// window, plus the client itself and the console verbs that address it.
//
// Three ways in, decided by the command line:
//   vht-client                     open the VirtualThere Client window (this file, GUI path)
//   vht-client <verb> [options]    the console verbs, unchanged (ClientCli)
//   vht-client --worker ...        the privileged half the window starts: the client runtime,
//                                  foreground, no window and no console
//
// The worker is not tied to the window that started it: a worker whose window is gone keeps
// serving (and keeps attached devices attached), and the next launch of the window attaches to it.
// The window stops its worker on Exit, over the control pipe (clients.detachAll) and then the
// process itself. See docs/UI.md section 1.

using System;
using Vht.Client.Cli;
using Vht.Client.Config;
using Vht.Ui;
using Vht.Ui.Hosting;

namespace Vht.Client;

/// <summary>Process entry point for <c>vht-client</c>.</summary>
internal static class Program
{
    [STAThread]
    internal static int Main(string[] args)
    {
        if (UiHost.IsWorkerInvocation(args))
        {
            return RunBlocking(
                ClientCli.RunAsync(WorkerArguments(args), Console.Out, Console.Error),
                worker: true);
        }

        // A bare first word is a verb, exactly as before: an unknown one is still reported as an
        // unknown command rather than silently opening a window.
        string? verb = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null;
        if (verb is not null)
        {
            HostConsole.EnsureForVerbs();
            return RunBlocking(ClientCli.RunAsync(args, Console.Out, Console.Error), worker: false);
        }

        bool helpWanted = HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "/?");

        if (HasFlag(args, "--version"))
        {
            HostConsole.EnsureForVerbs();
            Console.WriteLine(Branding.ProductName + " " + Branding.VersionText);
            return 0;
        }

        if (helpWanted)
        {
            // With a console (a shell, or a redirect) the usage goes there; with none - a
            // double-click with --help - it is shown in a dialog rather than vanishing.
            if (!HostConsole.EnsureForVerbs())
            {
                return UiHost.Run(UiRole.Client, args, WorkerOutputLogDirectory());
            }

            return RunBlocking(ClientCli.RunAsync(["--help"], Console.Out, Console.Error), worker: false);
        }

        return UiHost.Run(UiRole.Client, args, WorkerOutputLogDirectory());
    }

    /// <summary>
    /// The default log directory the window keeps its worker's output record in: the same rule the
    /// server window uses (<c>&lt;resolved config directory&gt;\logs</c>), with the client's own
    /// resolved config directory. Handed to the window rather than resolved inside it so the path
    /// rule stays with the executables that own it.
    /// </summary>
    private static string WorkerOutputLogDirectory() =>
        Path.Combine(ClientPaths.ConfigDirectory, "logs");

    /// <summary>Clients run a long-lived bridge; the console path has to be driven to completion here.</summary>
    private static int RunBlocking(System.Threading.Tasks.Task<int> run, bool worker)
    {
        try
        {
            return run.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (worker)
        {
            // The window went away and asked us to stop: a clean exit, not a failure.
            return 0;
        }
    }

    /// <summary>Drops <c>--worker</c> before the verb parses its own options.</summary>
    private static string[] WithoutWorkerFlag(string[] args)
    {
        var kept = new System.Collections.Generic.List<string>(args.Length);
        foreach (string argument in args)
        {
            if (string.Equals(argument, UiHost.WorkerSwitch, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            kept.Add(argument);
        }

        return kept.ToArray();
    }

    internal static string[] WorkerArguments(string[] args)
    {
        var workerArgs = new System.Collections.Generic.List<string>(args.Length + 2)
        {
            "serve",
        };
        workerArgs.AddRange(WithoutWorkerFlag(args));
        workerArgs.Add("--log");
        return workerArgs.ToArray();
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (string argument in args)
        {
            if (string.Equals(argument, flag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
