# Building Finestra

Two parts: the **managed app** (C#, this repo) and the **native FreeRDP engines**
(`engine\{x64,x86,arm}\wfreerdp.exe`, built from a modified FreeRDP 3.28.0 tree). You can rebuild
either independently — this document is also the practical half of the GPL-3.0 corresponding-source
obligation for the engines.

## 1. The managed app

**Toolchain constraints (deliberate — do not "upgrade"):**
- Visual Studio 2017 MSBuild (15.x) — no NuGet restore is used; all references are `HintPath`s
  to DLLs staged in `FinestraRDP\lib\` (+ Newtonsoft.Json from the NuGet global cache).
- **.NET Framework 4.7**, **C# 7.3**, **AnyCPU + Prefer32Bit**. This exact combination is what
  runs on jailbroken Windows RT 8.1 (ARM32): the one MSIL exe runs as 32-bit everywhere.
  Never retarget the csproj.
- The UI is fully owner-drawn (no visual-styles APIs that RT lacks). The bundled Roboto font is
  private-loaded at runtime — nothing needs installing.

```powershell
& "<MSBuild15>\MSBuild.exe" FinestraRDP\Finestra.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU
```

Output: `FinestraRDP\bin\Release\Finestra.exe` (+ `Finestra.exe.config` with the auto-generated
binding redirects — ship it) + the managed DLL closure. Stage the engines under
`bin\Release\engine\{x64,x86,arm}\wfreerdp.exe` and the app is runnable in place.

## 2. The FreeRDP engines

Base: **FreeRDP tag `3.28.0`** with the modifications recorded as patch files (apply them to a
clean checkout):

| Patch | What it does |
|---|---|
| `winpr-sspi-package-index.patch` | WinPR internal-SSPI dispatch fix (mixed-width package-name upper-pointer → package index). Without it, UNICODE builds with `WITH_NATIVE_SSPI=OFF` fail NLA with `SEC_E_SECPKG_NOT_FOUND`. |
| `wfclient-parent-window-stats-pipe.patch` | Windows client: `/parent-window` embed fixes + the per-PID stats/control named pipe (`\\.\pipe\finestrardp.<pid>`, live RTT/bandwidth + PAUSE/RESUME). The pipe name is a compiled contract with the app's `StatsPipe.cs`. |
| `build-config.patch` / `win81-winver.patch` | Root CMakeLists: adds the `WIN81` case (`WINVER/_WIN32_WINNT=0x0603`). |

*(In the development workspace these live in a `port\` folder beside the FreeRDP checkout; in the
public repo they ship under `patches/`.)*

### Common configure flags (all three architectures)

```
-G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF
-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded -DOPENSSL_USE_STATIC_LIBS=ON
-DWITH_CLIENT_SDL=OFF -DWITH_SERVER=OFF -DWITH_SAMPLE=OFF
-DWITH_FFMPEG=OFF -DWITH_SWSCALE=OFF -DWITH_CAIRO=OFF -DWITH_SIMD=OFF
-DWITH_JSON_DISABLED=ON -DWITH_KRB5=OFF -DWITH_NATIVE_SSPI=OFF
-DWITH_INTERNAL_RC4=ON -DWITH_INTERNAL_MD4=ON -DWITH_WINMM=OFF
-DWITH_WINPR_TOOLS=OFF -DWITH_WINDOWS_CERT_STORE=OFF -DWITH_PROGRESS_BAR=OFF
-DWITH_MANPAGES=OFF -DBUILD_TESTING=OFF -DWITH_JPEG=OFF
-DCHANNEL_URBDRC=OFF -DUSE_UNWIND=OFF -DWITH_VERBOSE_WINPR_ASSERT=OFF
```

Why the unusual ones: `WITH_NATIVE_SSPI=OFF` + `WITH_INTERNAL_RC4/MD4=ON` — RT has no usable
native SSPI, so WinPR's internal NTLM is used, and NTLMv2 needs RC4/MD4 which OpenSSL 3's default
provider dropped. `WITH_SIMD=OFF` — generic C, avoids NEON alignment faults on Tegra 3.
`WITH_VERBOSE_WINPR_ASSERT=OFF` — release engines (1.0 ships this way).

### x64 / x86 (MSVC + vcpkg static deps)

MSVC Build Tools (`vcvars64.bat` / `vcvars32.bat`) + a vcpkg with `openssl` and `zlib` installed
for `x64-windows-static` / `x86-windows-static`. Configure with the common flags plus:

```
-DCMAKE_TOOLCHAIN_FILE=<vcpkg>/scripts/buildsystems/vcpkg.cmake
-DVCPKG_TARGET_TRIPLET=<x64|x86>-windows-static
# x86 additionally pins FindOpenSSL/FindZLIB internals (MSVC quirk):
-DLIB_EAY_RELEASE=<vcpkg-installed>/lib/libcrypto.lib -DSSL_EAY_RELEASE=<...>/libssl.lib
-DZLIB_LIBRARY_RELEASE=<...>/lib/zs.lib -DZLIB_INCLUDE_DIR=<...>/include
```

Then `cmake --build <builddir> --target wfreerdp`.

### ARM32 / Windows RT (clang-cl cross-compile)

- **Toolchain:** clang-cl 18.x targeting `thumbv7-unknown-windows-msvc`, `lld-link`, `llvm-lib`,
  `llvm-rc`; MSVC 14.16 ARM CRT/STL libs + Windows SDK 19041 ARM import libs on `LIB`,
  matching headers on `INCLUDE`. A CMake toolchain file sets `CMAKE_SYSTEM_NAME=Windows`
  (NOT WindowsStore — that would flip UWP mode and disable the desktop client).
- **Dependencies:** ARM32-built static OpenSSL (3.3.x) + zlib (1.3.x), fed via FindOpenSSL's
  internal variables (`-DLIB_EAY_RELEASE=... -DSSL_EAY_RELEASE=... -DZLIB_LIBRARY_RELEASE=...`)
  because on MSVC those overwrite the public ones.
- Configure with the common flags plus `-DCMAKE_WINDOWS_VERSION=WIN81` (requires the
  win81-winver patch) and the toolchain file; build `wfreerdp` with Ninja.
- Verify the output: machine type `IMAGE_FILE_MACHINE_ARMNT (0x1C4)` and **zero
  `api-ms-win-*` imports** (RT 8.1 lacks the umbrella DLLs) — e.g.
  `llvm-readobj --file-headers --coff-imports wfreerdp.exe`.

## 3. Packaging

```powershell
& installer\anycpu\build.ps1     # must print:  Setup.exe arch = MSIL
```

Stages `bin\Release` flat + `engine\` + LICENSE/notices, produces
`installer\Output\Finestra-Setup-<ver>.zip` (AnyCPU MSIL installer — the RT-capable ship
vehicle; an Inno-style native installer cannot launch on RT), `Finestra-<ver>-portable.zip`,
and `SHA256SUMS.txt`.
