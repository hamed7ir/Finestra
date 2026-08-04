# =============================================================================
#  Builds the AnyCPU .NET installer (Setup.exe + payload.zip) AND the portable ZIP
#  from Finestra\bin\Release. Pattern MIRRORED from CS-Ray installer/anycpu/build.ps1.
#
#  WHY AnyCPU: an Inno Setup installer is native x86 and CANNOT run on Windows RT /
#  ARM32 (its SetupLdr has no ARM emulation). This installer is compiled AnyCPU/MSIL -
#  like the app - so it runs on RT AND x86/x64. Run this AFTER building Release AND
#  after staging the release engines into bin\Release\engine\{x64,x86,arm}.
#
#  Produces (installer\Output\):
#    anycpu\Setup.exe + anycpu\payload.zip       (must travel together)
#    Finestra-Setup-<version>.zip                 (ship: extract, run Setup.exe)
#    Finestra-<version>-portable.zip              (ship: extract anywhere, run Finestra.exe)
#    SHA256SUMS.txt
# =============================================================================
$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # repo root (...\Finestra)
$rel    = Join-Path $root 'Finestra\bin\Release'
$icon   = Join-Path $root 'Finestra.ico'
$here   = $PSScriptRoot
$outdir = Join-Path $root 'installer\Output\anycpu'

if (-not (Test-Path "$rel\Finestra.exe")) { throw "Build Release first: '$rel\Finestra.exe' not found." }
if (-not (Test-Path $icon))               { throw "App icon not found: '$icon'." }
# ENGINE SET (FIN-X86-UNBUNDLE) -------------------------------------------------------------------
# x64 and arm are REQUIRED. x86 is OPTIONAL and is deliberately NOT bundled - engine\x86\ ships as a
# folder carrying only a README that explains how to supply one. These loops assert an EXPECTED SET
# instead of iterating whatever happens to be on disk: a loop over "what is present" silently passes
# when something is missing, and this project has already shipped one guard that could not fire
# (the FIN-RDP-WINV comment marker, stripped at compile time - see NOTES-carried M1).
$EngineRequired = @('x64','arm')
$EngineOptional = @('x86')
$EngineKnown    = $EngineRequired + $EngineOptional

foreach ($a in $EngineRequired) {
    if (-not (Test-Path "$rel\engine\$a\wfreerdp.exe")) {
        throw "Engine missing: '$rel\engine\$a\wfreerdp.exe' - stage the RELEASE engines first. ($a is REQUIRED; only x86 is optional.)"
    }
}
# An engine folder nobody declared means the staging convention drifted - fail rather than ship it blind.
if (Test-Path "$rel\engine") {
    $unknownEng = @(Get-ChildItem "$rel\engine" -Directory | ForEach-Object Name | Where-Object { $EngineKnown -notcontains $_ })
    if ($unknownEng.Count) {
        throw "Unexpected engine folder(s) in '$rel\engine': $($unknownEng -join ', '). Known: $($EngineKnown -join ', ')."
    }
}
# Only folders that actually hold a wfreerdp.exe are staged/scanned - a README-only x86 folder is not a binary.
$EnginePresent = @($EngineKnown | Where-Object { Test-Path "$rel\engine\$_\wfreerdp.exe" })
$EngineAbsent  = @($EngineKnown | Where-Object { $EnginePresent -notcontains $_ })
Write-Host ("[build] engines: required {0} OK; binaries present {1}{2}" -f ($EngineRequired -join '/'), ($EnginePresent -join '/'), $(if ($EngineAbsent.Count) { "; absent (optional) " + ($EngineAbsent -join '/') } else { "" }))

