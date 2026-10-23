# Building Finestra

There are two parts: the app (C#, this repo) and the RDP engines (`engine\<arch>\sdl-freerdp.exe`,
FreeRDP 3.32.2's SDL3 client with my changes). You can build each one on its own. This file is also
the practical part of the source offer for the engines.

The release bundles the `x64` and `arm` engines only. The app still supports a 32-bit x86 engine in
`engine\x86\`, but it is not in the download anymore (32-bit x86 Windows is rare). See
`installer\anycpu\engine-x86-README.txt`, which ships in `engine\x86\` instead.

## 1. The app

Keep these exactly as they are. They are what lets one exe run on Windows RT:
- MSBuild from Visual Studio 2017 15.7 or newer, for C# 7.3 (I build with MSBuild 17.14). No NuGet
  restore: every reference is a `HintPath` to a
  DLL in `Finestra\lib\` (plus Newtonsoft.Json from the NuGet global cache).
- .NET Framework 4.7, C# 7.3, AnyCPU with Prefer32Bit. The one MSIL exe runs as a 32-bit process
  everywhere, including jailbroken Windows RT (ARM32). Don't retarget the project.
- All the UI is drawn by the app itself (Windows RT lacks some visual styles APIs). The Roboto font is
  built into the exe and loaded at runtime, nothing needs installing.

```powershell
& "<MSBuild>\MSBuild.exe" Finestra\Finestra.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU
```

The output is `Finestra\bin\Release\Finestra.exe`, its `Finestra.exe.config` (it has the binding
redirects, ship it) and the managed DLLs. Put the engines in `bin\Release\engine\<arch>\sdl-freerdp.exe`
(`x64` and `arm` are required by the packaging script, `x86` is optional) and the app runs in place.

## 2. The RDP engines

The engine source is my FreeRDP branch
[finestra-arm32-rt-3.32](https://github.com/hamed7ir/FreeRDP/tree/finestra-arm32-rt-3.32). It is
upstream tag `3.32.2` plus my changes, one commit each, listed in `FREERDP-MODIFICATIONS.txt`. The
branch README has the full ARM32 build sequence; for x64 it has the pins and options, and you can reuse
the option list from `finestra-rt/port/rt-configure-pins.txt` (leave out the toolchain, `WIN81` and
ARM32 library lines). In short:

- Build target `sdl3-freerdp`. The binary is `client\SDL\SDL3\sdl-freerdp.exe`.
- The main configure options: `WITH_CLIENT_SDL=ON`, `WITH_CLIENT_SDL3=ON`, `WITH_CLIENT_SDL2=OFF`,
  `WITH_SDL_LINK_SHARED=OFF`, `WITH_NATIVE_SSPI=OFF`, `WITH_INTERNAL_RC4=ON`, `WITH_INTERNAL_MD4=ON`,
  `WITH_WINMM=ON`, `WITH_VERBOSE_WINPR_ASSERT=OFF`, static `/MT`, server and sample parts off.
  Windows RT has no usable native SSPI, so sign-in (NLA) uses WinPR's own NTLM, and that needs the
  internal RC4 and MD4 because OpenSSL 3's default provider no longer offers them.
- Static libraries the engine links: OpenSSL 3.5.9, zlib 1.3.2, SDL 3.2.14, FreeType 2.15.0 and
  SDL3_ttf 3.2.2. The branch README lists the exact options for each one.

**x64:** MSVC (`vcvars64.bat`) and Ninja. OpenSSL 3.5.9 built with its `VC-WIN64A` target, zlib from a
vcpkg `x64-windows-static` install. If you use a copied vcpkg tree without `vcpkg.exe`, also pass
`-DVCPKG_APPLOCAL_DEPS=OFF`.

**ARM32 / Windows RT:** a clang-cl cross-compile to `thumbv7-unknown-windows-msvc`. The toolchain
file, the environment script, a small compatibility header, the OpenSSL and zlib build scripts and
their headers are all on the branch under `finestra-rt/`. Start with
`finestra-rt/port/rt-configure-pins.txt`, it has the whole sequence (build the libraries, configure,
build, check). It also turns `WITH_SIMD` off, to avoid NEON alignment faults on Tegra 3. The ARM32
engine needs a patched LLVM, rt2.1 (LLVM 23.1.1), from the
[hamed7ir/llvm-rt1](https://github.com/hamed7ir/llvm-rt1) series, because stock LLVM 18 gets some
ARM32 C++ wrong. rt2.1 is not published yet, rt2 is the newest one there now. The engine build uses
MSVC 14.44's ARM32 libraries and the Windows SDK 10.0.19041.0.

**Check every ARM32 binary before you use it:**
`llvm-readobj --file-headers --coff-imports sdl-freerdp.exe` must show machine type
`IMAGE_FILE_MACHINE_ARMNT (0x1C4)`, no `api-ms-win-*` imports and no `secur32.dll`. Windows RT 8.1
doesn't have the `api-ms-win-*` DLLs, so a binary that needs them does not start at all. A
`secur32.dll` import means the native SSPI path is in use, which this build avoids on purpose.

**x86** is not shipped. You can take `engine\x86\wfreerdp.exe` from the Finestra 1.0.2 download (that
one was built from FreeRDP 3.28.0), or try to build one yourself from the same branch (target
`wfreerdp` for the Windows client, or a 32-bit `sdl3-freerdp`). I have not built any 32-bit engine from
this branch.

## 3. Packaging

```powershell
& installer\anycpu\build.ps1     # it must print:  Setup.exe arch = MSIL
```

It takes `bin\Release` (the app and `engine\`), the license and notice files, and writes to
`release\public\` (or `-OutputRoot <folder>`):
- `Finestra-Setup-<version>.exe`: one self contained installer. It is AnyCPU MSIL, so it runs on
  Windows RT (a normal native installer can't start there). The app files and a small uninstaller
  are inside it.
- `Finestra-<version>-portable.zip`
- `SHA256SUMS.txt`

To test the installer from start to end in scratch folders:
`installer\anycpu\tests\test-installer.ps1 -Setup <the setup exe>`.
