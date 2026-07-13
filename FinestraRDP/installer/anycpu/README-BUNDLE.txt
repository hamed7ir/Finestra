Finestra 1.0.0 — Remote Connection Manager
===========================================
RDP · SSH · SFTP / FTP / FTPS — one themed, tabbed client.
Runs on Windows RT 8.1 (ARM32, jailbroken), Windows 10/11 ARM32, x86 and x64.

Requirements
------------
- .NET Framework 4.7 or later (in-box on Windows 10 1703+; RT 8.1 has 4.5 in-box —
  the app targets 4.7 and runs on the jailbroken RT stack used for testing).
- Windows RT requires a jailbroken device (unsigned desktop apps).
- The binaries are unsigned — SmartScreen will warn on first run (More info → Run anyway).

Running (portable)
------------------
Extract this folder anywhere and run Finestra.exe. Nothing is registered; delete the
folder to remove it. Your data (connections, host keys, certificates, settings) lives
in Documents\Finestra — never beside the exe.

The RDP engine (wfreerdp.exe, a modified FreeRDP 3.28.0 build) is picked automatically
from engine\x64, engine\x86 or engine\arm to match your machine.

License
-------
Finestra is © 2026 Hamed Ghorbani, licensed under GPL-3.0-only — see LICENSE.
Third-party components and full license texts: THIRD-PARTY-NOTICES.txt.
FreeRDP modification notice (Apache-2.0 §4(b)): FREERDP-MODIFICATIONS.txt.
Source code: https://github.com/hamed7ir/Finestra
