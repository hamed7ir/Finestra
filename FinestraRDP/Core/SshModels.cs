using System;

namespace Finestra.Core
{
    /// <summary>
    /// What kind of remote a <see cref="ConnectionProfile"/> targets. <b>Rdp is the default (0)</b> so every
    /// pre-SSH saved connection — whose JSON has no "Type" field — deserializes as RDP. That IS the migration:
    /// absence ⇒ Rdp, no code needed. RDP stays gated on <c>Type == Rdp</c> so nothing about it changes.
    /// Ftp (FRDP-FTP-BUILD-1) is the file-transfer type — SFTP / FTPS / FTP, see <see cref="FtpProtocol"/>.
    /// </summary>
    public enum ConnectionType { Rdp, Ssh, Ftp }

    /// <summary>How an SSH connection authenticates.</summary>
    public enum SshAuthKind { Password, PrivateKey }

    /// <summary>
    /// FRDP-POLISH-2 — terminal appearance preferences, used BOTH per-connection (inside <see cref="SshSettings"/>)
    /// and as the global default template (<see cref="AppSettings.TerminalDefaults"/>) that NEW SSH connections copy
    /// at creation. Editing the global default does NOT touch existing connections (each carries its own copy — the
    /// same pattern as the RDP <see cref="SettingsProfile"/> defaults). <b>Colors default ON</b>; off = client-side
    /// monochrome render (the server still gets <c>xterm-256color</c>, the control just ignores span fg/bg).
    /// </summary>
    public sealed class TerminalPrefs
    {
        public const int MinFont = 8, MaxFont = 28;

        public bool Colors { get; set; } = true;             // false = client-side monochrome render
        public int FontSize { get; set; } = 11;              // point size (clamped MinFont..MaxFont)
        public int ScrollbackLines { get; set; } = 5000;     // → VirtualTerminalController.MaximumHistoryLines

        public int ClampedFont => Math.Max(MinFont, Math.Min(MaxFont, FontSize));
        public int ClampedScrollback => Math.Max(200, Math.Min(200000, ScrollbackLines));

        public TerminalPrefs Clone() => (TerminalPrefs)MemberwiseClone();
    }

    /// <summary>
    /// Per-connection SSH options — the SSH parallel to the RDP <see cref="SettingsProfile"/>. Only consulted when
    /// <see cref="ConnectionProfile.Type"/> is <see cref="ConnectionType.Ssh"/>. The password (password auth) or the
    /// key passphrase (key auth) rides the shared DPAPI <see cref="ConnectionProfile.PasswordEnc"/>; the key FILE
    /// path lives here. Terminal preferences (font size, scrollback depth, colour scheme) come in a later batch.
    /// </summary>
    public sealed class SshSettings
    {
        public SshAuthKind AuthKind { get; set; } = SshAuthKind.Password;
        public string PrivateKeyPath { get; set; } = "";

        /// <summary>Terminal appearance (colors / font size / scrollback). FRDP-POLISH-2. Absent in older JSON ⇒ the
        /// built-in defaults (colors on, 11pt, 5000 lines).</summary>
        public TerminalPrefs Terminal { get; set; } = new TerminalPrefs();

        public SshSettings Clone()
        {
            var c = (SshSettings)MemberwiseClone();
            c.Terminal = Terminal != null ? Terminal.Clone() : new TerminalPrefs();   // deep-clone the sub-object
            return c;
        }
    }
}
