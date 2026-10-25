using System;
using System.Collections.Generic;
using Vht.Ui.Hosting;

namespace Vht.Ui.Services;

/// <summary>
/// Command line of a role executable: <c>vht-server.exe</c> or <c>vht-client.exe</c>, each with
/// its own window.
/// </summary>
/// <remarks>
/// <para>
/// There is no <c>--mode</c> any more: which window a process presents is decided by which
/// executable was started (see <see cref="UiRole"/>). <c>--mode</c> is still accepted and ignored
/// so a shortcut written for the old single tray executable keeps working - it just cannot change
/// the role of the binary it points at.
/// </para>
/// <para>
/// <c>--pipe</c>, <c>--minimized</c> and <c>--help</c> are the flags the UI harness and the
/// installer's shortcuts use, and they keep working as before.
/// </para>
/// </remarks>
public sealed class AppOptions
{
    /// <summary>
    /// The role this process presents. Set by the executable's entry point through
    /// <see cref="UiHost.Run"/>; the command line cannot override it.
    /// </summary>
    public UiRole Role { get; internal set; } = UiRole.Server;

    /// <summary>
    /// The default log directory the role executable resolved for the worker's durable output
    /// record (see <see cref="Hosting.WorkerLaunchSpec.OutputLogDirectory"/>). Set by
    /// <see cref="UiHost.Run"/> from the executable's own path resolution, so the window cannot
    /// disagree with the worker about where logs go; the command line cannot override it.
    /// </summary>
    public string? WorkerOutputLogDirectory { get; internal set; }

    public string PipeName { get; private set; } = Branding.PipeServer;

    public bool PipeNameExplicit { get; private set; }

    public bool StartMinimized { get; private set; }

    /// <summary>Plaintext listener port handed to the server worker, when it was given.</summary>
    public int? Port { get; private set; }

    /// <summary>
    /// True when the worker must be started without elevation. Default false: the shipping path
    /// raises exactly one consent prompt. <c>--no-elevate</c> exists for the automated harness,
    /// which cannot answer a UAC dialog, and is what lets the orphan-worker behaviour be asserted
    /// without a human at the keyboard.
    /// </summary>
    public bool NoElevate { get; private set; }

    /// <summary>
    /// True when the window must press its own Start as soon as it is up: the exact command the
    /// Start button executes, without a human to click it. Automation only (the acceptance test
    /// that measures "the window process dies, its worker keeps serving, the next window attaches"),
    /// and it is what makes that measurement possible without UI Automation and a desktop.
    /// </summary>
    public bool StartWorker { get; private set; }

    public bool ShowHelp { get; private set; }

    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new AppOptions();
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--pipe":
                    if (i + 1 < args.Count)
                    {
                        options.PipeName = args[++i];
                        options.PipeNameExplicit = true;
                    }

                    break;

                case "--port":
                    if (i + 1 < args.Count && int.TryParse(args[++i], out int port))
                    {
                        options.Port = port;
                    }

                    break;

                case "--no-elevate":
                    options.NoElevate = true;
                    break;

                case "--start-worker":
                    options.StartWorker = true;
                    break;

                case "--minimized":
                case "--tray":
                    options.StartMinimized = true;
                    break;

                case "--help":
                case "-h":
                case "/?":
                    options.ShowHelp = true;
                    break;

                case "--mode":
                case "-m":
                    // Accepted for compatibility with shortcuts written for the old single tray
                    // executable. The value is consumed and deliberately ignored.
                    if (i + 1 < args.Count)
                    {
                        i++;
                    }

                    break;

                default:
                    // Anything else is ignored here: the role executables consume the rest of the
                    // command line (verbs, --worker) before the GUI path is reached.
                    break;
            }
        }

        if (!options.PipeNameExplicit)
        {
            options.PipeName = Branding.PipeServer;
        }

        return options;
    }

    /// <summary>
    /// Fills in the role's default pipe. Called by the host once the role is known, because the role
    /// comes from the executable rather than from the command line.
    /// </summary>
    internal void ResolveDefaultPipe()
    {
        if (!PipeNameExplicit)
        {
            PipeName = Role == UiRole.Client ? Branding.PipeClient : Branding.PipeServer;
        }
    }

    public static string HelpText(UiRole role)
    {
        string executable = role == UiRole.Server ? "vht-server.exe" : "vht-client.exe";
        string window = role == UiRole.Server ? Branding.ServerTitle : Branding.ClientTitle;

        return Branding.ProductName + " - " + window + "\n" +
            "\n" +
            "  " + executable + "                     open the " + window + " window\n" +
            "  " + executable + " [options]\n" +
            "\n" +
            "  --pipe <name>          control pipe to attach to (default: " +
                Branding.PipeServer + " for the server, " + Branding.PipeClient + " for the client)\n" +
            "  --port <n>             plaintext listener port for the server worker (default: 3240)\n" +
            "  --minimized            start hidden in the notification area\n" +
            "  --no-elevate           start the privileged worker without a consent prompt (automation only)\n" +
            "  --start-worker         press Start as soon as the window is up (automation only)\n" +
            "  --help                 this text\n" +
            "\n" +
            "The window runs unelevated. Starting the service from it launches this same\n" +
            "executable as a hidden, elevated worker, which raises one consent prompt.\n" +
            "The console verbs (serve, status, list, ...) are unchanged: pass one explicitly, or\n" +
            "use " + Branding.ExecutableName + ", which offers the same verbs.\n";
    }
}
