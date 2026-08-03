using System;
using Newtonsoft.Json;

namespace Finestra.Core
{
    // ── value-option enums ─────────────────────────────────────────────────────
    // Every value-option has a "Default" member that emits NO flag, so wfreerdp keeps its own
    // internal default and the command line stays minimal. Only explicit non-Default choices are emitted.

    /// <summary>/network:&lt;type&gt; — connection type. Default emits nothing.</summary>
    public enum NetworkOpt { Default, Modem, BroadbandLow, Broadband, BroadbandHigh, Wan, Lan, Auto }

    /// <summary>/audio-mode:&lt;n&gt; — 0=redirect(play locally), 1=server(play on remote), 2=none. Default emits nothing.</summary>
    public enum AudioOpt { Default, PlayLocal, PlayRemote, None }

    /// <summary>/bpp:&lt;depth&gt; — session color depth. Default emits nothing.</summary>
    public enum ColorDepthOpt { Default, Bpp32, Bpp24, Bpp16, Bpp15, Bpp8 }

    /// <summary>/sec:… — force a security protocol. Default emits nothing (wfreerdp negotiates).</summary>
    public enum SecurityOpt { Default, ForceNla, NlaOff, TlsOnly, RdpOnly }

    /// <summary>/compression-level:&lt;0|1|2&gt;. Default emits nothing.</summary>
    public enum CompressionLevelOpt { Default, Level0, Level1, Level2 }

    /// <summary>/gfx:… — RDP8 graphics pipeline codec. Default emits nothing (auto-negotiate).</summary>
    public enum GfxOpt { Default, Avc444, Avc420, Rfx, Progressive }

    /// <summary>Resolution mode. Native = resolve the device's PHYSICAL resolution live at connect (a mode, not a
    /// stored number — the same profile resolves differently on RT vs desktop); Preset = a chosen aspect-ratio
    /// resolution; Custom = user-entered W/H. Portrait swaps W↔H at emit.</summary>
    public enum ResolutionMode { Native, Preset, Custom }

    /// <summary>
    /// What happens when the host window is LARGER than the session (the resolution is frozen at connect).
    /// An EXPLICIT user choice — never inferred — because two of the three silently override the resolution the
    /// user picked. wfreerdp treats the two flags as mutually exclusive (client/common/cmdline.c:2915), so this
    /// is a 3-way enum rather than two independent bools.
    ///  • <b>Letterbox</b> (default) — emit neither flag. The framebuffer keeps its exact size; black bars appear
    ///    when the window is larger, scroll bars when it is smaller. The chosen resolution is NEVER touched.
    ///  • <b>SmartSizing</b> — <c>/smart-sizing</c>. wfreerdp scales the picture to the window (may look soft).
    ///  • <b>Dynamic</b> — <c>+dynamic-resolution</c>. The remote desktop is RE-NEGOTIATED to the window size via
    ///    the disp channel, so the resolution the user chose is replaced as they resize.
    /// </summary>
    public enum OversizeMode { Letterbox, SmartSizing, Dynamic }

    /// <summary>Display strings for <see cref="OversizeMode"/> — SAME ORDER as the enum, so index == (int)value.
    /// Shared by the connection editor and the Defaults page so the two can never drift apart.</summary>
    public static class OversizeModeUi
    {
        public const string Label = "If the window is larger";
        public static readonly string[] Options =
        {
            "Keep exact size (black bars)",
            "Stretch to fill (may look soft)",
            "Resize session (changes resolution)"
        };
    }

