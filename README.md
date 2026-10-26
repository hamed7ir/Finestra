# Finestra

Finestra is a remote connection manager: RDP, SSH, SFTP, FTP and FTPS in one app, with tabs.

I made it mainly for Windows RT (ARM32 tablets like the Surface RT), because there is no good modern
remote desktop client for it. The same build also runs on Windows 10 and 11 (ARM32, x86 and x64).
For RDP it uses FreeRDP 3.32.2 (the SDL3 client), with my changes, built for ARM32 and x64.

## What was tested on real devices

I tested 2.0.0 on a real Windows RT 8.1 device and a Windows 10 ARM32 device. I installed it with the
new setup, and RDP (connect and use), the Touch / Mouse switch, the keyboard and sound all worked.
I did not test SSH and FTP on those devices with 2.0.0.

2.0.1 adds copy and paste of files. I tested it on my PC with a real server, text and files both ways,
but not on the devices yet.

Before that, a build from just before 1.0.3 was tested on the same kind of devices: RDP connect,
connection drop and reconnect, sound, and the layout with the touch keyboard.

If something does not work on your device, please tell me.

## Features

**RDP**
- Sessions run inside Finestra in tabs, or in real fullscreen.
- A Touch / Mouse switch on the tab bar of a connected RDP session. It only shows on a PC with a
  touch screen. A hand means Touch mode, an arrow means Mouse mode. In Touch mode your fingers go to
  the remote PC as real touch, so swipe and pinch work when the remote PC supports it. In Mouse mode
  a tap is a click, a drag is a drag, and press and hold is a right click.
- Clipboard both ways, with files. Copy files on the remote PC, paste them into a folder on your
  device, and they download like a normal copy. It works the other way too. In the connection
  settings, Copy files can be set to one way only, or off.
- Live ping, bandwidth and jitter on the tab bar.
- Pause a session, and Reconnect when it drops (the tab tells you why it dropped).
- Letterbox, smart sizing and dynamic resolution.
- Drag a session out to its own Finestra window (for example on another monitor), and go fullscreen
  on any monitor.

**SSH**
- A real terminal (xterm-256color) with font size, scrollback and colour settings.
- Password or private key login (ed25519, RSA, ECDSA, with passphrase).
- Keepalive, and reconnect without losing the scrollback.

**SFTP, FTP and FTPS**
- Two panes, local and remote. Upload, download, rename, delete, new folder.
- Transfers are written to a temp file first and renamed at the end, so a broken transfer does not
  leave a half file.

**Safety**
- SSH host keys are saved the first time and a changed key is refused.
- FTPS certificates are saved too, and you are asked before accepting a new one.
- Saved passwords are encrypted with Windows (DPAPI). They are never stored as plain text and never
  put on a command line.
- No telemetry. Finestra only connects to the servers you add.

**The app**
- RDP, SSH and FTP tabs in the same window. Drag a tab out to a new window and back.
- Tray icon, start with Windows, dark and light theme.
- All the UI is drawn by Finestra itself, so it looks the same on RT 8.1 and on Windows 11.

## Download and install

Get it from [Releases](https://github.com/hamed7ir/Finestra/releases).

- `Finestra-Setup-<version>.exe`: one file, just run it. It installs for your user only into
  `%LocalAppData%\Programs\Finestra`, with no admin needed. You get a Start menu shortcut and an
  uninstall entry. It runs on Windows RT too (I installed 2.0.0 with it on RT 8.1 and Windows 10
  ARM32).
- `Finestra-<version>-portable.zip`: extract it anywhere and run `Finestra.exe`. Nothing is
  registered. Delete the folder to remove it.

The right RDP engine is picked for your PC: `engine\x64` on 64-bit Windows, `engine\arm` on Windows RT
and ARM32 Windows. I have not tested ARM64 Windows. There is no 32-bit x86 engine in the download. On
32-bit Windows, read the `README.txt` in `engine\x86`. You can copy `wfreerdp.exe` from the
[1.0.2 release](https://github.com/hamed7ir/Finestra/releases/tag/v1.0.2) into that folder (that one
has no Touch mode). SSH and FTP work without it.

## Requirements

- Supported on ARM32: Windows RT 8.0, Windows RT 8.1 and Windows 10 ARM32 (build 15035).
- .NET Framework 4.7 or newer. Windows 10 1703 and later already have it. On Windows RT you need to
  have it installed too. On RT 8.0 that means the .NET Framework 4.8 update, and right now it is hard
  to find for that version.
- Windows RT has to be jailbroken, because RT only runs unsigned desktop apps after a jailbreak.
- The files are not signed, so SmartScreen warns the first time. Click "More info", then "Run anyway".

## Where your data is

An installed Finestra keeps your connections, host keys, certificates and settings in
`Documents\Finestra`. Upgrading or uninstalling does not touch that folder.

The portable zip has a file called `Finestra.portable` next to the exe. Because of that file, the
portable copy keeps everything in its own folder, so it starts with no connections and takes its data
with it. Delete that file if you want it to use `Documents\Finestra` instead.

## Known limitations

- You can't cancel a file transfer yet. It finishes or fails.
- A plain FTP tab only notices a dead connection when you do something in it (SSH and SFTP notice
  right away).
- Finestra uses the system DPI, not per monitor DPI, so a setup with monitors at different scaling
  can look a bit off.
- A very big folder listing (more than about 1,300 items) can hit a size limit in the list.
- An RDP session inside Finestra (in a tab, or in a window you dragged a tab into) always uses one
  monitor. "Use multiple monitors" and "Span across monitors" only work when Settings > Session >
  Connect mode is "Window (single, shell-out)", where the engine opens its own window without tabs.

## Building it yourself

See [BUILD.md](BUILD.md) for building the app. The RDP engines are built from my FreeRDP branch
[finestra-arm32-rt-3.32](https://github.com/hamed7ir/FreeRDP/tree/finestra-arm32-rt-3.32), and its
README explains how. BUILD.md is not updated for the new engine yet.

## License

Finestra is © 2026 Hamed Ghorbani, under the GPL-3.0-only license. See [LICENSE](LICENSE).

It is built on these open source projects. The full license texts are in
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

| Component | License |
|---|---|
| [FreeRDP](https://github.com/FreeRDP/FreeRDP) 3.32.2 and WinPR, changed by me (see [FREERDP-MODIFICATIONS.txt](FREERDP-MODIFICATIONS.txt)). My version: [hamed7ir/FreeRDP](https://github.com/hamed7ir/FreeRDP/tree/finestra-arm32-rt-3.32) | Apache-2.0 |
| OpenSSL and zlib (inside the RDP engine) | Apache-2.0, zlib |
| SDL3 3.2.14 and SDL3_ttf 3.2.2 (inside the RDP engine) | zlib |
| FreeType 2.15.0 (inside the RDP engine) | FreeType License |
| Open Sans (inside the RDP engine, for its dialogs) | SIL OFL 1.1 |
| [SSH.NET](https://github.com/sshnet/SSH.NET) 2026.0.0, BouncyCastle.Cryptography 2.7.0 | MIT |
| [FluentFTP](https://github.com/robinrodricks/FluentFTP) 55.1.0, VtNetCore 1.0.30, Newtonsoft.Json 13.0.4 | MIT |
| Roboto 3.009 (the UI font) | SIL OFL 1.1 |

## Credits

I built Finestra with help from [Claude](https://claude.ai), the AI assistant made by Anthropic.
