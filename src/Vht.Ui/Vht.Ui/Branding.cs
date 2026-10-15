using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace Vht.Ui;

/// <summary>
/// Every product-visible string in the tray application comes from here, so the
/// rebranding rule is checkable in one place: the application name, window titles,
/// tray tooltip, about box and file names all read "VirtualThere", and there is no
/// third-party vendor name, URL or end-user agreement text anywhere in this process.
/// </summary>
/// <remarks>
/// The absence of any activation / purchase surface is deliberate and enforced by
/// tests/Vht.Ui.Tests (see docs/UI.md, "No activation surface"). Do not add a string
/// field for one here.
/// </remarks>
public static class Branding
{
    /// <summary>Product name shown in titles, tray tooltip and the about box.</summary>
    public const string ProductName = "VirtualThere";

    public const string ServerTitle = ProductName + " Server";
    public const string ClientTitle = ProductName + " Client";
    public const string TrayServerTooltip = ProductName + " Server";
    public const string TrayClientTooltip = ProductName + " Client";
    public const string ExecutableName = "vht.exe";

    /// <summary>Short line under the product name in the about box.</summary>
    public const string AboutTagline = "USB devices over the network, for Windows.";

    /// <summary>About-box body. No vendor references, no agreement text.</summary>
    public const string AboutBody =
        ProductName + " is an independent, open implementation of the USB/IP protocol.\n" +
        "It is built from scratch: no third-party product's code, assets or strings are used,\n" +
        "and it is not wire-compatible with any other USB-over-IP implementation.\n" +
        "\n" +
        "This window manages the " + ProductName + " server or client service. The window\n" +
        "itself runs unelevated and never touches USB devices directly: the privileged side is\n" +
        "a separate worker process reached over a local control pipe.";

    public const string AboutLicenceLine =
        "Licensed under the Apache License, Version 2.0. See LICENSE in the project root.";

    public const string PipeServer = "vht-server";
    public const string PipeClient = "vht-client";

    public static string VersionText
    {
        get
        {
            Assembly asm = typeof(Branding).Assembly;
            string? informational = asm
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                int plus = informational.IndexOf('+', StringComparison.Ordinal);
                return plus > 0 ? informational[..plus] : informational;
            }

            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    /// <summary>
    /// Draws the tray icon at runtime (a rounded square with the product initials) so the
    /// build carries no binary icon asset and no borrowed artwork. Callers own the result.
    /// </summary>
    public static Icon CreateTrayIcon()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var backBrush = new SolidBrush(Color.FromArgb(255, 32, 62, 112));
            using var path = RoundedRect(new Rectangle(1, 1, size - 2, size - 2), 7);
            g.FillPath(backBrush, path);

            using var ring = new Pen(Color.FromArgb(255, 226, 236, 250), 1.6f);
            g.DrawPath(ring, path);

            using var font = new Font("Segoe UI", 12.5f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.FromArgb(255, 240, 246, 255));
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("VT", font, textBrush, new RectangleF(0, 0, size, size), format);
        }

        // Icon.FromHandle does not own the HICON; the clone below is independent of the handle.
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            if (!NativeMethods.DestroyIcon(handle))
            {
                // Nothing useful to do: a leaked GDI icon handle at process exit is harmless.
            }
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
