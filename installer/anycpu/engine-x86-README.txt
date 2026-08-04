Finestra — the 32-bit x86 RDP engine is NOT bundled
===================================================

This folder is intentionally empty of a wfreerdp.exe.

WHY
---
Finestra ships one universal AnyCPU application that runs on Windows RT 8.1 (ARM32),
Windows 10/11 on ARM32/ARM64, and on x86 and x64 Windows. It picks its RDP engine from
the OS architecture:

    64-bit Windows  -> engine\x64\wfreerdp.exe
    ARM64 Windows   -> engine\arm\wfreerdp.exe   (the ARM32 engine, emulated)
    Windows RT 8.1  -> engine\arm\wfreerdp.exe
    32-bit Windows  -> engine\x86\wfreerdp.exe   <- this folder

Note that a 64-bit Windows machine uses the x64 engine even though Finestra itself runs
as a 32-bit process. The x86 engine is therefore reached ONLY by a genuine 32-bit x86
Windows installation, which is now rare. Rather than add ~6 MB to every download for
every user, the x86 engine is left out and this note ships in its place.

Everything else about x86 support is unchanged: the application still detects 32-bit
Windows and still looks in this exact folder. Nothing was removed from the program.

WHAT YOU SEE IF YOU ARE ON 32-BIT WINDOWS
-----------------------------------------
Starting an RDP session reports:

    No RDP engine for this CPU — expected engine\x86\wfreerdp.exe beside the app.

SSH, SFTP, FTP and FTPS are unaffected and work normally. Only RDP needs the engine.

HOW TO ENABLE RDP ON 32-BIT WINDOWS
-----------------------------------
Put a 32-bit build of FreeRDP's Windows client in this folder, named exactly:

    wfreerdp.exe

so that the full path is  <install folder>\engine\x86\wfreerdp.exe . Restart Finestra.
The application verifies the PE machine type before launching it, so a 64-bit or ARM
binary placed here will be refused rather than run.

Two ways to get one:

 1. Take it from an older Finestra release. Finestra 1.0.2 and earlier DID bundle the
    x86 engine. Download the 1.0.2 portable zip or Setup bundle from
    https://github.com/hamed7ir/Finestra/releases , open it, and copy
    engine\x86\wfreerdp.exe out of it into this folder. It is the same engine this
    release would have shipped.

 2. Build it yourself from source:
      source : https://github.com/hamed7ir/FreeRDP   branch  finestra-arm32-rt-3.28
      recipe : build\engine-configs\shipping-x86.cmake.txt in the Finestra source
               distribution — the exact configure line, with the OpenSSL/zlib pins and
               the MSVC quirk that x86 requires, ready to run.

    Any reasonably current upstream FreeRDP 3.x Windows client will also work; the
    branch above is simply the one Finestra's own engines are built from.

Use a binary you built yourself or otherwise trust — it is launched as a child process
with your connection details, so treat it exactly as you would any other executable you
choose to run.

⚠ REINSTALLING OR UPGRADING REMOVES WHAT YOU PUT HERE
-----------------------------------------------------
The installer replaces the program folder wholesale on every install, so a wfreerdp.exe
you place here is deleted when you install a newer version (or reinstall this one).
Keep your copy somewhere outside the install folder and put it back afterwards.

Your saved connections and settings are NOT affected — they live in
Documents\Finestra\ and are never touched by the installer.
