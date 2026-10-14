// SPDX-License-Identifier: Apache-2.0
//
// Errors the client raises at its own boundary, so the CLI/control plane can distinguish "the peer
// said no" from "we refused to do it" from "the network died".

namespace Vht.Client;

/// <summary>Base for every error this client reports to a caller (CLI, control plane, tests).</summary>
internal class ClientException : Exception
{
    internal ClientException(string message) : base(message) { }

    internal ClientException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A peer or the local driver refused the operation (contains the peer's own status).</summary>
internal sealed class ClientAttachException : ClientException
{
    internal ClientAttachException(string message, Usbip.OpStatus? peerStatus = null) : base(message)
    {
        PeerStatus = peerStatus;
    }

    /// <summary>The peer's op_common.status, when the failure came from the peer rather than locally.</summary>
    internal Usbip.OpStatus? PeerStatus { get; }
}

/// <summary>The transport could not be established or was refused on policy grounds.</summary>
internal sealed class ClientTransportException : ClientException
{
    internal ClientTransportException(string message) : base(message) { }
}

/// <summary>Well-formed bytes were expected and were not there. A malformed frame is an error, never a crash.</summary>
internal sealed class UsbipProtocolException : ClientException
{
    internal UsbipProtocolException(string message) : base(message) { }
}
