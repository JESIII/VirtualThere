using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Vht.Ui.ControlPlane;
using Vht.Ui.Services;
using Vht.Ui.ViewModels;
using Vht.Ui.Views;

namespace Vht.Ui;

/// <summary>
/// One tray executable, two modes (docs/ARCHITECTURE.md section 1). The mode decides only
/// which control pipe is spoken to and which window is presented; both modes share the
/// control-plane client, the log viewer, the about box and the tray behaviour.
/// </summary>
public partial class App : Application
{
    private readonly CancellationTokenSource _shutdown = new();
    private Mutex? _instanceMutex;
    private TrayService? _tray;
    private TrayWindow? _window;
    private ServerViewModel? _serverViewModel;
    private ClientViewModel? _clientViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppOptions options = AppOptions.Parse(e.Args);

        if (options.ShowHelp)
        {
            MessageBox.Show(AppOptions.HelpText, Branding.ProductName + " tray", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        bool created;
        _instanceMutex = new Mutex(initiallyOwned: true, name: @"Local\VirtualThere.Tray." + options.Mode, out created);
        if (!created)
        {
            // One instance per mode; the running one is reachable from the notification area.
            MessageBox.Show(
                Branding.ProductName + " " + options.Mode.ToString().ToLowerInvariant() +
                " tray is already running. Use its notification-area icon to open it.",
                Branding.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        if (options.Mode == TrayMode.Server)
        {
            StartServerMode(options);
        }
        else
        {
            StartClientMode(options);
        }
    }

    private void StartServerMode(AppOptions options)
    {
        var service = new VhtServiceClient(new ControlPlaneClient(options.PipeName));
        _serverViewModel = new ServerViewModel(service, new WpfPromptService(), TimeSpan.FromSeconds(2));

        var window = new ServerWindow { DataContext = _serverViewModel };
        _window = window;

        _tray = new TrayService(
            Branding.TrayServerTooltip,
            () => _serverViewModel.IsRunning,
            show: () => window.RestoreFromTray(),
            start: () => _serverViewModel.StartCommand.Execute(null),
            stop: () => _serverViewModel.StopCommand.Execute(null),
            showLog: () =>
            {
                _serverViewModel.Log.IsVisible = true;
                window.RestoreFromTray();
            },
            about: () => ShowAbout(window),
            exit: window.ExitApplication);

        window.MinimizedToTray += (_, _) => _tray?.HintStillRunning();
        window.RestoredFromTray += (_, _) => _tray?.Update();
        _serverViewModel.PropertyChanged += (_, _) => _tray?.Update();

        _serverViewModel.MinimizeRequested += (_, _) => window.HideToTray();
        _serverViewModel.AboutRequested += (_, _) => ShowAbout(window);
        _serverViewModel.ExitRequested += (_, _) => window.ExitApplication();

        _serverViewModel.Start();
        if (!options.StartMinimized)
        {
            window.Show();
        }
        else
        {
            _tray.HintStillRunning();
        }
    }

    private void StartClientMode(AppOptions options)
    {
        var service = new VhtServiceClient(new ControlPlaneClient(options.PipeName));
        _clientViewModel = new ClientViewModel(service, new WpfPromptService(), TimeSpan.FromSeconds(2));

        var window = new ClientWindow { DataContext = _clientViewModel };
        _window = window;

        _tray = new TrayService(
            Branding.TrayClientTooltip,
            () => _clientViewModel.ServiceReachable,
            show: () => window.RestoreFromTray(),
            start: () => _clientViewModel.RefreshCommand.Execute(null),
            stop: () => _ = _clientViewModel.DetachAllAsync(),
            showLog: () =>
            {
                _clientViewModel.Log.IsVisible = true;
                window.RestoreFromTray();
            },
            about: () => ShowAbout(window),
            exit: window.ExitApplication);

        window.MinimizedToTray += (_, _) => _tray?.HintStillRunning();
        window.RestoredFromTray += (_, _) => _tray?.Update();
        _clientViewModel.PropertyChanged += (_, _) => _tray?.Update();

        _clientViewModel.MinimizeRequested += (_, _) => window.HideToTray();
        _clientViewModel.AboutRequested += (_, _) => ShowAbout(window);
        _clientViewModel.ExitRequested += (_, _) => window.ExitApplication();

        _clientViewModel.Start();
        if (!options.StartMinimized)
        {
            window.Show();
        }
        else
        {
            _tray.HintStillRunning();
        }
    }

    private static void ShowAbout(Window owner)
    {
        var about = new AboutWindow { Owner = owner.IsVisible ? owner : null };
        about.ShowDialog();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A tray app must not die silently: report and keep the notification-area icon alive.
        _serverViewModel?.Log.AppendLocal("Unhandled error: " + e.Exception.Message);
        _clientViewModel?.Log.AppendLocal("Unhandled error: " + e.Exception.Message);
        MessageBox.Show(
            "An unexpected error occurred:\n\n" + e.Exception.Message,
            Branding.ProductName,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        _serverViewModel?.Dispose();
        _clientViewModel?.Dispose();
        _tray?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _shutdown.Dispose();
        base.OnExit(e);
    }
}
