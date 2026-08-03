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

    /// <summary>FIN-RDPECAM — camera capture resolution offered to the server (x64-only rdpecam/MF backend).
    /// LOWER resolution = smoother outgoing video: realtime H.264 encode of 1080p plus the RDP upload
    /// stutters, so the default is 720p. Enum order == the ChoiceRow index (HD720 first = default).</summary>
    public enum CameraResOpt { HD720, FHD1080, VGA480 }

    /// <summary>UI labels + pixel dimensions for <see cref="CameraResOpt"/> (parallels OversizeModeUi).</summary>
    public static class CameraResUi
    {
        public static readonly string[] Options = { "720p (1280×720)", "1080p (1920×1080)", "480p (640×480)" };

        public static void Dimensions(CameraResOpt r, out int width, out int height)
        {
            switch (r)
            {
                case CameraResOpt.FHD1080: width = 1920; height = 1080; break;
                case CameraResOpt.VGA480:  width = 640;  height = 480;  break;
                default:                   width = 1280; height = 720;  break; // HD720 (default)
            }
        }
    }

    /// <summary>FIN-RDPECAM — camera capture FRAME-RATE cap (x64-only rdpecam/MF backend). A cheaper stutter
    /// lever than resolution when you want to keep 720p sharpness: halving fps halves the realtime H.264
    /// encode + upload load. Default = no cap (let the server pick, usually 30). Enum order == ChoiceRow
    /// index (Default first). The backend filters its offered media types to ≤ this fps; if the camera has
    /// no matching lower-fps type it falls back gracefully (the smallest-size type is always kept).</summary>
    /// <summary>FIN-RDPECAM-HWENC — camera capture FRAME-RATE (x64 rdpecam/MF). HONEST CLAMP: rates above 30
    /// require the SENSOR to actually capture at that rate — most laptop cameras top out at 30 (often only at
    /// lower resolutions). The backend caps its offered media types to ≤ this value and the server negotiates
    /// the highest the sensor really provides, so asking 120 on a 30-fps sensor simply yields 30.</summary>
    public enum CameraFpsOpt { Fps30, Fps60, Fps90, Fps120, Custom }

    /// <summary>UI labels + numeric fps for <see cref="CameraFpsOpt"/> (parallels CameraResUi).</summary>
    public static class CameraFpsUi
    {
        public static readonly string[] Options =
            { "30 fps", "60 fps (sensor permitting)", "90 fps (sensor permitting)", "120 fps (sensor permitting)", "Custom…" };

        /// <summary>Numeric fps for the /camera arg. Custom uses <paramref name="custom"/> (accepted 1..240;
        /// out-of-range falls back to 30).</summary>
        public static int Value(CameraFpsOpt f, int custom)
        {
            switch (f)
            {
                case CameraFpsOpt.Fps60:  return 60;
                case CameraFpsOpt.Fps90:  return 90;
                case CameraFpsOpt.Fps120: return 120;
                case CameraFpsOpt.Custom: return (custom >= 1 && custom <= 240) ? custom : 30;
                default:                  return 30; // Fps30
            }
        }
    }

    /// <summary>FIN-RDPECAM-HWENC — which H.264 encoder the x64 engine uses for the camera. Auto/HardwareMF
    /// both PREFER the GPU hardware encoder and fall back to software if it can't init (a call never fails for
    /// lack of HW); Software forces openh264. Emitted as the FREERDP_H264_ENCODER env var, x64-gated.</summary>
    public enum CameraEncoderOpt { Auto, HardwareMF, Software }

    /// <summary>UI labels + env value for <see cref="CameraEncoderOpt"/>. The engine treats "sw" as force-software
    /// and anything else as prefer-hardware, so HardwareMF and Auto behave identically (honest: HW can't be forced
    /// without risking a failed call — it always falls back).</summary>
    public static class CameraEncoderUi
    {
        public static readonly string[] Options = { "Auto (prefer GPU)", "Hardware (GPU)", "Software (openh264)" };

        public static string EnvValue(CameraEncoderOpt e)
        {
            switch (e)
            {
                case CameraEncoderOpt.Software:   return "sw";
                case CameraEncoderOpt.HardwareMF: return "hw";
                default:                          return "auto"; // Auto
            }
        }
    }

    /// <summary>FIN-RDPECAM-QUALITY — camera H.264 target bitrate (x64 rdpecam/MF). Auto scales with the
    /// negotiated resolution (raised well above the stock webcam table — sharper, not blocky); the fixed tiers
    /// let the user trade quality against uplink (higher = sharper but more upload). Passed as the
    /// FREERDP_H264_BITRATE env var (kbps); Auto sends nothing so the engine uses its resolution-scaled default.</summary>
    public enum CameraBitrateOpt { Auto, Low, Medium, High, Max }

    /// <summary>UI labels + kbps for <see cref="CameraBitrateOpt"/> (parallels CameraEncoderUi). 0 == Auto.</summary>
    public static class CameraBitrateUi
    {
        public static readonly string[] Options =
            { "Auto (match resolution)", "Low (~1 Mbps)", "Medium (~2 Mbps)", "High (~4 Mbps)", "Max (~8 Mbps)" };

        /// <summary>Target bitrate in kbps for the FREERDP_H264_BITRATE env var; 0 = Auto (engine scales by res).</summary>
        public static int Kbps(CameraBitrateOpt b)
        {
            switch (b)
            {
                case CameraBitrateOpt.Low:    return 1000;
                case CameraBitrateOpt.Medium: return 2000;
                case CameraBitrateOpt.High:   return 4000;
                case CameraBitrateOpt.Max:    return 8000;
                default:                      return 0;   // Auto
            }
        }
    }

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
        // FIN-AV-REDIR — redirect the LOCAL microphone to the server (audio-input via the audin+winmm
        // capture channel). x64-ONLY by design: the flag is emitted only when the engine is x64 (see
        // RdpLauncher.BuildTokens) and the toggle is only shown on x64 (SettingsForm). Off by default.
        public bool Microphone { get; set; } = false;            // /microphone:sys:winmm when true (x64 only)
        // FIN-RDPECAM — redirect the LOCAL camera to the server (x64-only MF rdpecam backend). Emits
        // /camera with the chosen resolution cap; x64-gated in the UI and in RdpLauncher.BuildTokens.
        public bool Camera { get; set; } = false;                // /camera:width:..,height:.. when true (x64 only)
        public CameraResOpt CameraResolution { get; set; } = CameraResOpt.HD720;  // offered resolution cap (720p default)
        public CameraFpsOpt CameraFps { get; set; } = CameraFpsOpt.Fps30;         // offered fps (30 default; sensor-clamped)
        public int CameraFpsCustom { get; set; } = 30;                            // used when CameraFps == Custom
        public CameraEncoderOpt CameraEncoder { get; set; } = CameraEncoderOpt.Auto; // GPU-hardware vs openh264 (x64)
        public CameraBitrateOpt CameraBitrate { get; set; } = CameraBitrateOpt.Auto; // target bitrate/quality (x64); Auto scales w/ resolution

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
