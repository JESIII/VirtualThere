using System.Windows;
using Vht.Ui.ViewModels;
using Vht.Ui.Views;

namespace Vht.Ui.Services;

/// <summary>
/// Dialog implementation used by the running application. The only two prompts in the whole
/// UI are "device nickname" and "device password"; there is deliberately no dialog that asks
/// for a key, an account or a payment.
/// </summary>
public sealed class WpfPromptService : IPromptService
{
    public string? AskText(string title, string label, string? initialValue = null)
    {
        var window = new PromptWindow(title, label, initialValue, secret: false)
        {
            Owner = OwnerOrSelf(windowOwner: Application.Current?.MainWindow),
        };

        bool? result = window.ShowDialog();
        return result == true ? window.Value : null;
    }

    public SecretPromptResult AskSecret(string title, string label)
    {
        var window = new PromptWindow(title, label, initialValue: null, secret: true)
        {
            Owner = OwnerOrSelf(windowOwner: Application.Current?.MainWindow),
        };

        bool? result = window.ShowDialog();
        if (result != true)
        {
            return new SecretPromptResult(false, false, null);
        }

        return window.RemoveSecret
            ? new SecretPromptResult(true, true, null)
            : new SecretPromptResult(true, false, window.Value);
    }

    private static Window? OwnerOrSelf(Window? windowOwner) =>
        windowOwner is { IsVisible: true } ? windowOwner : null;
}
