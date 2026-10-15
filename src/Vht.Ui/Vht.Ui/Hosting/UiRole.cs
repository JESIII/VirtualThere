namespace Vht.Ui.Hosting;

/// <summary>
/// Which management window a role executable presents. The role is decided by the
/// executable that was launched - <c>vht-server.exe</c> is the server, <c>vht-client.exe</c>
/// is the client - and never by a command-line switch (docs/UI.md section 1).
/// </summary>
public enum UiRole
{
    Server,
    Client,
}
