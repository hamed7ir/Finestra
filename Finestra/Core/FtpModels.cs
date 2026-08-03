namespace Finestra.Core
{
    /// <summary>Which file-transfer protocol an <see cref="ConnectionType.Ftp"/> connection speaks. They share a
    /// host/user shape but differ in transport, default port, and auth:
    ///  • <b>Sftp</b> — SSH-for-files: rides the SSH transport (password OR key + passphrase) and reuses the
    ///    <see cref="KnownHosts"/> TOFU store verbatim. Port 22.
    ///  • <b>Ftps</b> — FTP over TLS (<see cref="FtpSettings.TlsMode"/>), server cert verified via the
    ///    <see cref="KnownCerts"/> TOFU store. Port 21 (explicit) / 990 (implicit).
    ///  • <b>Ftp</b> — plain FTP, user/password, no TLS. Port 21.</summary>
    public enum FtpProtocol { Sftp, Ftps, Ftp }

    /// <summary>FTPS TLS negotiation mode → FluentFTP <c>FtpEncryptionMode</c>.</summary>
    public enum FtpTlsMode { Explicit, Implicit, Auto }

    /// <summary>
    /// FRDP-FTP-BUILD-1 — per-connection FTP options, the parallel to <see cref="SshSettings"/>/<see cref="SettingsProfile"/>,
    /// consulted only when <see cref="ConnectionProfile.Type"/> is <see cref="ConnectionType.Ftp"/>. For the
    /// <see cref="FtpProtocol.Sftp"/> variant the auth fields mirror SSH (<see cref="AuthKind"/> + key path; the
    /// password/passphrase rides the shared DPAPI <see cref="ConnectionProfile.PasswordEnc"/>). For FTPS/FTP the
    /// secret is the FTP password (same DPAPI slot).
    /// </summary>
    public sealed class FtpSettings
    {
        public FtpProtocol Protocol { get; set; } = FtpProtocol.Sftp;

        // ── SFTP auth (mirrors SshSettings) ──
        public SshAuthKind AuthKind { get; set; } = SshAuthKind.Password;
        public string PrivateKeyPath { get; set; } = "";

        // ── FTP/FTPS ──
        public FtpTlsMode TlsMode { get; set; } = FtpTlsMode.Explicit;   // only for Ftps
        public bool Passive { get; set; } = true;                        // PASV (usually correct behind NAT)

        /// <summary>The default port for the chosen variant: SFTP 22, FTPS-implicit 990, everything else 21.</summary>
        public int DefaultPort()
        {
            if (Protocol == FtpProtocol.Sftp) return 22;
            if (Protocol == FtpProtocol.Ftps && TlsMode == FtpTlsMode.Implicit) return 990;
            return 21;
        }

        public FtpSettings Clone() => (FtpSettings)MemberwiseClone();   // value types + immutable strings
    }
}