    /// <summary>
    /// The per-connection RDP options that map 1:1 to real wfreerdp flags (see port\wfreerdp-help.txt).
    /// Toggle DEFAULTS below equal wfreerdp's OWN defaults (decoded from the help's +/- convention), so the
    /// launcher (RdpLauncher) emits a flag ONLY when the value differs from wfreerdp's default — a minimal,
    /// non-overriding command line. Value-options use the "Default" enum member = emit nothing.
    /// The ONE curated smart default is the display resolution (detected primary), applied at launch.
    /// </summary>
    public sealed class SettingsProfile
    {
        // ── Display — resolution (edited via ResolutionPicker) ──
        public ResolutionMode ResolutionMode { get; set; } = ResolutionMode.Native;   // Native / Preset / Custom
        public int Width { get; set; } = 0;                      // landscape W for Preset/Custom (ignored in Native)
        public int Height { get; set; } = 0;                     // landscape H for Preset/Custom (ignored in Native)
        public bool Portrait { get; set; } = false;             // swap W↔H at emit (Preset/Custom)
        public bool UseCustomResolution { get; set; } = false;   // DEPRECATED — retained only for one-time migration
        public bool Fullscreen { get; set; } = false;            // +f (embed: picks fullscreen vs windowed host)
        public bool MultiMon { get; set; } = false;              // /multimon
        public bool Span { get; set; } = false;                  // +span
        public ColorDepthOpt ColorDepth { get; set; } = ColorDepthOpt.Default;   // /bpp:

        /// <summary>Explicit oversize behaviour. Default Letterbox = emit nothing = never override the chosen
        /// resolution. Absorbs the old SmartSizing / DynamicResolution bools (see <see cref="MigrateOversize"/>).</summary>
        public OversizeMode OversizeMode { get; set; } = OversizeMode.Letterbox;

        public bool SmartSizing { get; set; } = false;           // DEPRECATED — retained only for one-time migration
        public bool DynamicResolution { get; set; } = false;     // DEPRECATED — retained only for one-time migration

        // ── Experience ── (wfreerdp defaults: wallpaper/themes/fonts/compression ON; the rest OFF)
        public bool Wallpaper { get; set; } = true;              // -wallpaper when false
        public bool Themes { get; set; } = true;                 // -themes when false
        public bool FontSmoothing { get; set; } = true;          // -fonts when false (help: "-fonts Disable smooth fonts")
        public bool MenuAnimations { get; set; } = false;        // +menu-anims when true
        public bool WindowDrag { get; set; } = false;            // +window-drag when true
        public bool Aero { get; set; } = false;                  // +aero when true
        public bool Compression { get; set; } = true;            // -compression when false
        public CompressionLevelOpt CompressionLevel { get; set; } = CompressionLevelOpt.Default;   // /compression-level:
        public NetworkOpt Network { get; set; } = NetworkOpt.Default;   // /network:
        public GfxOpt Gfx { get; set; } = GfxOpt.Default;        // /gfx:

        // ── Connection ──
        public int TimeoutMs { get; set; } = 0;                  // /timeout: (0 = unset)
        public bool AutoReconnect { get; set; } = false;         // +auto-reconnect
        public bool TrustCertificate { get; set; } = false;      // /cert:ignore (off by default = secure)
        public string GatewayHost { get; set; } = "";            // /gateway:g:<host>

        // ── Local resources ──
        public AudioOpt Audio { get; set; } = AudioOpt.Default;  // /audio-mode:
        public bool Clipboard { get; set; } = true;              // -clipboard when false
        public bool Drives { get; set; } = false;                // +drives when true
        public bool Printer { get; set; } = false;               // /printer (default printer) when true

        // ── Security ──
        public SecurityOpt Security { get; set; } = SecurityOpt.Default;   // /sec:

        public SettingsProfile Clone()
        {
            return (SettingsProfile)MemberwiseClone();   // all fields are value types / immutable strings
        }

        /// <summary>One-time migration of pre-picker saved connections (which only had UseCustomResolution + W/H):
        /// UseCustomResolution=true → Custom mode (keeping its W/H); false → Native (the old "detected primary"
        /// behavior). Idempotent — clears the flag, so re-running is a no-op. No data loss.</summary>
        public void MigrateResolution()
        {
            if (UseCustomResolution) { ResolutionMode = ResolutionMode.Custom; UseCustomResolution = false; }
        }

