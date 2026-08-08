Finestra — Remote Connection Manager
===================================
RDP · SSH · SFTP / FTP / FTPS — one themed, tabbed client.
Runs on Windows RT 8.1 (ARM32, jailbroken), Windows 10/11 ARM32, x86 and x64.

Requirements
------------
- .NET Framework 4.7 or later (in-box on Windows 10 1703+; RT 8.1 has 4.5 in-box —
  the app targets 4.7 and runs on the jailbroken RT stack used for testing).
- Windows RT requires a jailbroken device (unsigned desktop apps).
- The binaries are unsigned — SmartScreen will warn on first run (More info → Run anyway).

Running
-------
Extract this folder anywhere and run Finestra.exe. Nothing is registered; delete the
folder to remove it.

Where your data (connections, host keys, certificates, settings) is kept depends on one
file, Finestra.portable, sitting next to Finestra.exe:

  - PRESENT  — this is a portable copy. Everything stays in this folder and nothing
               outside it is read or written, so the copy starts with NO connections and
               any you create travel with the folder. The portable download ships this
               file; read it for the details.
  - ABSENT   — the installed layout. Data lives in Documents\Finestra, shared with every
               other installed copy, and it survives upgrades and uninstalls.

Delete Finestra.portable to switch a portable copy over to the shared Documents location.
Note that "run at Windows startup", if you turn it on, is a Windows setting rather than a
file, so it is the one thing that lives outside the folder either way.

The RDP engine (wfreerdp.exe, a modified FreeRDP 3.28.0 build) is picked automatically to
match your machine: engine\x64 on 64-bit Windows, engine\arm on Windows RT 8.1 and on
ARM32/ARM64 Windows.

32-bit x86 Windows: the x86 engine is NOT bundled — engine\x86\ ships with a README that
explains how to add one (you can copy it out of the Finestra 1.0.2 download, which did
bundle it). Everything except RDP works without it; SSH, SFTP, FTP and FTPS are unaffected.

License
-------
Finestra is © 2026 Hamed Ghorbani, licensed under GPL-3.0-only — see LICENSE.
Third-party components and full license texts: THIRD-PARTY-NOTICES.txt.
FreeRDP modification notice (Apache-2.0 §4(b)): FREERDP-MODIFICATIONS.txt.
Source code: https://github.com/hamed7ir/Finestra
