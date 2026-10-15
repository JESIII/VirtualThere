using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;

namespace Vht.Ui.Views;

/// <summary>
/// Shared window behaviour for both modes: close-to-tray (the app stays usable with no window
/// open, which the card requires) and an explicit exit that really exits.
/// </summary>
public abstract class TrayWindow : Window
{
    private bool _exitRequested;

    protected TrayWindow()
    {
        AutomationProperties.SetName(this, Branding.ProductName);
    }

    /// <summary>Raised after the window has been hidden rather than closed.</summary>
    public event EventHandler? MinimizedToTray;

    /// <summary>Raised after the window becomes visible again.</summary>
    public event EventHandler? RestoredFromTray;

    /// <summary>True while the window is hidden in the notification area.</summary>
    public bool IsInTray => !IsVisible;

    /// <summary>Hides the window, keeping the process and the tray icon alive.</summary>
    public void HideToTray()
    {
        Hide();
        MinimizedToTray?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows and foregrounds the window (tray double-click / menu "Open").</summary>
    public void RestoreFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        nint handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            _ = NativeMethods.SetForegroundWindow(handle);
        }

        RestoredFromTray?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// True once the Exit affordance was used. From then on closing the window really closes it
    /// instead of hiding it.
    /// </summary>
    public bool ExitRequested => _exitRequested;

    /// <summary>Exits the application for real (tray menu "Exit", Exit button, Alt+F4 policy).</summary>
    /// <remarks>
    /// The exit flag is set before the application is asked to shut down, and the application is
    /// reached through <c>?</c> so a window with no <see cref="Application"/> (a unit test) still
    /// records the decision instead of throwing.
    /// </remarks>
    public void ExitApplication()
    {
        _exitRequested = true;
        Application.Current?.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            // Closing the window is "minimize to tray", matching the reference product's
            // behaviour and the card's requirement that the app stays usable with no window.
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>Alt+F4 / close button while already in the tray exits only via the tray menu.</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
    }
}
