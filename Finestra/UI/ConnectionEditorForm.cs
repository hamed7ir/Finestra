using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Finestra.Core;
using Finestra.Helpers;
using Finestra.UI.Controls;

namespace Finestra.UI
{
    /// <summary>
    /// Add/Edit a <see cref="ConnectionProfile"/>. Now TYPE-AWARE (FRDP-SSH-BUILD-1): a Type row (RDP / SSH)
    /// at the top switches the field set — RDP keeps its identity + Display/Advanced exactly as before; SSH shows
    /// host / port(22) / user / password + private-key path + auth kind. Shared identity/credential rows are
    /// reused. Works on a CLONE, returns it via <see cref="Result"/> only on Save. Password stored DPAPI by the
    /// model. RDP behaviour is untouched (gated on Type==Rdp).
    /// </summary>
    public sealed class ConnectionEditorForm : ThemedDialog
    {
        private static readonly string[] TypeOptions = { "RDP", "SSH", "FTP" };
        private static readonly string[] AuthOptions = { "Password", "Private key" };
        private static readonly string[] FtpProtoOptions = { "SFTP (over SSH)", "FTPS (FTP over TLS)", "FTP (plain)" };
        private static readonly string[] TlsOptions = { "Explicit", "Implicit", "Auto" };

        public ConnectionProfile Result { get; private set; }

        private readonly ConnectionProfile _cp;
        private SettingsProfile _settings;
        private readonly bool _isEdit;
        private ConnectionType _type;

        private readonly ChoiceRow _typeRow;
        private readonly TextRow _name, _host, _port, _user, _domain, _pass;
        private readonly ResolutionPicker _resPicker;   // RDP
        private readonly ChoiceRow _oversize;           // RDP
        private readonly RoundedButton _advanced;       // RDP
        private readonly ChoiceRow _authKind;           // SSH
        private readonly TextRow _keyPath;              // SSH
        private readonly RoundedButton _keyBrowse;      // SSH
        private readonly TextRow _passphrase;           // SSH (key auth) — rides the same DPAPI slot as _pass
        private readonly ToggleRow _termColors;         // SSH terminal prefs (FRDP-POLISH-2)
        private readonly TextRow _fontSize, _scrollback;
        private readonly ChoiceRow _ftpProtocol;        // FTP (FRDP-FTP-BUILD-1)
        private readonly ChoiceRow _tlsMode;            // FTPS
        private readonly ToggleRow _passive;            // FTP/FTPS

