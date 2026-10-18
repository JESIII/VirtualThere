using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using Vht.Ui.Services;

namespace Vht.Ui.Hosting;

/// <summary>
/// Entry point every role executable calls: <c>UiHost.Run(UiRole.Server, args)</c> from
/// <c>vht-server.exe</c>, <c>UiHost.Run(UiRole.Client, args)</c> from <c>vht-client.exe</c>.
/// </summary>
/// <remarks>
/// <para>
/// Runs on the STA thread the executable's <c>Main</c> already owns, and blocks until the user
/// exits. The worker the GUI started (if any) is stopped before this returns, so the process
/// that opens a window is also the process that cleans up after itself
/// (see <see cref="WorkerSupervisor"/>).
/// </para>
/// </remarks>
public static class UiHost
{
    /// <summary>The switch a role executable uses to run its privileged worker instead of a window.</summary>
    public const string WorkerSwitch = "--worker";

    /// <summary>
    /// True when this command line asks for the privileged worker instead of a window. The worker is
    /// the same executable with <see cref="WorkerSwitch"/>; nothing else is added to it, because the
    /// worker does not watch the window it came from - it outlives it (see <see cref="WorkerLifetime"/>).
    /// </summary>
    public static bool IsWorkerInvocation(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        foreach (string argument in args)
        {
            if (string.Equals(argument, WorkerSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs the management window for <paramref name="role"/>. Returns the process exit code.
    /// <paramref name="workerOutputLogDirectory"/> is the default log directory the executable
    /// resolved (the server runtime log's <c>&lt;resolved config directory&gt;\logs</c>); the
    /// window hands it to the launcher so a worker's captured output lands next to the worker's
    /// own log rather than somewhere of the window's choosing.
    /// </summary>
    public static int Run(
        UiRole role,
        IReadOnlyList<string> args,
        string? workerOutputLogDirectory = null)
    {
        AppOptions options = AppOptions.Parse(args);

        // The executable decides the role; --mode is accepted but cannot change it. The default
        // control pipe follows from the role, so it is resolved only once the role is set.
        options.Role = role;
        options.WorkerOutputLogDirectory = workerOutputLogDirectory;
        options.ResolveDefaultPipe();

        if (options.ShowHelp)
        {
            Dialogs.ShowInformation(AppOptions.HelpText(role));
            return 0;
        }

        var session = new UiSession(role, options);
        if (!session.TryClaimSingleInstance())
        {
            Dialogs.ShowInformation(
                Branding.ProductName + " " + RoleName(role) + " is already running. " +
                "Use its notification-area icon to open the window it already has.");
            session.Dispose();
            return 0;
        }

        try
        {
            var application = new UiApplication();
            application.DispatcherUnhandledException += (_, e) => session.HandleUnhandledException(e);
            application.Exit += (_, _) => session.Shutdown();
            application.Startup += (_, _) => session.Start();

            int exitCode = application.Run();
            session.Shutdown();
            return exitCode;
        }
        finally
        {
            session.Dispose();
        }
    }

    internal static string RoleName(UiRole role) => role == UiRole.Server ? "server" : "client";
}

/// <summary>Message boxes, kept in one place: a WinExe has no console to print usage to.</summary>
internal static class Dialogs
{
    internal static void ShowInformation(string text) =>
        MessageBox.Show(text, Branding.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);

    internal static void ShowWarning(string text) =>
        MessageBox.Show(text, Branding.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
}
