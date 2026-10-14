// SPDX-License-Identifier: Apache-2.0
//
// A peer: a remote USB/IP server this client can attach from.
//
// Lives in the root `Vht.Client` namespace because every layer needs it — transport, bridge, catalog,
// policy, credentials — and a type that four namespaces import is a domain type, not a member of any
// one of them.

namespace Vht.Client;

/// <summary>
/// A peer (a remote USB/IP server) as the client knows it. <see cref="Name"/> is the user-visible
/// identity used by the auto-attach grant store and by qualified busid routing, so a rename is a
/// rename and never a silent re-consent (ARCHITECTURE.md §7).
/// </summary>
internal sealed record Peer
{
    /// <summary>User-visible peer name; used in grants, routing keys and logs.</summary>
    internal required string Name { get; init; }

    internal required string Host { get; init; }

    /// <summary>3241 is the TLS listener's default; 3240 is the plaintext listener's (ARCHITECTURE.md §6.3).</summary>
    internal int Port { get; init; } = 3241;

    /// <summary>True for the TLS listener. The plaintext stub transport refuses this (no downgrade).</summary>
    internal bool UseTls { get; init; } = true;

    /// <summary>base64url SHA-256 of the server's SubjectPublicKeyInfo, once the user has pinned one.</summary>
    internal string? SpkiPin { get; init; }

    /// <summary>True for a peer the user added by hand — manual entry always works (§7).</summary>
    internal bool Manual { get; init; } = true;

    internal string Endpoint => $"{Host}:{Port}";

    public override string ToString() => $"{Name} @ {Endpoint}{(UseTls ? " (tls)" : " (plaintext)")}";
}