        public ConnectionEditorForm(ConnectionProfile existing)
            : base(existing == null ? "New connection" : "Edit connection", 480, 600)
        {
            _isEdit = existing != null;
            _cp = existing != null
                ? CloneOf(existing)
                : new ConnectionProfile { Settings = AppSettings.Instance.Defaults.Clone(), Ssh = new SshSettings { Terminal = AppSettings.Instance.TerminalDefaults.Clone() } };
            _settings = _cp.Settings != null ? _cp.Settings.Clone() : new SettingsProfile();
            _type = _cp.Type;

            _typeRow = new ChoiceRow("Type", TypeOptions, (int)_type) { ValueWidth = 120 };
            _typeRow.Changed += () => OnTypeChanged((ConnectionType)_typeRow.SelectedIndex);

            _name = new TextRow("Name", _cp.Name);
            _host = new TextRow("Host / IP address", _cp.Host);
            _port = new TextRow("Port", _cp.Port > 0 ? _cp.Port.ToString() : _cp.DefaultPort.ToString(), numeric: true);
            _user = new TextRow("Username", _cp.Username);
            _domain = new TextRow("Domain (optional)", _cp.Domain);
            _pass = new TextRow(_isEdit && _cp.HasPassword ? "Password (blank = keep saved)" : "Password", "", password: true);

            // ── RDP-only ──
            _advanced = new RoundedButton { Text = "Advanced settings…", Kind = RoundedButtonKind.Secondary, Height = 42, Font = FontHelper.Ui(10f, FontStyle.Bold) };
            _advanced.Click += (s, e) =>
            {
                using (var f = new SettingsForm(_settings))
                    if (f.ShowDialog(this) == DialogResult.OK && f.Result != null)
                    { _settings = f.Result; _resPicker.Rebind(_settings); _oversize.SelectedIndex = (int)_settings.OversizeMode; }
            };
            _resPicker = new ResolutionPicker(_settings);
            _oversize = new ChoiceRow(OversizeModeUi.Label, OversizeModeUi.Options, (int)_settings.OversizeMode) { ValueWidth = 240 };
            _oversize.Changed += () => _settings.OversizeMode = (OversizeMode)_oversize.SelectedIndex;

            // ── SSH-only ──
            var ssh = _cp.Ssh ?? new SshSettings();
            var ftpInit = _cp.Ftp ?? new FtpSettings();
            int authInit = _cp.Type == ConnectionType.Ftp ? (int)ftpInit.AuthKind : (int)ssh.AuthKind;   // SFTP reuses these auth rows
            _authKind = new ChoiceRow("Authentication", AuthOptions, authInit) { ValueWidth = 160 };
            _authKind.Changed += () => { if (_type == ConnectionType.Ssh || IsSftp) RebuildBody(); };   // swap password ⇄ key fields
            _keyPath = new TextRow("Private key file (OpenSSH format)", _cp.Type == ConnectionType.Ftp ? ftpInit.PrivateKeyPath : ssh.PrivateKeyPath);
            _keyBrowse = new RoundedButton { Text = "Browse…", Kind = RoundedButtonKind.Neutral, Height = 40, Font = FontHelper.Ui(10f, FontStyle.Bold) };
            _keyBrowse.Click += (s, e) =>
            {
                using (var ofd = new OpenFileDialog { Filter = "Private key files|*.*", Title = "Select an OpenSSH private key" })
                {
                    try { if (!string.IsNullOrEmpty(_keyPath.Value) && File.Exists(_keyPath.Value)) ofd.InitialDirectory = Path.GetDirectoryName(_keyPath.Value); } catch { }
                    if (ofd.ShowDialog(this) == DialogResult.OK) _keyPath.Value = ofd.FileName;
                }
            };
            _passphrase = new TextRow(_isEdit && _cp.HasPassword ? "Passphrase (blank = keep saved)" : "Passphrase (blank = prompt at connect)", "", password: true);

            // ── SSH terminal appearance (per-connection; new connections inherit the Settings defaults) ──
            var tp = ssh.Terminal ?? new TerminalPrefs();
            _termColors = new ToggleRow("Terminal colours", tp.Colors);
            _fontSize = new TextRow("Terminal font size (" + TerminalPrefs.MinFont + "–" + TerminalPrefs.MaxFont + ")", tp.FontSize.ToString(), numeric: true);
            _scrollback = new TextRow("Scrollback lines", tp.ScrollbackLines.ToString(), numeric: true);

            // ── FTP-only (FRDP-FTP-BUILD-1) — SFTP reuses the SSH auth rows above; FTPS/FTP use user/password (+TLS) ──
            _ftpProtocol = new ChoiceRow("Protocol", FtpProtoOptions, (int)ftpInit.Protocol) { ValueWidth = 200 };
            _ftpProtocol.Changed += OnFtpProtoChanged;
            _tlsMode = new ChoiceRow("TLS mode", TlsOptions, (int)ftpInit.TlsMode) { ValueWidth = 140 };
            _tlsMode.Changed += () => { if (_type == ConnectionType.Ftp) SyncDefaultPort(); };
            _passive = new ToggleRow("Passive mode (PASV)", ftpInit.Passive);

            RebuildBody();

            var save = AddFooterButton("Save", RoundedButtonKind.Primary, DialogResult.None);
            save.Click += OnSave;
            AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
        }

        private bool IsSftp => _type == ConnectionType.Ftp && _ftpProtocol != null && _ftpProtocol.SelectedIndex == (int)FtpProtocol.Sftp;

        private void OnTypeChanged(ConnectionType t)
        {
            if (t == _type) return;
            _type = t;
            SyncDefaultPort();
            RebuildBody();
        }

        private void OnFtpProtoChanged()
        {
            if (_type != ConnectionType.Ftp) return;
            SyncDefaultPort();
            RebuildBody();
        }

        private int CurrentDefaultPort()
        {
            if (_type == ConnectionType.Ssh) return 22;
            if (_type == ConnectionType.Ftp)
                return new FtpSettings { Protocol = (FtpProtocol)_ftpProtocol.SelectedIndex, TlsMode = (FtpTlsMode)_tlsMode.SelectedIndex }.DefaultPort();
            return 3389;
        }

        /// <summary>Swap the port to the current variant's default if the box still holds a known default (or is blank).</summary>
        private void SyncDefaultPort()
        {
            string p = (_port.Value ?? "").Trim();
            if (p.Length == 0 || p == "3389" || p == "22" || p == "21" || p == "990")
                _port.Value = CurrentDefaultPort().ToString();
        }