        /// <summary>One-time migration of the pre-FRDP-FILL bools into <see cref="OversizeMode"/>:
        /// DynamicResolution=true → Dynamic, SmartSizing=true → SmartSizing, both off → Letterbox. Dynamic wins if
        /// somehow both were set (wfreerdp rejects that combination outright, so such a profile never connected).
        /// Idempotent — it clears the bools, so re-running is a no-op and a later switch back to Letterbox sticks.</summary>
        public void MigrateOversize()
        {
            if (OversizeMode == OversizeMode.Letterbox)
            {
                if (DynamicResolution) OversizeMode = OversizeMode.Dynamic;
                else if (SmartSizing) OversizeMode = OversizeMode.SmartSizing;
            }
            SmartSizing = false;
            DynamicResolution = false;
        }
    }

    /// <summary>
    /// A saved RDP connection: identity + target + credentials + its own <see cref="SettingsProfile"/>.
    /// The password is stored DPAPI-protected (base64) via <see cref="Secret"/> — NEVER in plaintext, and the
    /// blob is CurrentUser-scoped so it cannot be decrypted by another user or on another machine.
    /// </summary>
    public sealed class ConnectionProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>RDP (default) or SSH. Absence in older JSON ⇒ Rdp — see <see cref="ConnectionType"/>.</summary>
        public ConnectionType Type { get; set; } = ConnectionType.Rdp;

        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; } = 3389;
        public string Username { get; set; } = "";
        public string Domain { get; set; } = "";

        /// <summary>DPAPI-protected, base64. Set via <see cref="SetPassword"/>; read via <see cref="GetPassword"/>.</summary>
        public string PasswordEnc { get; set; } = "";

        /// <summary>RDP flags — only used when <see cref="Type"/> is Rdp.</summary>
        public SettingsProfile Settings { get; set; } = new SettingsProfile();

        /// <summary>SSH options — only used when <see cref="Type"/> is Ssh.</summary>
        public SshSettings Ssh { get; set; } = new SshSettings();

        /// <summary>FTP/FTPS/SFTP options — only used when <see cref="Type"/> is Ftp (FRDP-FTP-BUILD-1).</summary>
        public FtpSettings Ftp { get; set; } = new FtpSettings();

        /// <summary>The protocol's default port (RDP 3389 / SSH 22 / FTP per-variant) — the port the editor
        /// pre-fills and <see cref="DisplayTarget"/> hides.</summary>
        [JsonIgnore] public int DefaultPort =>
            Type == ConnectionType.Ssh ? 22 :
            Type == ConnectionType.Ftp ? (Ftp ?? new FtpSettings()).DefaultPort() : 3389;

        [JsonIgnore] public bool HasPassword => !string.IsNullOrEmpty(PasswordEnc);

        /// <summary>Encrypts + stores the password (empty clears it). Plaintext never persists.</summary>
        public void SetPassword(string plain) => PasswordEnc = string.IsNullOrEmpty(plain) ? "" : Secret.Protect(plain);

        /// <summary>Decrypts the stored password for launch (empty if none / undecryptable).</summary>
        public string GetPassword() => Secret.Unprotect(PasswordEnc);

        /// <summary>FRDP-POLISH-4 — a fully independent copy (new Id, nested Settings/Ssh/Ftp objects NOT shared
        /// with the original) for the manager's "Duplicate" action. A field-by-field MemberwiseClone would leave
        /// the nested settings objects shared by reference — editing the duplicate would silently mutate the
        /// original too. JSON round-trip via the SAME serializer this app already persists profiles with is the
        /// simplest way to deep-clone every field correctly without hand-maintaining a field list here. PasswordEnc
        /// carries over as-is and still decrypts fine — DPAPI protection here is user-scoped, not tied to Id.</summary>
        public ConnectionProfile Clone()
        {
            var dup = JsonConvert.DeserializeObject<ConnectionProfile>(JsonConvert.SerializeObject(this));
            dup.Id = Guid.NewGuid().ToString("N");
            return dup;
        }

        [JsonIgnore]
        public string DisplayTarget
        {
            get
            {
                string hp = (Port == DefaultPort || Port <= 0) ? Host : Host + ":" + Port;
                string u = string.IsNullOrEmpty(Username) ? "" :
                    (string.IsNullOrEmpty(Domain) ? Username : Domain + "\\" + Username) + "@";
                return u + hp;
            }
        }
    }
}
