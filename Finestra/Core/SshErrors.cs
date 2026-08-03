using System;

namespace Finestra.Core
{
    /// <summary>
    /// FRDP-SSH-AUTH — one place that turns an SSH connect exception into a plain-language message (never a raw
    /// stack). Shared by the standalone <c>SshTerminalForm</c> and the in-shell <c>SshContent</c> so both explain
    /// failures identically.
    /// </summary>
    public static class SshErrors
    {
        public static string Explain(Exception ex, string host)
        {
            var ae = ex as SshAuthException;
            if (ae != null)
            {
                switch (ae.Error)
                {
                    case SshAuthError.HostKeyRejected:
                        return "Host key verification failed.\n\nThe server's key was not trusted, so the connection was cancelled.";
                    case SshAuthError.KeyRejected:
                        return "The server rejected the key.\n\nCheck the username, and that this key's public half is in the server's authorized_keys.";
                    case SshAuthError.PasswordRejected:
                        return "The server rejected the password.\n\nCheck the username and password.";
                    default:   // PuttyPpk / PassphraseRequired / BadPassphrase / UnreadableKey carry a ready message
                        return ae.Message;
                }
            }
            return "Could not connect to " + host + ":\n\n" + ex.Message;
        }
    }
}
