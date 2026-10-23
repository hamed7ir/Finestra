Finestra, a remote connection manager
=====================================
RDP, SSH, SFTP, FTP and FTPS in one app, with tabs.
It runs on Windows RT 8.0 and 8.1 (ARM32, jailbroken), Windows 10 ARM32, and Windows 10 and 11 on
x86 and x64.

Requirements
------------
- .NET Framework 4.7 or newer. Windows 10 1703 and later already have it. On Windows RT you need to
  have it installed too. On RT 8.0 that means the .NET Framework 4.8 update.
- Windows RT has to be jailbroken, because RT only runs unsigned desktop apps after a jailbreak.
- The files are not signed, so SmartScreen warns the first time. Click "More info", then "Run anyway".

Installed or portable
---------------------
If you installed Finestra with the setup, uninstall it from Settings > Apps (Windows 10 and 11) or
Control Panel > Programs and Features (Windows RT 8.x), or run uninstall.exe in this folder. Your
connections and settings are in Documents\Finestra and uninstall keeps them.

If this is the portable zip, there is a file called Finestra.portable next to Finestra.exe. Because of
that file, Finestra keeps everything (connections, host keys, certificates, settings and the log) in
this folder and nothing outside it. So it starts with no connections, and your data moves with the
folder. Delete the folder to remove it. Delete just Finestra.portable if you want this copy to use
Documents\Finestra instead.
"Start with Windows" is a Windows setting, not a file, so if you turn it on, that one thing lives
outside the folder.

RDP engine
----------
The RDP engine is sdl-freerdp.exe: FreeRDP 3.32.2, its SDL3 client, with my changes. Finestra picks the
right one for your PC: engine\x64 on 64-bit Windows, engine\arm on Windows RT and ARM32 Windows.
I have not tested ARM64 Windows.

There is no 32-bit x86 engine here. On 32-bit Windows read engine\x86\README.txt. SSH, SFTP, FTP and
FTPS work without it.

Touch screens
-------------
On a PC with a touch screen, a connected RDP session has a Touch / Mouse button on its tab bar (a hand
means Touch mode, an arrow means Mouse mode). In Touch mode your fingers go to the remote PC as real
touch, so swipe and pinch work when the remote PC supports it. In Mouse mode a tap is a click, a drag
is a drag, and press and hold is a right click. Settings > Session > "Touch screen in RDP starts as"
picks how new sessions start, and pressing the button changes that setting too.

License
-------
Finestra is (c) 2026 Hamed Ghorbani, under the GPL-3.0-only license. See LICENSE.
The other projects it uses and their full licenses: THIRD-PARTY-NOTICES.txt.
What I changed in FreeRDP (Apache-2.0 section 4(b)): FREERDP-MODIFICATIONS.txt.
Source code: https://github.com/hamed7ir/Finestra
