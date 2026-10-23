Finestra: there is no 32-bit x86 RDP engine in this folder
==========================================================

This folder is empty on purpose.

Why
---
Finestra is one app for Windows RT (ARM32), ARM Windows, and x86 and x64 Windows. It picks the RDP
engine from your Windows version:

    64-bit Windows  -> engine\x64\sdl-freerdp.exe
    ARM64 Windows   -> engine\arm\sdl-freerdp.exe   (the ARM32 engine, not tested on ARM64)
    Windows RT      -> engine\arm\sdl-freerdp.exe
    32-bit Windows  -> engine\x86\                 (this folder)

A 64-bit Windows PC uses the x64 engine, even though Finestra itself runs as a 32-bit app. So this
folder is only used on a real 32-bit x86 Windows, and that is rare now. I didn't want to make every
download about 3 MB bigger for that (the old x86 engine is 6 MB unpacked), so I left it out and put
this note here instead.

The app still supports 32-bit Windows and still looks in this folder. Nothing was removed from the
program.

What you see on 32-bit Windows
------------------------------
When you start an RDP session you get:

    No RDP engine for this CPU: expected engine\x86\sdl-freerdp.exe beside the app.

SSH, SFTP, FTP and FTPS work normally. Only RDP needs the engine.

How to get RDP working on 32-bit Windows
----------------------------------------
Put a 32-bit FreeRDP engine in this folder and restart Finestra. Its name can be:

    wfreerdp.exe      (FreeRDP's Windows client)
    sdl-freerdp.exe   (FreeRDP's SDL3 client, if you build a 32-bit one yourself)

Only my SDL3 engine has the Touch / Mouse switch. Finestra checks the file before it runs it, so a
64-bit or ARM file put here is refused.

Two ways to get one:

 1. Copy it from Finestra 1.0.2. Versions 1.0.2 and older had the x86 engine inside. Download the
    1.0.2 portable zip from https://github.com/hamed7ir/Finestra/releases/tag/v1.0.2 , open it, and
    copy engine\x86\wfreerdp.exe into this folder. It is an older FreeRDP (3.28.0) with my changes
    for the Windows client, and Finestra uses it the same way. It has no Touch mode.

 2. Build it yourself. My FreeRDP source is at https://github.com/hamed7ir/FreeRDP , branch
    finestra-arm32-rt-3.32.

Only use a file you built yourself or trust. Finestra runs it with your connection details, so treat
it like any other program you run.

What the installer does with it
-------------------------------
The installer only replaces and removes the files it installed itself, and an engine you put here is
not one of them. So it stays when you install a newer version, and it also stays when you uninstall
(together with this folder, which you can delete yourself). It is still a good idea to keep a copy
somewhere else.

Your connections and settings are in Documents\Finestra and the installer never touches them.
