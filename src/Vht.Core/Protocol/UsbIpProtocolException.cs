namespace Vht.Core.Protocol;

/// <summary>
/// A malformed, truncated, oversized or out-of-sequence USB/IP frame.
/// </summary>
/// <remarks>
/// The server parses frames from an untrusted peer, so this exception is the *only* correct
/// response to a bad frame (ARCHITECTURE.md §6.1, risk 10). Callers translate it into a
/// connection-level error and close the connection; it is never swallowed.
/// </remarks>
public sealed class UsbIpProtocolException : Exception
{
    public UsbIpProtocolException(string message) : base(message) { }

    public UsbIpProtocolException(string message, Exception inner) : base(message, inner) { }
}