        private void RebuildBody()
        {
            Body.Host.Controls.Clear();
            if (_type == ConnectionType.Ssh)
            {
                if (_authKind.SelectedIndex == (int)SshAuthKind.PrivateKey)
                    PopulateBody(
                        new SectionHeader("Connection"), _typeRow, _name, _host, _port, _user,
                        new SectionHeader("SSH"), _authKind, _keyPath, _keyBrowse, _passphrase,
                        new SectionHeader("Terminal"), _termColors, _fontSize, _scrollback);
                else
                    PopulateBody(
                        new SectionHeader("Connection"), _typeRow, _name, _host, _port, _user, _pass,
                        new SectionHeader("SSH"), _authKind,
                        new SectionHeader("Terminal"), _termColors, _fontSize, _scrollback);
            }
            else if (_type == ConnectionType.Ftp)
            {
                if (IsSftp)   // SFTP = SSH auth (reuses the key/password rows + known_hosts)
                {
                    if (_authKind.SelectedIndex == (int)SshAuthKind.PrivateKey)
                        PopulateBody(new SectionHeader("Connection"), _typeRow, _ftpProtocol, _name, _host, _port, _user,
                                     new SectionHeader("Authentication (SSH)"), _authKind, _keyPath, _keyBrowse, _passphrase);
                    else
                        PopulateBody(new SectionHeader("Connection"), _typeRow, _ftpProtocol, _name, _host, _port, _user, _pass,
                                     new SectionHeader("Authentication (SSH)"), _authKind);
                }
                else if (_ftpProtocol.SelectedIndex == (int)FtpProtocol.Ftps)
                    PopulateBody(new SectionHeader("Connection"), _typeRow, _ftpProtocol, _name, _host, _port, _user, _pass,
                                 new SectionHeader("TLS"), _tlsMode, _passive);
                else   // plain FTP
                    PopulateBody(new SectionHeader("Connection"), _typeRow, _ftpProtocol, _name, _host, _port, _user, _pass,
                                 new SectionHeader("Options"), _passive);
            }
            else
                PopulateBody(
                    new SectionHeader("Connection"), _typeRow, _name, _host, _port, _user, _domain, _pass,
                    new SectionHeader("Display"), _resPicker, _oversize,
                    new SectionHeader("Options"), _advanced);
        }

        private void OnSave(object sender, EventArgs e)
        {
            string name = (_name.Value ?? "").Trim();
            string host = (_host.Value ?? "").Trim();
            if (name.Length == 0 || host.Length == 0)
            {
                MessageBox.Show(this, "Name and Host are required.", "Finestra", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int p;
            string portStr = (_port.Value ?? "").Trim();
            bool portOk = int.TryParse(portStr, out p) && p >= 1 && p <= 65535;   // FRDP-FIXSWEEP B8 — validate 1..65535
            if (portStr.Length > 0 && !portOk)
            {
                ConfirmDialog.Info(this, "Port must be a number between 1 and 65535, or left blank for the protocol default.", "Finestra");
                return;
            }
            _cp.Type = _type;
            _cp.Name = name;
            _cp.Host = host;
            _cp.Port = portOk ? p : _cp.DefaultPort;
            _cp.Username = (_user.Value ?? "").Trim();

            // the secret rides one DPAPI slot: it's the password (RDP / SSH+FTP password) or the key passphrase
            // (SSH or SFTP key auth). Blank on edit = keep whatever was saved.
            bool keyAuth = (_type == ConnectionType.Ssh || IsSftp) && _authKind.SelectedIndex == (int)SshAuthKind.PrivateKey;
            string secret = keyAuth ? _passphrase.Value : _pass.Value;
            if (!string.IsNullOrEmpty(secret)) _cp.SetPassword(secret);

            if (_type == ConnectionType.Ssh)
            {
                _cp.Ssh = new SshSettings
                {
                    AuthKind = (SshAuthKind)_authKind.SelectedIndex,
                    PrivateKeyPath = (_keyPath.Value ?? "").Trim(),
                    Terminal = new TerminalPrefs
                    {
                        Colors = _termColors.On,
                        FontSize = ParseInt(_fontSize.Value, 11),
                        ScrollbackLines = ParseInt(_scrollback.Value, 5000)
                    }
                };
            }
            else if (_type == ConnectionType.Ftp)
            {
                _cp.Ftp = new FtpSettings
                {
                    Protocol = (FtpProtocol)_ftpProtocol.SelectedIndex,
                    TlsMode = (FtpTlsMode)_tlsMode.SelectedIndex,
                    Passive = _passive.On,
                    AuthKind = (SshAuthKind)_authKind.SelectedIndex,       // used only by the SFTP variant
                    PrivateKeyPath = (_keyPath.Value ?? "").Trim()
                };
            }
            else
            {
                _cp.Domain = (_domain.Value ?? "").Trim();
                _cp.Settings = _settings;
            }

            Result = _cp;
            DialogResult = DialogResult.OK;
        }

        private static int ParseInt(string s, int def) { int v; return int.TryParse((s ?? "").Trim(), out v) ? v : def; }

        private static ConnectionProfile CloneOf(ConnectionProfile s) => new ConnectionProfile
        {
            Id = s.Id,
            Type = s.Type,
            Name = s.Name,
            Host = s.Host,
            Port = s.Port,
            Username = s.Username,
            Domain = s.Domain,
            PasswordEnc = s.PasswordEnc,
            Settings = s.Settings != null ? s.Settings.Clone() : new SettingsProfile(),
            Ssh = s.Ssh != null ? s.Ssh.Clone() : new SshSettings(),
            Ftp = s.Ftp != null ? s.Ftp.Clone() : new FtpSettings()
        };
    }
}
