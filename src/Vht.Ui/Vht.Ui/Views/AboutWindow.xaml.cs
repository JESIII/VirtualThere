using System.Windows;

namespace Vht.Ui.Views;

/// <summary>
/// The about box: product name, version, what the project is, and its open-source licence.
/// It contains no agreement text to accept, no key field and no purchase link - there is no
/// such thing anywhere in this application (docs/UI.md, "No activation surface").
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        VersionText.Text = "Version " + Branding.VersionText;
        BodyText.Text = Branding.AboutBody;
        LicenceText.Text = Branding.AboutLicenceLine;
        ComponentsText.Text =
            "Tray, windows and control-plane client: this application (Apache-2.0).\n" +
            "UI toolkit: WPF and Windows Forms NotifyIcon, MIT, .NET 10 in-box.\n" +
            "IPC and JSON: System.IO.Pipes and System.Text.Json, MIT, .NET 10 in-box.\n" +
            "It speaks to the " + Branding.ProductName + " server and client services over a local " +
            "named pipe and never accesses USB devices itself.";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