# ENGINE PROVENANCE GUARD -----------------------------------------------------------------------
# The development engine tree carries spike code that must never reach a release: the windowed
# Windows-key passthrough and the Media Foundation camera backend. Both are deliberately excluded
# from the publication branch, so an engine built from the development tree would ship them
# silently. Detect them by what actually survives compilation - a source comment does not, so a
# marker like FIN-RDP-WINV is absent from the binary in both ASCII and UTF-16 and cannot be used.
#   WinV   : the hook is the only caller of GetAsyncKeyState in this client (verified: 1 hit in a
#            WinV build, 0 in the shipped 1.0.2 engines).
#   camera : the Media Foundation backend pulls in mfplat / mfreadwrite imports.
foreach ($a in $EnginePresent) {
    $engPath = "$rel\engine\$a\wfreerdp.exe"
    $bytes   = [System.IO.File]::ReadAllBytes($engPath)
    $ascii   = [System.Text.Encoding]::ASCII.GetString($bytes)
    $wide    = [System.Text.Encoding]::Unicode.GetString($bytes)     # a marker may be UTF-16
    if ($ascii.Contains('GetAsyncKeyState') -or $wide.Contains('GetAsyncKeyState')) {
        throw "Engine '$a' carries the windowed Windows-key (WinV) spike. Build release engines from the publication branch (finestra-arm32-rt-3.28), not the development tree."
    }
    foreach ($mf in 'mfplat.dll','mfreadwrite.dll') {
        if ($ascii.ToLower().Contains($mf) -or $wide.ToLower().Contains($mf)) {
            throw "Engine '$a' carries the camera spike (imports $mf). Build release engines from the publication branch (finestra-arm32-rt-3.28), not the development tree."
        }
    }
}
Write-Host ("[build] engine provenance guard: {0} clean (no WinV hook, no camera spike)" -f ($EnginePresent -join '/'))

foreach ($f in 'LICENSE','THIRD-PARTY-NOTICES.txt','FREERDP-MODIFICATIONS.txt') {
    if (-not (Test-Path (Join-Path $root $f))) { throw "Legal file missing at repo root: $f" }
}

# Version tracks the BUILT assembly so the distributable names never go stale.
$ver = [System.Diagnostics.FileVersionInfo]::GetVersionInfo("$rel\Finestra.exe").FileVersion   # e.g. 1.0.0.0
$ver = (($ver -split '\.')[0..2]) -join '.'                                                    # -> 1.0.0

# GUARD: Setup.cs's AppVersion const is what the wizard displays and what lands in the uninstall
# registry as DisplayVersion, and it does NOT track AssemblyInfo.cs. Historically it silently drifted
# (the shipped 1.0.2 installer was built from a Setup.cs whose committed copy still said 1.0.0), so a
# mismatch is now a hard build failure rather than something you notice after publishing.
$setupCs  = Join-Path $PSScriptRoot 'Setup.cs'
$declared = [regex]::Match((Get-Content $setupCs -Raw), 'AppVersion\s*=\s*"([0-9]+(?:\.[0-9]+)*)"').Groups[1].Value
if (-not $declared) { throw "Could not read AppVersion from $setupCs" }
if ($declared -ne $ver) {
    throw "Version mismatch: Finestra.exe is $ver but Setup.cs declares AppVersion = '$declared'. " +
          "Update AppVersion in $setupCs (and Properties\AssemblyInfo.cs) so they agree, then re-run."
}
Write-Host "[build] version $ver (Setup.cs AppVersion agrees)"

