using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Vht.Ui.ControlPlane;
using Vht.Ui.Services;
using Vht.Ui.ViewModels;
using Vht.Ui.Views;

namespace Vht.Ui.Hosting;

/// <summary>
/// One management window, its view model, its notification-area presence and the privileged
/// worker it may have started. This is the old <c>App.xaml.cs</c> body, split out of the
/// <see cref="Application"/> so the same session can run under either role executable.
/// </summary>
internal sealed class UiSession : IDisposable
{
    private readonly UiRole _role;
    private readonly AppOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private Mutex? _instanceMutex;
    private TrayService? _tray;
    private TrayWindow? _window;
    private ServerViewModel? _server;
    private ClientViewModel? _client;
    private WorkerSupervisor? _workers;
    private WorkerLifetime? _lifetime;
    private bool _shutdownDone;

    internal UiSession(UiRole role, AppOptions options)
    {
        _role = role;
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Claims the per-role single-instance mutex. A second <c>vht-server.exe</c> must not start a
    /// second worker or a second control-plane client; the running one is reachable from its
    /// notification-area icon.
    /// </summary>
    internal bool TryClaimSingleInstance()
    {
        _instanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\VirtualThere." + UiHost.RoleName(_role),
            out bool created);
        return created;
    }

    internal void Start()
    {
        if (_role == UiRole.Server)
        {
            StartServerMode();
        }
        else
        {
            StartClientMode();
        }
    }

    private void StartServerMode()
    {
        var client = new VhtServiceClient(new ControlPlaneClient(_options.PipeName));
        _workers = WorkerSupervisor.ForServer(client, _options);
        _lifetime = new WorkerLifetime(_workers);
        _server = new ServerViewModel(client, new WpfPromptService(), TimeSpan.FromSeconds(2), _workers);

        var window = new ServerWindow { DataContext = _server };
        _window = window;

        _tray = new TrayService(
            Branding.TrayServerTooltip,
            () => _server.IsRunning,
            show: () => window.RestoreFromTray(),
            start: () => _server.StartCommand.Execute(null),
            stop: () => _server.StopCommand.Execute(null),
            showLog: () =>
            {
                _server.Log.IsVisible = true;
                window.RestoreFromTray();
            },
            about: () => ShowAbout(window),
            exit: window.ExitApplication);

        // Closing or minimising the window hides it; the worker keeps serving (WorkerLifetime).
        window.MinimizedToTray += (_, _) =>
        {
            _lifetime?.OnWindowHidden();
            _tray?.HintStillRunning();
        };

        window.RestoredFromTray += (_, _) => _tray?.Update();
        _server.PropertyChanged += (_, _) => _tray?.Update();

        _server.MinimizeRequested += (_, _) => window.HideToTray();
        _server.AboutRequested += (_, _) => ShowAbout(window);
        _server.ExitRequested += (_, _) => window.ExitApplication();

        _server.Start();
        Present(window);
    }

    private void StartClientMode()
    {
        var client = new VhtServiceClient(new ControlPlaneClient(_options.PipeName));
        _workers = WorkerSupervisor.ForClient(client, _options);
        _lifetime = new WorkerLifetime(_workers);
        _client = new ClientViewModel(client, new WpfPromptService(), TimeSpan.FromSeconds(2), _workers);

        var window = new ClientWindow { DataContext = _client };
        _window = window;

        _tray = new TrayService(
            Branding.TrayClientTooltip,
            () => _client.ServiceReachable,
            show: () => window.RestoreFromTray(),
            start: () => _client.RefreshCommand.Execute(null),
            stop: () => _ = _client.DetachAllAsync(),
            showLog: () =>
            {
                _client.Log.IsVisible = true;
                window.RestoreFromTray();
            },
            about: () => ShowAbout(window),
            exit: window.ExitApplication,
            startText: () => _client.StartOrRefreshText);

        window.MinimizedToTray += (_, _) =>
        {
            _lifetime?.OnWindowHidden();
            _tray?.HintStillRunning();
        };

        window.RestoredFromTray += (_, _) => _tray?.Update();
        _client.PropertyChanged += (_, _) => _tray?.Update();

        _client.MinimizeRequested += (_, _) => window.HideToTray();
        _client.AboutRequested += (_, _) => ShowAbout(window);
        _client.ExitRequested += (_, _) => window.ExitApplication();

        _client.Start();
        Present(window);
    }

    private void Present(TrayWindow window)
    {
        if (!_options.StartMinimized)
        {
            window.Show();
            return;
        }

        _tray?.HintStillRunning();
    }

    private static void ShowAbout(Window owner)
    {
        var about = new AboutWindow { Owner = owner.IsVisible ? owner : null };
        about.ShowDialog();
    }

    internal void HandleUnhandledException(DispatcherUnhandledExceptionEventArgs e)
    {
        // A management window must not die silently: report and keep the notification-area icon
        // (and the worker this session owns) alive.
        //
        // Reporting is the last thing between a caught exception and the process dying, so it is
        // not allowed to throw: an exception raised here is not handed back to
        // DispatcherUnhandledException, it terminates the process (and with it the worker it was
        // supervising). That is exactly what a log line once did - appending it forced a layout
        // pass from inside this handler, on a visual tree that was already mid-exception.
        try
        {
            _server?.Log.AppendLocal("Unhandled error: " + e.Exception.Message);
            _client?.Log.AppendLocal("Unhandled error: " + e.Exception.Message);
        }
        catch (Exception)
        {
            // The log is a diagnostic; losing it is better than losing the window.
        }

        Dialogs.ShowWarning("An unexpected error occurred:\n\n" + e.Exception.Message);
        e.Handled = true;
    }

    /// <summary>
    /// The explicit-Exit path: stops the privileged worker this session started, then releases
    /// everything else. Runs on the way out of <c>Application.Run</c> - which only happens on Exit,
    /// never when the window is closed to the notification area - and is idempotent: the exit
    /// handler and the caller both invoke it.
    /// </summary>
    internal void Shutdown()
    {
        if (_shutdownDone)
        {
            return;
        }

        _shutdownDone = true;
        _shutdown.Cancel();

        // The worker is stopped first, while the pipe client and the log are still alive, and it
        // is stopped *only* when this session started it: a worker that came from the installed
        // service, or from another instance, is left alone (WorkerLifetime.OnExitAsync).
        if (_lifetime is not null)
        {
            try
            {
                _lifetime.OnExitAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _server?.Log.AppendLocal("Could not stop the worker: " + ex.Message);
                _client?.Log.AppendLocal("Could not stop the worker: " + ex.Message);
            }
        }

        _server?.Dispose();
        _client?.Dispose();
        _tray?.Dispose();
        _workers?.Dispose();
    }

    public void Dispose()
    {
        Shutdown();

        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owner (a second instance that lost the race): nothing to release.
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        _shutdown.Dispose();
    }
}
