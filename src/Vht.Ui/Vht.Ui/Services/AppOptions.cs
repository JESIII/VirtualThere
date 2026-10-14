using System;
using System.Collections.Generic;

namespace Vht.Ui.Services;

public enum TrayMode
{
    Server,
    Client,
}

/// <summary>
/// Command line of the single tray executable:
/// <c>vht-tray [--mode server|client] [--pipe &lt;name&gt;] [--minimized]</c>.
/// One binary, two modes (docs/ARCHITECTURE.md section 1); the mode only chooses which
/// control pipe and which window the process presents.
/// </summary>
public sealed class AppOptions
{
    public TrayMode Mode { get; private set; } = TrayMode.Server;

    public string PipeName { get; private set; } = Branding.PipeServer;

    public bool PipeNameExplicit { get; private set; }

    public bool StartMinimized { get; private set; }

    public static AppOptions Parse(IReadOnlyList<string> args)
    {
        var options = new AppOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--mode":
                case "-m":
                    if (i + 1 < args.Count)
                    {
                        options.Mode = ParseMode(args[++i]);
                    }

                    break;
                case "--client":
                    options.Mode = TrayMode.Client;
                    break;
                case "--server":
                    options.Mode = TrayMode.Server;
                    break;
                case "--pipe":
                    if (i + 1 < args.Count)
                    {
                        options.PipeName = args[++i];
                        options.PipeNameExplicit = true;
                    }

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
                default:
                    // Bare word: treat as a mode, matching the reference product's
                    // "server"/"client" argument style without its naming.
                    if (string.Equals(arg, "server", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "client", StringComparison.OrdinalIgnoreCase))
                    {
                        options.Mode = ParseMode(arg);
                    }

                    break;
            }
        }

        if (!options.PipeNameExplicit)
        {
            options.PipeName = options.Mode == TrayMode.Client ? Branding.PipeClient : Branding.PipeServer;
        }

        return options;
    }

    private static TrayMode ParseMode(string value) =>
        string.Equals(value, "client", StringComparison.OrdinalIgnoreCase) ? TrayMode.Client : TrayMode.Server;

    public bool ShowHelp { get; private set; }

    public static string HelpText =>
        Branding.ProductName + " tray - " + Branding.ExecutableName + "\n" +
        "\n" +
        "  --mode server|client   which service to manage (default: server)\n" +
        "  --pipe <name>          override the control pipe name\n" +
        "  --minimized            start hidden in the notification area\n" +
        "  --help                 this text\n";
}
