namespace Vht.Transport.Auth;

/// <summary>Optional side-interface for a pin store that can report its own integrity.</summary>
/// <remarks>A pin store that silently reads as empty would turn Strict mode into Trust-On-First-Use.</remarks>
public interface IPinStoreHealth
{
    /// <summary>True when the backing document was read successfully.</summary>
    bool Healthy { get; }
}
