using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Vht.Transport.Tls;

/// <summary>
/// Filesystem hardening for the server's private key and the client's pin store
/// (ARCHITECTURE.md §6.1: "stored with restrictive ACL").
/// </summary>
/// <remarks>
/// <para>
/// Windows ACLs are set through in-box <c>icacls.exe</c> rather than a P/Invoke or the
/// <c>System.IO.FileSystem.AccessControl</c> package, because that package is a Windows-only
/// dependency and <c>Vht.Transport</c> is deliberately a portable <c>net10.0</c> library. The
/// principal is expressed as <c>DOMAIN\user</c>, taken from the environment rather than by parsing
/// localized tool output.
/// </para>
/// <para>
/// Hardening failure is a hard error unless the caller explicitly accepted the risk: an unreadable
/// private key is an outage, but a world-readable private key is a compromise.
/// </para>
/// </remarks>
public static class SecureFileStore
{
    /// <summary>Restricts a directory to the current user, removing inherited access.</summary>
    public static void HardenDirectory(string path, bool allowInsecureKeyStorage = false) =>
        Harden(path, isDirectory: true, allowInsecureKeyStorage);

    /// <summary>Restricts a file to the current user, removing inherited access.</summary>
    public static void HardenFile(string path, bool allowInsecureKeyStorage = false) =>
        Harden(path, isDirectory: false, allowInsecureKeyStorage);

    /// <summary>Writes a file and immediately hardens it, using a create-with-no-sharing handle so the bytes are never visible unprotected.</summary>
    public static void WriteSecretFile(string path, ReadOnlySpan<byte> contents, bool allowInsecureKeyStorage = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            HardenDirectory(directory, allowInsecureKeyStorage);
        }

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }

        HardenFile(path, allowInsecureKeyStorage);
    }

    /// <summary>Reads a file that is expected to be secret.</summary>
    public static byte[] ReadSecretFile(string path) => File.ReadAllBytes(path);

    private static void Harden(string path, bool isDirectory, bool allowInsecureKeyStorage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                HardenWindows(path, isDirectory);
            }
            else
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    (isDirectory ? UnixFileMode.UserExecute : UnixFileMode.None));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!allowInsecureKeyStorage)
            {
                throw new InvalidOperationException(
                    $"could not restrict permissions on '{path}': {ex.Message}. " +
                    "Refusing to keep secret material readable by other accounts; set " +
                    "transport.tls.allow_insecure_key_storage = true to accept that risk.",
                    ex);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void HardenWindows(string path, bool isDirectory)
    {
        var principal = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var rights = isDirectory ? "(OI)(CI)F" : "F";

        var psi = new ProcessStartInfo("icacls.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("/inheritance:r");
        psi.ArgumentList.Add("/grant:r");
        psi.ArgumentList.Add($"{principal}:{rights}");
        if (isDirectory)
        {
            // Recurse into anything already present; a file created later inherits the directory ACL.
            psi.ArgumentList.Add("/T");
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start icacls.exe");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process already gone.
            }

            throw new IOException($"icacls.exe timed out hardening '{path}'");
        }

        if (process.ExitCode != 0)
        {
            throw new IOException($"icacls.exe exited {process.ExitCode}: {stderr.Trim()} {stdout.Trim()}");
        }
    }

    /// <summary>Makes a best-effort random filename for secret material inside a directory.</summary>
    internal static string RandomFileName(string extension) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant() + extension;
}
