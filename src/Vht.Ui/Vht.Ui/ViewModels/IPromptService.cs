namespace Vht.Ui.ViewModels;

/// <summary>Outcome of the password dialog.</summary>
/// <param name="Accepted">False when the user cancelled; no verb is sent in that case.</param>
/// <param name="Remove">True when the user asked for the password to be removed.</param>
/// <param name="Value">The password, or null when removing.</param>
public sealed record SecretPromptResult(bool Accepted, bool Remove, string? Value);

/// <summary>
/// The dialogs the view models need. Abstracted so the view models are exercisable without a
/// message pump (tests and the UI driver substitute a scripted implementation).
/// </summary>
public interface IPromptService
{
    /// <summary>Single-line text input. Returns null when cancelled.</summary>
    string? AskText(string title, string label, string? initialValue = null);

    /// <summary>Password-style input with an explicit "remove" choice.</summary>
    SecretPromptResult AskSecret(string title, string label);
}

/// <summary>Cancels everything: used where no user interaction is possible.</summary>
public sealed class NullPromptService : IPromptService
{
    public string? AskText(string title, string label, string? initialValue = null) => null;

    public SecretPromptResult AskSecret(string title, string label) => new(false, false, null);
}
