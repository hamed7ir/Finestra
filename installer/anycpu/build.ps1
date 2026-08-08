# =============================================================================
#  Builds the AnyCPU .NET installer (Setup.exe + payload.zip) AND the portable ZIP
#  from Finestra\bin\Release. Pattern MIRRORED from CS-Ray installer/anycpu/build.ps1.
#
#  WHY AnyCPU: an Inno Setup installer is native x86 and CANNOT run on Windows RT /
#  ARM32 (its SetupLdr has no ARM emulation). This installer is compiled AnyCPU/MSIL -
#  like the app - so it runs on RT AND x86/x64. Run this AFTER building Release AND
#  after staging the release engines into bin\Release\engine\{x64,x86,arm}.
#
#  Produces (all under the OUTPUT ROOT - see -OutputRoot; public defaults to release\public\):
#    anycpu\Setup.exe + anycpu\payload.zip       (must travel together)
#    Finestra-Setup-<version>.zip                 (ship: extract, run Setup.exe)
#    Finestra-<version>-portable.zip              (ship: extract anywhere, run Finestra.exe)
#    SHA256SUMS.txt
# =============================================================================
# PRIVATE packaging opt-in — the ONLY way past the engine provenance guard below.
#
# THE RULE: a RELEASE engine is built from the publication branch finestra-arm32-rt-3.28, which carries
# exactly four source modifications over FreeRDP 3.28.0 and nothing else. The development tree carries
# additional, unreleased work on top of that. An engine built from the development tree is therefore not
# a release engine, however it was labelled -- so the guard below identifies one by what survives
# compilation and refuses it.
#
# NAMED FOR THE GUARD, NOT FOR ONE PIECE OF WORK. It is deliberately not named after whatever happens to
# be in the development tree today; that list changes, and a switch named for one item would age badly.
#
# It is a named switch on purpose -- an environment variable can be left set from a previous shell and
# silently taint the next build, which is exactly how an unreleased engine would eventually ship. It
# must be typed, per invocation, by someone who means it.
#   .\build.ps1 -AllowSpikeEngines
# With it: such an engine is allowed through, the artefacts and their checksum file are renamed so they
# cannot be mistaken for a release, Setup.exe is compiled as a separate product (see PRIVATE_BUILD in
# Setup.cs), and a PRIVATE-DO-NOT-DISTRIBUTE marker is written beside them.
# Without it: behaviour is EXACTLY as before -- the guard still refuses.
param(
    [switch]$AllowSpikeEngines,
    # Where EVERYTHING this script produces goes - intermediates and artefacts alike. See below.
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # repo root (...\Finestra)
$rel    = Join-Path $root 'Finestra\bin\Release'
$icon   = Join-Path $root 'Finestra.ico'
$here   = $PSScriptRoot
# SEPARATE OUTPUT ROOTS ---------------------------------------------------------------------------
# Public and private builds no longer share a directory. EVERY intermediate and EVERY artefact lands
# under its own root, so a name collision between the two flavours is not possible rather than
# individually prevented. SHA256SUMS.txt and anycpu\payload.zip were two instances of that one class:
# each was renamed after it bit, which is a treadmill. Two roots ends it.
#
# The PRIVATE root must be OUTSIDE this repository, and that is ENFORCED below, not merely advised.
# Private artefacts have twice had to be moved out by hand after landing in the repo's output folder;
# an ignore rule keeps them out of the index but not out of the working tree, and "remember to move
# them" is not a control. There is deliberately no default private path here - a public file should not
# name one - so a private build must say where its output goes, via -OutputRoot or FINESTRA_PRIVATE_OUT.
if (-not $OutputRoot) {
    if ($AllowSpikeEngines) {
        $OutputRoot = $env:FINESTRA_PRIVATE_OUT
        if (-not $OutputRoot) {
            throw ("A PRIVATE build needs an output root OUTSIDE this repository. Pass -OutputRoot <path>, " +
                   "or set FINESTRA_PRIVATE_OUT. (Private artefacts must not be written into the repo, " +
                   "even where .gitignore would hide them.)")
        }
    }
    else { $OutputRoot = Join-Path $root 'release\public' }
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
if ($AllowSpikeEngines) {
    # Compare SLASH-TERMINATED on BOTH sides. GetFullPath never appends a separator, so testing the raw
    # $OutputRoot against 'D:\repo\finestra\' was a strict proper-descendant test: it caught
    # 'D:\repo\finestra\release\priv' but MISSED the repository root itself, which is the one path most
    # likely to be typed by accident and the worst place for a private artefact to land.
    $repoFull = [System.IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $outFull  = $OutputRoot.TrimEnd('\') + '\'
    if ($outFull.StartsWith($repoFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw ("Refusing to write a PRIVATE build inside the repository: '$OutputRoot' is at or under '$root'. " +
               "Choose a root outside it.")
    }
}
New-Item $OutputRoot -ItemType Directory -Force | Out-Null
Write-Host ("[build] output root = {0}  ({1})" -f $OutputRoot, $(if ($AllowSpikeEngines) { 'PRIVATE' } else { 'public' }))

$outdir = Join-Path $OutputRoot 'anycpu'

if (-not (Test-Path "$rel\Finestra.exe")) { throw "Build Release first: '$rel\Finestra.exe' not found." }
if (-not (Test-Path $icon))               { throw "App icon not found: '$icon'." }
# ENGINE SET (FIN-X86-UNBUNDLE) -------------------------------------------------------------------
# x64 and arm are REQUIRED. x86 is OPTIONAL and is deliberately NOT bundled - engine\x86\ ships as a
# folder carrying only a README that explains how to supply one. These loops assert an EXPECTED SET
# instead of iterating whatever happens to be on disk: a loop over "what is present" silently passes
# when something is missing, and this project has already shipped one guard that could not fire
# (a source-comment marker, stripped at compile time - see NOTES-carried M1).
$EngineRequired = @('x64','arm')
$EngineOptional = @('x86')
$EngineKnown    = $EngineRequired + $EngineOptional

# FIN-PRIVATE-CAM — a PRIVATE build has a NARROWER expected set, not a relaxed one. Spike features are
# x64-only by design, so an ARM slot in a private bundle is meaningless: the only engine that could sit
# there is a PUBLIC one, riding inside an artefact named DO-NOT-DISTRIBUTE. That is a distribution
# hazard, so in spike mode x64 becomes the only REQUIRED arch and arm is FORBIDDEN outright.
# This tightens the rule and changes nothing on the public path below.
if ($AllowSpikeEngines) {
    $EngineRequired = @('x64')
    if (Test-Path "$rel\engine\arm\wfreerdp.exe") {
        throw "PRIVATE build: an ARM engine is staged at '$rel\engine\arm\wfreerdp.exe'. Private/spike builds are x64-ONLY - unstage it before packaging."
    }
}

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

$spikeEngines = @()
# ENGINE PROVENANCE GUARD -----------------------------------------------------------------------
# A release engine comes from the publication branch finestra-arm32-rt-3.28. The development tree
# carries unreleased work on top of that branch, so an engine built there would ship it silently.
#
# Identify one by what SURVIVES COMPILATION, not by anything written in source: a comment marker does
# not reach the binary at all, in either ASCII or UTF-16 (see NOTES-carried M1), so the only reliable
# evidence is imported API names and called entry points.
# Two independent signatures, neither present in an engine built from the publication branch:
#   - GetAsyncKeyState : this client has no other caller (1 hit in a development build, 0 in every
#                        shipped engine, verified in 1.0.2 and 1.0.3).
#   - mfplat / mfreadwrite : Media Foundation is not linked by the publication branch's build.
# Either one means "not built from the publication branch". Add a signature here when the development
# tree gains work with its own compiled fingerprint.
#
# ⚠ TWO DISCRIMINATORS, NOT INTERCHANGEABLE. Use the right one for what you are inspecting:
#     SOURCE REFS (a branch, a tree)  -> presence of the backend's own source DIRECTORY
#     BUILT BINARIES (what is below)  -> the mfplat / mfreadwrite strings
#   Do NOT scan a source ref for mfplat. Upstream's own libfreerdp/codec/h264_mf.c contains it, so
#   every ref descended from 3.28.0 matches and the scan reads as "everything is contaminated".
#
# ⚠ AND THIS SCAN HAS A BUILD-CONFIG DEPENDENCY. It is only sound while the pinned configuration
#   leaves Media Foundation OUT of the engine. The gate is NOT WITH_OPENH264 - it is:
#       libfreerdp/codec/CMakeLists.txt:  if(WIN32 AND WITH_MEDIA_FOUNDATION) ... h264_mf.c
#   If WITH_MEDIA_FOUNDATION is ever turned ON for a legitimate public engine, h264_mf.c compiles in,
#   its LoadLibraryA("mfplat.dll") puts that string in the binary, and THIS GUARD WOULD REFUSE A
#   PERFECTLY GOOD RELEASE ENGINE. If that day comes, do not weaken the guard - sharpen it (below).
#
#   TWO SHARPER FORMS, recorded but deliberately not implemented here:
#     1. Drop 'mfplat.dll' and keep ONLY 'mfreadwrite.dll'. Upstream never references mfreadwrite - all
#        12 mfplat/mfreadwrite hits on the publication branch are mfplat, in h264_mf.c - so mfreadwrite
#        alone separates the two cases with a one-word change.
#     2. Read the PE IMPORT TABLE rather than scanning strings. The cases differ exactly there:
#          h264_mf.c (upstream)  : LoadLibraryA("mfplat.dll")  -> a string, NO import-table entry
#          camera backend (ours) : CMake links mfplat/mf/mfreadwrite/mfuuid -> REAL import entries
#   Either survives WITH_MEDIA_FOUNDATION=ON. Left as a two-string scan for now because it is simple
#   and the pinned config keeps it correct; this note is the trigger to change that.
#
# ⚠ AND NOTE WHERE THE 'OFF' ACTUALLY COMES FROM. Three of the four shipping recipes pass
#   -DWITH_MEDIA_FOUNDATION=OFF explicitly; port\x64-rig-configure.bat passes NEITHER flag and relies
#   purely on the CMake default (cmake/ConfigOptions.cmake declares it OFF). A default is a weaker
#   guarantee than an explicit flag - if an upstream bump ever changes it, that recipe changes silently.
foreach ($a in $EnginePresent) {
    $engPath = "$rel\engine\$a\wfreerdp.exe"
    $bytes   = [System.IO.File]::ReadAllBytes($engPath)
    $ascii   = [System.Text.Encoding]::ASCII.GetString($bytes)
    $wide    = [System.Text.Encoding]::Unicode.GetString($bytes)     # a marker may be UTF-16
    $sig = @()
    if ($ascii.Contains('GetAsyncKeyState') -or $wide.Contains('GetAsyncKeyState')) { $sig += 'GetAsyncKeyState' }
    foreach ($mf in 'mfplat.dll','mfreadwrite.dll') {
        if ($ascii.ToLower().Contains($mf) -or $wide.ToLower().Contains($mf)) { $sig += $mf }
    }
    if ($sig.Count -gt 0) {
        # -AllowSpikeEngines is the ONLY bypass, and it is loud: the artefacts get renamed and marked.
        if (-not $AllowSpikeEngines) {
            throw "Engine '$a' is not built from the publication branch - it carries $($sig -join ', '), which finestra-arm32-rt-3.28 does not. Build release engines from that branch, not the development tree. If this is a deliberate PRIVATE build, re-run with -AllowSpikeEngines."
        }
        $spikeEngines += $a
        Write-Host ("[build] PRIVATE: engine '{0}' is not from the publication branch ({1}) - allowed by -AllowSpikeEngines" -f $a, ($sig -join ', '))
    }
}
if ($AllowSpikeEngines -and $spikeEngines.Count -eq 0) {
    Write-Host "[build] PRIVATE: -AllowSpikeEngines was given but every staged engine looks publication-branch clean - output is still marked private."
}
if (-not $AllowSpikeEngines) {
    Write-Host ("[build] engine provenance guard: {0} clean (publication-branch signatures only)" -f ($EnginePresent -join '/'))
}

foreach ($f in 'LICENSE','THIRD-PARTY-NOTICES.txt','FREERDP-MODIFICATIONS.txt') {
    if (-not (Test-Path (Join-Path $root $f))) { throw "Legal file missing at repo root: $f" }
}

# GUARD: Setup.cs's AppVersion const is what the wizard displays and what lands in the uninstall
# registry as DisplayVersion, and it does NOT track AssemblyInfo.cs. Historically it silently drifted
# (the shipped 1.0.2 installer was built from a Setup.cs whose committed copy still said 1.0.0), so a
# mismatch is now a hard build failure rather than something you notice after publishing.
#
# FIN-1.0.3 — WIDENED. Rail R5 is FOUR edits across TWO files, because AssemblyInfo.cs carries THREE
# INDEPENDENT version attributes and Setup.cs a fourth const. This guard previously compared only
# AssemblyFileVersion, so the other two could drift unnoticed:
#   AssemblyVersion              - the managed identity version used for assembly binding
#   AssemblyFileVersion          - what Explorer shows, and what names the distributables below
#   AssemblyInformationalVersion - surfaces as ProductVersion; what About and marketing text read
# All four are now cross-checked against each other, read from the COMPILED assembly (not the source),
# so a stale AssemblyInfo.cs edit cannot reach a package.
$setupCs  = Join-Path $PSScriptRoot 'Setup.cs'
$declared = [regex]::Match((Get-Content $setupCs -Raw), 'AppVersion\s*=\s*"([0-9]+(?:\.[0-9]+)*)"').Groups[1].Value
if (-not $declared) { throw "Could not read AppVersion from $setupCs" }

# Normalise every flavour to major.minor.patch: AssemblyVersion/FileVersion carry a 4th field, and an
# InformationalVersion may legitimately carry a semver suffix (1.0.3-rc1) that is not a version mismatch.
function Get-Norm3([string]$v) {
    if ([string]::IsNullOrWhiteSpace($v)) { return '' }
    $p = @($v.Trim() -split '[\.\+\-]')
    while ($p.Count -lt 3) { $p += '0' }
    return ($p[0..2]) -join '.'
}

$exePath  = "$rel\Finestra.exe"
$fvi      = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
$rawFile  = $fvi.FileVersion                                                          # AssemblyFileVersion
$rawProd  = $fvi.ProductVersion                                                       # AssemblyInformationalVersion
$rawAsm   = [System.Reflection.AssemblyName]::GetAssemblyName($exePath).Version.ToString()   # AssemblyVersion

$ver = Get-Norm3 $rawFile     # distributable names keep tracking FileVersion, exactly as before

$bad = @()
if ((Get-Norm3 $rawFile) -ne $declared) { $bad += "AssemblyFileVersion          = '$rawFile'" }
if ((Get-Norm3 $rawAsm)  -ne $declared) { $bad += "AssemblyVersion              = '$rawAsm'" }
if ((Get-Norm3 $rawProd) -ne $declared) { $bad += "AssemblyInformationalVersion = '$rawProd'" }
if ($bad.Count -gt 0) {
    throw ("Version mismatch. Setup.cs declares AppVersion = '$declared', but the built assembly says:`n  " +
           ($bad -join "`n  ") +
           "`nR5: a bump is FOUR edits - AssemblyInfo.cs lines 13/14/15 and Setup.cs's AppVersion const.")
}
Write-Host "[build] version $ver (AssemblyVersion + FileVersion + InformationalVersion + Setup.cs AppVersion all agree)"

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

# BUILD PROVENANCE — ties this artefact to the SOURCE COMMIT it was built from.
#
# The GPL source offer points at a repository; without this, matching a downloaded zip to a commit in
# that repository is guesswork, and "this build contains fix X" is an assertion nobody can check. The
# stamp goes INSIDE the bundle (so it travels with the artefact, not just with the console scrollback)
# and is echoed to the console.
#
# ⚠ A DIRTY TREE IS RECORDED AS DIRTY, not silently ignored. An artefact built from uncommitted work is
# exactly the one whose provenance cannot be reconstructed later, so it says so in the file. This is a
# statement of fact, not a gate - the build still proceeds.
$commit = 'unknown (git unavailable)'
$treeState = ''
try {
    $c = (& git -C $root rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $c) {
        $commit = $c.Trim()
        $porcelain = (& git -C $root status --porcelain 2>$null)
        $treeState = if ($porcelain) { "  ⚠ WORKING TREE WAS DIRTY - this artefact does NOT correspond to that commit alone" }
                     else            { "  (working tree clean)" }
    }
} catch { }
$engineLines = @()
foreach ($a in $EnginePresent) {
    $p = "$rel\engine\$a\wfreerdp.exe"
    if (Test-Path $p) { $engineLines += ("  engine/{0,-4} {1}" -f $a, (Get-FileHash $p -Algorithm SHA256).Hash.ToLower()) }
}
Set-Content -Path (Join-Path $stage 'BUILD-INFO.txt') -Encoding utf8 -Value (@(
  "Finestra $ver" + $(if ($AllowSpikeEngines) { '  [PRIVATE BUILD - DO NOT DISTRIBUTE]' } else { '' }),
  '',
  "source commit : $commit",
  "tree          :$treeState",
  "built (UTC)   : $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))",
  "repository    : https://github.com/hamed7ir/Finestra",
  '',
  'Verify this artefact against its source:',
  "  git clone https://github.com/hamed7ir/Finestra && git -C Finestra checkout $commit",
  '',
  'RDP engine binaries in this bundle (SHA-256), built from the FreeRDP fork branch',
  'finestra-arm32-rt-3.28 at https://github.com/hamed7ir/FreeRDP :'
) + $engineLines)
Write-Host ("[build] provenance: commit {0}{1}" -f $commit, $treeState)

New-Item $outdir -ItemType Directory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

# PRIVATE-BUILD MARKING — a private build must be UNMISTAKABLE. The name carries PRIVATE and
# DO-NOT-DISTRIBUTE, so it cannot be confused with a release artefact in a folder listing, in a chat
# window, or six months from now.
$nameTag = if ($AllowSpikeEngines) { "PRIVATE-DO-NOT-DISTRIBUTE-$ver" } else { $ver }

# ORDER MATTERS BELOW. The installer payload and the portable zip are the SAME stage except for one
# file, so the payload is written FIRST, then the portable sentinel is added, then the portable.
#
# 1a. payload.zip for the INSTALLER - deliberately WITHOUT the portable sentinel. An installed copy
#     must keep using the shared per-user data directory, which is how a public and a private install
#     see the same connections.
$payload = Join-Path $outdir 'payload.zip'
if (Test-Path $payload) { Remove-Item $payload -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $payload)

# 1b. The PORTABLE sentinel, for BOTH flavours (the RT no-install path AND the GPL relinking path).
#     ⚠ NOT gated on -AllowSpikeEngines. It used to be, and that was the bug: the sentinel rode on the
#     private flavour only, so the PUBLIC portable zip never carried it and was not portable at all.
#     Portability is a property of a portable bundle, not of a private one - never key it off the
#     DO-NOT-DISTRIBUTE marker or any other private-only artefact.
Set-Content -Path (Join-Path $stage 'Finestra.portable') -Encoding ascii -Value @(
  'This file makes Finestra PORTABLE: it keeps ALL of its data in this folder -',
  'connections, settings, known hosts, certificates and the log - instead of in',
  'your Documents folder.',
  '',
  'So this copy starts with NO connections, and any you create here stay here.',
  'That is the point: extract it anywhere, carry it, delete the folder to remove it.',
  '',
  'One exception, and it is opt-in: "Run at Windows startup" is a Windows setting,',
  'not a file, so turning it on writes an entry outside this folder that points at',
  'this exe. Leave it off and nothing outside this folder is written.',
  '',
  'Delete this file to make this copy use the shared Documents\Finestra data instead.')
Write-Host "[build] portable sentinel written into the PORTABLE bundle only (installer payload has none)"

# 1c. The PORTABLE ZIP - the same stage plus that one file.
$portable = Join-Path $OutputRoot "Finestra-$nameTag-portable.zip"
if (Test-Path $portable) { Remove-Item $portable -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $portable)
$stageSizeMB = [math]::Round(((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 2)
Remove-Item $stage -Recurse -Force

# 2. Compile Setup.exe as AnyCPU (MSIL -> runs on RT + x86/x64), embedding the icon.
$setup = Join-Path $outdir 'Setup.exe'
if (Test-Path $setup) { Remove-Item $setup -Force }
# FIN-PRIVATE-SEPARATION — a private installer is compiled as a SEPARATE PRODUCT: PRIVATE_BUILD swaps
# the identity consts in Setup.cs (install folder, shortcut name, uninstall key, display name) so it
# cannot overwrite or uninstall the public app. Conditional compilation rather than a runtime flag on
# purpose: the public Setup.exe then does not merely decline the private identity, it does not contain
# it at all.
$setupDefines = @()
if ($AllowSpikeEngines) { $setupDefines += '/define:PRIVATE_BUILD' }
& $csc /nologo /target:winexe /platform:anycpu "/out:$setup" "/win32icon:$icon" "/resource:$icon,FinestraSetup.icon.ico" `
    @setupDefines `
    /reference:System.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll "$here\Setup.cs"
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
if ($AllowSpikeEngines) { Write-Host "[build] PRIVATE: Setup.exe compiled with PRIVATE_BUILD (separate install dir, shortcut and uninstall entry)" }

# 3. Package the RT-runnable installer distributable (Setup.exe + payload.zip must travel together -
#    Setup.exe extracts payload.zip from next to itself).
$dist = Join-Path $OutputRoot "Finestra-Setup-$nameTag.zip"
if (Test-Path $dist) { Remove-Item $dist -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($outdir, $dist)

# 3b. FIN-CAMERA-CAPABILITY G4 — the marker, written BESIDE the artefacts.
if ($AllowSpikeEngines) {
    $marker = Join-Path $OutputRoot 'PRIVATE-DO-NOT-DISTRIBUTE.txt'
    @(
      'PRIVATE BUILD - DO NOT DISTRIBUTE, DO NOT PUBLISH, DO NOT ATTACH TO A RELEASE.',
      '',
      "Produced $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') by installer\anycpu\build.ps1 -AllowSpikeEngines.",
      'That switch exists to package an engine that was NOT built from the publication branch, which',
      'the provenance guard otherwise refuses outright.',
      '',
      $(if ($spikeEngines.Count) { "Engines not from the publication branch: $($spikeEngines -join ', ')" }
        else { 'Every staged engine looked publication-branch clean, but the switch was given, so this output is marked private anyway.' }),
      '',
      'This build is PRIVATE and stays private: not published, not released, not attached to a release,',
      'and not pushed to any public repository without explicit approval, per instance. Private does NOT',
      'mean deprecated - it is a supported way to build, just not a shippable one.',
      '',
      'The published release artefacts are the ones WITHOUT this marker and without PRIVATE in their',
      'file names. If you are looking at a folder containing both, the private ones are not shippable.'
    ) | Set-Content $marker -Encoding utf8
    Write-Host "[build] PRIVATE: wrote $marker"
}

# 4. Checksums.
# FIN-PRIVATE-CAM — the sums file carries the SAME tag as the artefacts it describes. Previously it was
# always 'SHA256SUMS.txt', so a private build silently OVERWROTE the public release's sums file sitting
# beside it - the one artefact whose whole job is to be trusted. Tagging it keeps the two apart.
$sums = Join-Path $OutputRoot ("SHA256SUMS" + $(if ($AllowSpikeEngines) { "-PRIVATE-DO-NOT-DISTRIBUTE-$ver" } else { "" }) + ".txt")
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
