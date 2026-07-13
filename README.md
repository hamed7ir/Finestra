# Finestra

**Remote Connection Manager** — RDP · SSH · SFTP / FTP / FTPS in one themed, tabbed client.

Finestra's flagship target is **Windows RT 8.1 on ARM32** (jailbroken Surface RT–class devices),
where no modern remote-desktop client exists — and it runs equally on **Windows 10/11 ARM32,
x86 and x64** from one universal AnyCPU build. The RDP engine is a ported, modified
**FreeRDP 3.28.0** cross-compiled for all three architectures.

<p align="center">
  <img src="docs/manager.png" width="640" alt="Connection manager"><br>
  <img src="docs/session-tabs.png" width="640" alt="Tabbed sessions"><br>
  <img src="docs/about.png" width="320" alt="About">
</p>

## Features

- **RDP** — embedded FreeRDP sessions (tabbed or true fullscreen), live RTT/bandwidth/jitter
  readout, pause/resume, Letterbox / SmartSizing / Dynamic-resolution oversize modes,
  multi-monitor tear-off, per-monitor fullscreen.
- **SSH** — owner-drawn xterm-256color terminal (VtNetCore), live font-size/scrollback/colour
  preferences, drag-selection copy, password + private-key auth (ed25519 / RSA / ECDSA,
  passphrase supported), keepalive, manual reconnect with scrollback preserved.
- **SFTP / FTP / FTPS** — dual-pane file browser (local ⇄ remote), crash-safe temp+rename
  transfers, upload/download/rename/delete/mkdir, per-session reconnect.
- **Security posture** — host-key TOFU pinning (`known_hosts.json`, fail-closed on changed
  keys), FTPS certificate pinning with accept-once, DPAPI-encrypted saved passwords (never
  plaintext, never on a command line), no telemetry: the app makes **no network calls except
  to the servers you configure**.
- **One shell** — mixed RDP/SSH/FTP tabs in one window, drag tabs out to new windows and back,
  per-type toolbars, system tray, run-on-startup, dark/light theme following the Windows accent,
  fully owner-drawn UI (renders identically on RT 8.1 and Windows 11).

## Install

Grab a release from [Releases](https://github.com/hamed7ir/Finestra/releases):

| Artifact | Use |
|---|---|
| `Finestra-Setup-<ver>.zip` | Extract, run `Setup.exe` — per-user install (`%LocalAppData%\Programs\Finestra`), **no admin**, Start-menu shortcut, uninstall entry. The installer is AnyCPU/MSIL so it **runs on Windows RT** too. |
| `Finestra-<ver>-portable.zip` | Extract anywhere, run `Finestra.exe`. Nothing registered — delete the folder to remove. |

One bundle covers all architectures — the right `engine\{x64,x86,arm}\wfreerdp.exe` is picked
automatically.

**Requirements**
- .NET Framework **4.7+** (in-box on Windows 10 1703+).
- **Windows RT 8.1**: a jailbroken device (RT only runs unsigned desktop apps after jailbreak).
- Binaries are **unsigned** — SmartScreen will warn on first run (*More info → Run anyway*).

Your data (connections, host keys, certificates, settings) lives in **`Documents\Finestra\`** —
it survives upgrades and uninstalls.

## Known limitations (1.0)

- No transfer **cancel** yet — a running file transfer completes or fails (queued ops wait).
- Plain-FTP drop detection is **op-driven**: an idle FTP tab discovers a dead link on the next
  operation (SSH/SFTP detect drops immediately).
- System-DPI aware (not per-monitor) — mixed-DPI multi-monitor setups may scale imperfectly.
- Very large directory listings (>~1,300 rows) can hit an owner-drawn list height limit.

## Building

See [BUILD.md](BUILD.md) — including how to rebuild the modified FreeRDP engines for all three
architectures (the practical half of the GPL corresponding-source offer).

## License & credits

Finestra is **© 2026 Hamed Ghorbani**, licensed **GPL-3.0-only** — see [LICENSE](LICENSE).

Built on excellent open source — full texts in
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt):

| Component | License |
|---|---|
| [FreeRDP](https://github.com/FreeRDP/FreeRDP) 3.28.0 + WinPR (**modified** — see [FREERDP-MODIFICATIONS.txt](FREERDP-MODIFICATIONS.txt)) | Apache-2.0 |
| OpenSSL · zlib (statically linked in the engine) | Apache-2.0 · zlib |
| [SSH.NET](https://github.com/sshnet/SSH.NET) 2025.1.0 · BouncyCastle.Cryptography 2.6.2 | MIT |
| [FluentFTP](https://github.com/robinrodricks/FluentFTP) 54.2.0 · VtNetCore 1.0.30 · Newtonsoft.Json 13.0.4 | MIT |
| Roboto 3.009 (bundled UI font) | SIL OFL 1.1 |