# Locate a C# compiler (Roslyn preferred; framework csc is the fallback).
# Override with $env:FINESTRA_CSC when neither default location applies.
$csc = $env:FINESTRA_CSC
if (-not $csc -or -not (Test-Path $csc)) {
    $csc = @(
        'D:\Program Files\Vscom\MSBuild\15.0\Bin\Roslyn\csc.exe',
        'D:\Program Files\vs22buildtools\MSBuild\Current\Bin\Roslyn\csc.exe',
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $csc) { throw "No C# compiler found. Set FINESTRA_CSC to a csc.exe path." }

# 1. Stage the payload FLAT. Finestra is pure-managed with NO App.config <probing privatePath> - every managed
#    DLL sits BESIDE the exe. The native engines live under engine\<arch>\wfreerdp.exe, resolved by the app's
#    own BaseDirectory logic. One UNIVERSAL bundle for all three architectures.
$stage = Join-Path $env:TEMP ("finpay_" + [Guid]::NewGuid().ToString('N'))
New-Item $stage -ItemType Directory -Force | Out-Null
Copy-Item "$rel\Finestra.exe", "$rel\Finestra.exe.config" $stage
Copy-Item "$rel\*.dll" $stage                          # the managed closure, flat beside the exe
Copy-Item "$rel\engine" $stage -Recurse                # engine\<arch>\wfreerdp.exe
# x86 is UNBUNDLED (FIN-X86-UNBUNDLE). The FOLDER still ships - so the app's "expected
# engine\x86\wfreerdp.exe beside the app" message names a real place, and a user on 32-bit Windows can
# drop their own build in - but the BINARY does not. Excluding it here rather than deleting it from
# bin\Release keeps the exclusion explicit and reviewable, and leaves the build output intact.
$stageX86 = Join-Path $stage 'engine\x86'
New-Item $stageX86 -ItemType Directory -Force | Out-Null
if (Test-Path (Join-Path $stageX86 'wfreerdp.exe')) {
    Remove-Item (Join-Path $stageX86 'wfreerdp.exe') -Force
    Write-Host "[build] x86 engine binary EXCLUDED from the bundle (folder + README still ship)"
}
$x86Readme = Join-Path $here 'engine-x86-README.txt'
if (-not (Test-Path $x86Readme)) { throw "Missing '$x86Readme' - engine\x86\ must ship with its README." }
Copy-Item $x86Readme (Join-Path $stageX86 'README.txt')
# GPL: the license + third-party notices + the FreeRDP modification notice SHIP IN THE BUNDLE.
foreach ($f in 'LICENSE','THIRD-PARTY-NOTICES.txt','FREERDP-MODIFICATIONS.txt') {
    Copy-Item (Join-Path $root $f) $stage
}
$readme = Join-Path $here 'README-BUNDLE.txt'
if (Test-Path $readme) { Copy-Item $readme (Join-Path $stage 'README.txt') }

New-Item $outdir -ItemType Directory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

# 1a. The PORTABLE ZIP is the same stage, zipped as-is (the RT no-install path AND the GPL relinking path:
#     a user can swap any DLL/engine and re-run - nothing is registered anywhere).
$portable = Join-Path $root "installer\Output\Finestra-$ver-portable.zip"
if (Test-Path $portable) { Remove-Item $portable -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $portable)

# 1b. payload.zip for the installer (identical content).
$payload = Join-Path $outdir 'payload.zip'
if (Test-Path $payload) { Remove-Item $payload -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $payload)
$stageSizeMB = [math]::Round(((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 2)
Remove-Item $stage -Recurse -Force

# 2. Compile Setup.exe as AnyCPU (MSIL -> runs on RT + x86/x64), embedding the icon.
$setup = Join-Path $outdir 'Setup.exe'
if (Test-Path $setup) { Remove-Item $setup -Force }
& $csc /nologo /target:winexe /platform:anycpu "/out:$setup" "/win32icon:$icon" "/resource:$icon,FinestraSetup.icon.ico" `
    /reference:System.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "$here\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }

# 3. Package the RT-runnable installer distributable (Setup.exe + payload.zip must travel together -
#    Setup.exe extracts payload.zip from next to itself).
$dist = Join-Path $root "installer\Output\Finestra-Setup-$ver.zip"
if (Test-Path $dist) { Remove-Item $dist -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($outdir, $dist)

# 4. Checksums.
$sums = Join-Path $root 'installer\Output\SHA256SUMS.txt'
$lines = @()
foreach ($f in @($dist, $portable)) {
    $h = (Get-FileHash $f -Algorithm SHA256).Hash.ToLower()
    $lines += "$h  $(Split-Path $f -Leaf)"
}
Set-Content -Path $sums -Value ($lines -join "`n") -Encoding ascii

"Bundle (unpacked) = $stageSizeMB MB"
"Setup.exe arch    = " + [System.Reflection.AssemblyName]::GetAssemblyName($setup).ProcessorArchitecture
"Installer dist    = $dist  ({0} MB)" -f [math]::Round((Get-Item $dist).Length / 1MB, 2)
"Portable dist     = $portable  ({0} MB)" -f [math]::Round((Get-Item $portable).Length / 1MB, 2)
Get-Content $sums | ForEach-Object { "SHA256: $_" }
