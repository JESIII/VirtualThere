// SPDX-License-Identifier: Apache-2.0
//
// Where the client keeps its files. ARCHITECTURE.md §9: `%LOCALAPPDATA%\VirtualThere\config.toml`
// for the client/tray, overridable by `--config <path>` and `VHT_CONFIG`, and portable builds keep
// config next to the binary when a `portable.marker` file is present.
//
// The *format* (TOML, via Tomlyn) and the canonical key names are Vht.Config's (card config-workstream).
// Until that lands, the client persists the small state files it owns (peers, auto-attach grants,
// credential remember-flags) as JSON, in the same directory, behind PeerStore / AutoAttachPolicy /
// CredentialCache so replacing them with Vht.Config's store is a localised change. The documented
// path layout below does not change either way.

namespace Vht.Client.Config;

/// <summary>Resolves the client's config/state locations.</summary>
internal static class ClientPaths
{
    private const string ProductDirectoryName = "VirtualThere";

    /// <summary>`%LOCALAPPDATA%\VirtualThere`, or next to the binary in a portable build.</summary>
    internal static string ConfigDirectory
    {
        get
        {
            string? explicitConfig = Environment.GetEnvironmentVariable("VHT_CONFIG");
            if (!string.IsNullOrWhiteSpace(explicitConfig))
            {
                string? directory = Path.GetDirectoryName(Path.GetFullPath(explicitConfig));
                if (!string.IsNullOrEmpty(directory))
                {
                    return directory;
                }
            }

            string? portableMarker = AppContext.BaseDirectory;
            if (portableMarker is not null && File.Exists(Path.Combine(portableMarker, "portable.marker")))
            {
                return AppContext.BaseDirectory;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, ProductDirectoryName, "client");
        }
    }

    internal static string ConfigFile => Path.Combine(ConfigDirectory, "config.toml");

    internal static string PeersFile => Path.Combine(ConfigDirectory, "peers.json");

    internal static string AutoAttachGrantsFile => Path.Combine(ConfigDirectory, "autoattach.json");

    internal static string CredentialMemoryFile => Path.Combine(ConfigDirectory, "credentials.json");

    internal static string LogFile => Path.Combine(ConfigDirectory, "vht-client.log");

    /// <summary>Applies `--config &lt;path&gt;` by exporting it, so every component agrees on one directory.</summary>
    internal static void ApplyConfigOverride(string? configPath)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            Environment.SetEnvironmentVariable("VHT_CONFIG", Path.GetFullPath(configPath));
        }
    }
}
