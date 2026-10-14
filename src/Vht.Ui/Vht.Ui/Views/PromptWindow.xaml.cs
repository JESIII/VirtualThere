using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace Vht.Ui.Views;

/// <summary>
/// The only text input dialog in the application: a device nickname, or a device password.
/// There is no other prompt - in particular none for a key, an account or a payment.
/// </summary>
public partial class PromptWindow : Window
{
    private readonly bool _secret;

    public PromptWindow(string title, string label, string? initialValue, bool secret)
    {
        InitializeComponent();

        _secret = secret;
        Title = title;
        LabelText.Text = label;
        AutomationProperties.SetName(this, title);

        if (secret)
        {
            SecretBox.Visibility = Visibility.Visible;
            RemoveCheck.Visibility = Visibility.Visible;
            HintText.Text = "The password is sent to the service over the local control pipe and stored by it.";
            Loaded += (_, _) => SecretBox.Focus();
        }
        else
        {
            ValueBox.Visibility = Visibility.Visible;
            ValueBox.Text = initialValue ?? string.Empty;
            HintText.Text = "Leave the field empty to clear the nickname.";
            Loaded += (_, _) =>
            {
                ValueBox.Focus();
                ValueBox.SelectAll();
            };
        }
    }

    /// <summary>Entered text (empty for a removal).</summary>
    public string Value { get; private set; } = string.Empty;

    /// <summary>True when the user asked for the password to be removed.</summary>
    public bool RemoveSecret { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e) => Accept();

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
    }

    private void OnRemoveChecked(object sender, RoutedEventArgs e)
    {
        SecretBox.IsEnabled = !RemoveCheck.IsChecked.GetValueOrDefault();
        OkButton.IsEnabled = true;
    }

    private void Accept()
    {
        Value = _secret ? SecretBox.Password : ValueBox.Text;
        RemoveSecret = _secret && RemoveCheck.IsChecked.GetValueOrDefault();
        DialogResult = true;
    }
}
