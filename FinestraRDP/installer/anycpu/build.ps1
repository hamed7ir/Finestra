# =============================================================================
#  Builds the AnyCPU .NET installer (Setup.exe + payload.zip) AND the portable ZIP
#  from FinestraRDP\bin\Release. Pattern MIRRORED from CS-Ray installer/anycpu/build.ps1.
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
$root   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # repo root (...\FinestraRDP)
$rel    = Join-Path $root 'FinestraRDP\bin\Release'
$icon   = Join-Path $root 'Finestra.ico'
$here   = $PSScriptRoot
$outdir = Join-Path $root 'installer\Output\anycpu'

if (-not (Test-Path "$rel\Finestra.exe")) { throw "Build Release first: '$rel\Finestra.exe' not found." }
if (-not (Test-Path $icon))               { throw "App icon not found: '$icon'." }
foreach ($a in 'x64','x86','arm') {
    if (-not (Test-Path "$rel\engine\$a\wfreerdp.exe")) { throw "Engine missing: '$rel\engine\$a\wfreerdp.exe' - stage the RELEASE engines first." }
}
foreach ($f in 'LICENSE','THIRD-PARTY-NOTICES.txt','FREERDP-MODIFICATIONS.txt') {
    if (-not (Test-Path (Join-Path $root $f))) { throw "Legal file missing at repo root: $f" }
}

# Version tracks the BUILT assembly so the distributable names never go stale. (Keep Setup.cs's AppVersion
# const in sync for the wizard's displayed version + the uninstall registry DisplayVersion.)
$ver = [System.Diagnostics.FileVersionInfo]::GetVersionInfo("$rel\Finestra.exe").FileVersion   # e.g. 1.0.0.0
$ver = (($ver -split '\.')[0..2]) -join '.'                                                    # -> 1.0.0

# Locate a C# compiler (Roslyn preferred; framework csc is the fallback).
$csc = 'D:\Program Files\Vscom\MSBuild\15.0\Bin\Roslyn\csc.exe'
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "No C# compiler found (looked for Roslyn + framework csc)." }

# 1. Stage the payload FLAT. Finestra is pure-managed with NO App.config <probing privatePath> - every managed
#    DLL sits BESIDE the exe. The native engines live under engine\<arch>\wfreerdp.exe, resolved by the app's
#    own BaseDirectory logic. One UNIVERSAL bundle for all three architectures.
$stage = Join-Path $env:TEMP ("finpay_" + [Guid]::NewGuid().ToString('N'))
New-Item $stage -ItemType Directory -Force | Out-Null
Copy-Item "$rel\Finestra.exe", "$rel\Finestra.exe.config" $stage
Copy-Item "$rel\*.dll" $stage                          # the managed closure, flat beside the exe
Copy-Item "$rel\engine" $stage -Recurse                # engine\{x64,x86,arm}\wfreerdp.exe
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
