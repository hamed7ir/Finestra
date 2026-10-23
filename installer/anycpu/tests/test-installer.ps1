# End-to-end test of a built Finestra installer against SCRATCH folders (run installer\anycpu\build.ps1 first).
#   powershell -NoProfile -ExecutionPolicy Bypass -File installer\anycpu\tests\test-installer.ps1 [-Setup <root>\Finestra-Setup-<ver>.exe] [-Work <dir>]
# -Setup defaults to the one Finestra-Setup-*.exe in release\public. Both flavours work: the identity under test (app
# name, install folder name, uninstall key, version) is read from the Setup exe itself, so a PRIVATE_BUILD Setup tests
# its own shortcut and Add/Remove entry.
# The Setup exe is ONE file: payload.zip, payload.list and uninstall.exe are embedded resources, and this test reads
# them back out of it. The tamper cases rebuild Setup from installer\anycpu\Setup.cs with an altered payload, so run
# the test against a package built from the Setup.cs in this checkout (an untampered rebuild is the control).
#
# Ported from CS-Ray's installer\tests\test-installer.ps1 (INSTALLER-REVIEW-HANDOFF-CSRAY.md). Safe on a dev machine:
#   - installs only under -Work (default %TEMP%\finestra-installer-test), never the real install folder
#   - backs up and restores the Start-menu and desktop shortcuts of that name
#   - `reg export`s the uninstall key first if it exists and imports it back at the end
#   - creates a Run value only under a name that is absent, and restores both Run values at the end
#   - reads Documents\Finestra only to hash it; never starts, stops or kills the real app (the in-use checks use a
#     stand-in that only sleeps, started from the scratch folder)
# Run it in its own powershell.exe: it loads Setup.exe to call its helpers, which keeps that file mapped until exit.
param([string]$Setup, [string]$Work)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
if (-not $Setup) {
    $found = @(Get-ChildItem (Join-Path $root 'release\public') -Filter 'Finestra-Setup-*.exe' -ErrorAction SilentlyContinue)
    if ($found.Count -ne 1) { throw "Expected exactly one Finestra-Setup-*.exe in release\public, found $($found.Count). Run installer\anycpu\build.ps1 first, or pass -Setup." }
    $Setup = $found[0].FullName
}
if (-not (Test-Path $Setup)) { throw "No $Setup. Run installer\anycpu\build.ps1 first, or pass -Setup." }
$Setup = (Resolve-Path $Setup).Path
$outRoot = Split-Path $Setup
$portableZip = @(Get-ChildItem $outRoot -Filter 'Finestra-*-portable.zip')
if ($portableZip.Count -ne 1) { throw "Expected exactly one Finestra-*-portable.zip in $outRoot, found $($portableZip.Count)." }
if (-not $Work) { $Work = Join-Path $env:TEMP 'finestra-installer-test' }
$Work = [System.IO.Path]::GetFullPath($Work).TrimEnd('\')

Add-Type -AssemblyName System.IO.Compression.FileSystem
$asm = [System.Reflection.Assembly]::LoadFrom($Setup)
$bf = [System.Reflection.BindingFlags]'NonPublic,Static,Public'
$P = $asm.GetType('FinestraSetup.Program')
function Const([string]$n) { $P.GetField($n, $bf).GetRawConstantValue() }
$AppName = Const 'AppName'; $FolderName = Const 'InstallFolderName'; $Version = Const 'AppVersion'; $UKeyPath = Const 'UninstallKey'
$ukey = 'HKCU:\' + $UKeyPath
$ukeyReg = 'HKCU\' + $UKeyPath
"Testing $Setup"
"  identity: '$AppName' $Version  folder '$FolderName'  key '$UKeyPath'"

# Never let the scratch area be (or contain) a real install location.
$realDefault = [System.IO.Path]::GetFullPath($P.GetMethod('DefaultDir', $bf).Invoke($null, $null)).TrimEnd('\')
$programsRoot = Join-Path $env:LOCALAPPDATA 'Programs'
if ($Work.StartsWith($programsRoot, [StringComparison]::OrdinalIgnoreCase) -or $realDefault.StartsWith($Work + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing -Work ${Work}: it overlaps the real install location."
}

$fails = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $tag = if ($ok) { '  ok  ' } else { '  FAIL' }
    Write-Output ("{0} {1,-66} {2}" -f $tag, $name, $detail)
    if (-not $ok) { $script:fails++ }
}
function RunSetup([string[]]$a, [string]$exe = $Setup) { $p = Start-Process -FilePath $exe -ArgumentList $a -PassThru -Wait -WindowStyle Hidden; return $p.ExitCode }
function Hash([string]$f) { if (Test-Path $f) { (Get-FileHash $f -Algorithm SHA256).Hash } else { 'none' } }
function Rel([string]$base, [string]$path) { $path.Substring($base.TrimEnd('\').Length + 1) }
function Left([string]$d) { @(Get-ChildItem $d -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { Rel $d $_.FullName } | Sort-Object) }
function Inside([string]$path, [string]$dir) {
    if (-not $path -or -not $dir) { return $false }
    try { $p = [System.IO.Path]::GetFullPath($path).TrimEnd('\'); $d = $dir.TrimEnd('\') } catch { return $false }
    return $p.Equals($d, [StringComparison]::OrdinalIgnoreCase) -or $p.StartsWith($d + '\', [StringComparison]::OrdinalIgnoreCase)
}

# The payload as Setup.exe describes it (the embedded payload.list).
$listLines = @()
$rs = $asm.GetManifestResourceStream('payload.list')
if ($rs) { $sr = New-Object System.IO.StreamReader($rs); while ($null -ne ($l = $sr.ReadLine())) { if ($l) { $listLines += $l } }; $sr.Dispose() }
$list = @($listLines | ForEach-Object { $f = $_ -split "`t"; [pscustomobject]@{ Sha = $f[0].ToUpperInvariant(); Size = [long]$f[1]; Name = $f[2] } })

$csc = if ($env:FINESTRA_TEST_CSC) { $env:FINESTRA_TEST_CSC } else { Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$here = $PSScriptRoot
if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
New-Item -ItemType Directory $Work | Out-Null
$dir = Join-Path $Work 'inst'
$other = Join-Path $Work 'other-copy'   # a pretend second install: entries pointing here are "another copy's"
New-Item -ItemType Directory $other | Out-Null
Set-Content (Join-Path $other 'Finestra.exe') 'not a real exe'   # gives that copy's shortcut a target that exists
$sh = New-Object -ComObject WScript.Shell

# The three embedded resources, written out for the checks and for the rebuilt variants below.
function SaveResource([string]$res, [string]$to) {
    $rr = $asm.GetManifestResourceStream($res)
    if (-not $rr) { return $false }
    $fw = [System.IO.File]::Create($to); try { $rr.CopyTo($fw) } finally { $fw.Dispose(); $rr.Dispose() }
    return $true
}
$payloadZip = Join-Path $Work 'payload.zip'
$listFile = Join-Path $Work 'payload.list'
$uninstEmbedded = Join-Path $Work 'embedded-uninstaller.bin'
$resOk = @(@{ R = 'payload.zip'; F = $payloadZip }, @{ R = 'payload.list'; F = $listFile }, @{ R = 'uninstall.exe'; F = $uninstEmbedded } | Where-Object { SaveResource $_.R $_.F })
if ($resOk.Count -ne 3) { throw "$Setup does not embed payload.zip, payload.list and uninstall.exe - is it the uninstaller, or a Setup.exe from before the single-exe installer?" }

# ── safety: snapshot everything shared that an install or uninstall can touch ────────────────────────────────
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
$dlnk = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) "$AppName.lnk"
$hadLnk = Test-Path $lnk;  if ($hadLnk)  { Copy-Item $lnk  (Join-Path $Work 'start.lnk.backup') }
$hadDLnk = Test-Path $dlnk; if ($hadDLnk) { Copy-Item $dlnk (Join-Path $Work 'desktop.lnk.backup') }
$dlnkBefore = Hash $dlnk
$hadKey = Test-Path $ukey
$keyBackup = Join-Path $Work 'uninstall-key.reg'
if ($hadKey) {
    cmd /c "reg export `"$ukeyReg`" `"$keyBackup`" /y >nul 2>&1"
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $keyBackup)) { throw "Could not export $ukeyReg - not running, so it cannot be lost." }
}
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
function RunVal([string]$n) { (Get-ItemProperty $runKey -Name $n -ErrorAction SilentlyContinue).$n }
$runBefore = @{ 'Finestra' = (RunVal 'Finestra'); 'FinestraRDP' = (RunVal 'FinestraRDP') }
$docs = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Finestra'
$docsFiles = 'connections.json', 'settings.json', 'known_hosts.json', 'known_certs.json'
$docsBefore = ($docsFiles | ForEach-Object { Hash (Join-Path $docs $_) }) -join ','

try {
    Check 'Setup embeds payload.list, payload.zip and uninstall.exe' ($list.Count -gt 0) "$($list.Count) files"
    $slashed = 0; $back = @()
    foreach ($zp in @($payloadZip, $portableZip[0].FullName)) {
        $zr = [System.IO.Compression.ZipFile]::OpenRead($zp)
        try { foreach ($e in $zr.Entries) { if ($e.FullName.Contains('\')) { $back += $e.FullName } elseif ($e.FullName.Contains('/')) { $slashed++ } } } finally { $zr.Dispose() }
    }
    # The '/' count is the control: entries under engine\ must have SOME separator, so 0 of either would prove nothing.
    Check 'zip entry names use forward slashes (payload + portable)' ($back.Count -eq 0 -and $slashed -gt 0) ("'/' in $slashed, '\' in $($back.Count) " + ($back -join ', '))

    # ── 1. silent install to scratch ─────────────────────────────────────────────────────────────────────────
    $code = RunSetup @('--silent', "`"$dir`"", '--no-desktop')
    Check '--silent install exit 0' ($code -eq 0) "exit $code"
    $missing = @(@($list.Name) + 'uninstall.exe' | Where-Object { -not (Test-Path (Join-Path $dir $_)) })
    Check 'every payload.list file installed, plus uninstall.exe' ($missing.Count -eq 0) ($missing -join ', ')
    $ref = Join-Path $Work 'payload-ref'
    [System.IO.Compression.ZipFile]::ExtractToDirectory($payloadZip, $ref)
    $refNames = @(Get-ChildItem $ref -File -Recurse | ForEach-Object { Rel $ref $_.FullName } | Sort-Object)
    Check 'payload.zip and payload.list name the same files' (($refNames -join '|') -eq ((@($list.Name) | Sort-Object) -join '|'))
    $badList = @($list | Where-Object { (Hash (Join-Path $ref $_.Name)) -ne $_.Sha -or (Get-Item (Join-Path $ref $_.Name)).Length -ne $_.Size })
    Check 'payload.list hashes + sizes match payload.zip' ($badList.Count -eq 0) (($badList | ForEach-Object Name) -join ', ')
    $diff = @($list | Where-Object { (Hash (Join-Path $dir $_.Name)) -ne $_.Sha })
    Check 'installed files byte-identical to the package' ($diff.Count -eq 0) (($diff | ForEach-Object Name) -join ', ')
    Check 'uninstall.exe = the embedded uninstaller, byte for byte' ((Hash (Join-Path $dir 'uninstall.exe')) -eq (Hash $uninstEmbedded))
    # Loaded from bytes, not from the install folder: a file-loaded assembly stays mapped and could not be deleted later.
    $uBytes = [System.IO.File]::ReadAllBytes($uninstEmbedded)
    $uAsm = [System.Reflection.Assembly]::Load($uBytes)
    $uRes = @($uAsm.GetManifestResourceNames())
    $uList = ''; $ur = $uAsm.GetManifestResourceStream('payload.list'); if ($ur) { $usr = New-Object System.IO.StreamReader($ur); $uList = $usr.ReadToEnd(); $usr.Dispose() }
    Check 'uninstall.exe carries the same payload.list and no payload' (($uRes -notcontains 'payload.zip') -and ($uRes -notcontains 'uninstall.exe') -and ([System.IO.File]::ReadAllText($listFile) -eq $uList)) ($uRes -join ', ')
    Check 'uninstall.exe is small (under a fifth of Setup)' ($uBytes.Length -lt (Get-Item $Setup).Length / 5) ("{0:N0} B vs {1:N0} B" -f $uBytes.Length, (Get-Item $Setup).Length)
    Check 'no portable sentinel in an install' (-not (Test-Path (Join-Path $dir 'Finestra.portable')))
    Check 'no x86 engine binary shipped (README only)' ((Test-Path (Join-Path $dir 'engine\x86\README.txt')) -and -not (Test-Path (Join-Path $dir 'engine\x86\wfreerdp.exe')) -and -not (Test-Path (Join-Path $dir 'engine\x86\sdl-freerdp.exe')))
    # The engine layout comes from the package's own list: a PUBLIC bundle ships the SDL engine for x64 and arm; a
    # PRIVATE one is x64-only (build.ps1 refuses an arm engine there) and carries the spike wfreerdp.exe.
    $engNames = @('sdl-freerdp.exe', 'wfreerdp.exe')
    $x64Eng = @($list.Name | Where-Object { $_ -match '^engine\\x64\\' })
    $armEng = @($list.Name | Where-Object { $_ -match '^engine\\arm\\' })
    if ($AppName -eq 'Finestra') {
        Check 'public: the x64 and arm engines are the SDL engine' (($x64Eng -join ',') -eq 'engine\x64\sdl-freerdp.exe' -and ($armEng -join ',') -eq 'engine\arm\sdl-freerdp.exe' -and (Test-Path (Join-Path $dir $x64Eng[0])) -and (Test-Path (Join-Path $dir $armEng[0]))) (($x64Eng + $armEng) -join ', ')
    } else {
        Check 'private: one x64 engine and no arm engine' ($x64Eng.Count -eq 1 -and $armEng.Count -eq 0 -and (Test-Path (Join-Path $dir $x64Eng[0]))) (($x64Eng + $armEng) -join ', ')
    }
    $otherX64 = 'engine\x64\' + @($engNames | Where-Object { ('engine\x64\' + $_) -ne $x64Eng[0] })[0]   # the x64 name this package does NOT ship
    $k = Get-ItemProperty $ukey -ErrorAction SilentlyContinue
    Check 'uninstall entry: location + version' ($k -and $k.InstallLocation -eq $dir -and $k.DisplayVersion -eq $Version) ("" + $k.DisplayVersion + " " + $k.InstallLocation)
    $u = Join-Path $dir 'uninstall.exe'
    Check 'uninstall entry: UninstallString + QuietUninstallString' ($k -and $k.UninstallString -eq "`"$u`" --uninstall `"$dir`"" -and $k.QuietUninstallString -eq "`"$u`" --uninstall `"$dir`" --silent") $k.QuietUninstallString
    Check 'start menu shortcut -> installed exe' ((Test-Path $lnk) -and $sh.CreateShortcut($lnk).TargetPath -eq (Join-Path $dir 'Finestra.exe'))
    Check 'desktop shortcut untouched with --no-desktop' ((Hash $dlnk) -eq $dlnkBefore)

    # ── 2. a running app or engine from the folder blocks install + uninstall (exit 4), and is never killed ────
    & $csc /nologo "/out:$Work\Sleeper.exe" "$here\Sleeper.cs" | Out-Null
    if (-not (Test-Path "$Work\Sleeper.exe")) { throw "could not compile Sleeper.cs with $csc" }
    # The engine this package ships blocks; so does the OTHER engine name, which an older release (or the other
    # flavour) may have left running from the folder - that file is not in this package, so it is created.
    foreach ($case in @(@{ Rel = 'Finestra.exe'; Label = 'Finestra.exe' }, @{ Rel = $x64Eng[0]; Label = $x64Eng[0] },
                        @{ Rel = $otherX64; Label = "an unshipped $otherX64"; Extra = $true })) {
        $target = Join-Path $dir $case.Rel
        $saved = Join-Path $Work ('real-' + ($case.Rel -replace '\\', '_'))
        if (-not $case.Extra) { Copy-Item $target $saved }
        Copy-Item "$Work\Sleeper.exe" $target -Force
        $sleeper = Start-Process $target -PassThru -WindowStyle Hidden
        Start-Sleep -Milliseconds 600
        $code = RunSetup @('--silent', "`"$dir`"", '--no-desktop')
        Check "install while $($case.Label) runs from it -> exit 4" ($code -eq 4) "exit $code"
        $code = RunSetup @('--uninstall', "`"$dir`"", '--silent')
        Check "uninstall while $($case.Label) runs from it -> exit 4" ($code -eq 4) "exit $code"
        Check "the running $($case.Label) was not killed" (-not $sleeper.HasExited)
        Stop-Process -Id $sleeper.Id -Force; $sleeper.WaitForExit()
        Start-Sleep -Milliseconds 300
        if ($case.Extra) { Remove-Item $target -Force } else { Copy-Item $saved $target -Force }
    }
    Check 'folder intact after the refused runs' ((@($list | Where-Object { (Hash (Join-Path $dir $_.Name)) -ne $_.Sha })).Count -eq 0)

    # ── 3. a damaged or mismatched payload is refused BEFORE the installed copy is touched ───────────────────
    # Variants are Setup rebuilt from Setup.cs with the SAME payload.list and uninstaller and an altered payload.zip -
    # what a mismatched or corrupted package looks like from inside. The untampered rebuild is the control: if IT
    # fails, the exit 1s below would prove nothing about the payload checks.
    $setupDefs = @(); if ($AppName -ne 'Finestra') { $setupDefs += '/define:PRIVATE_BUILD' }
    function Variant([string]$name, [scriptblock]$edit) {
        $v = Join-Path $Work $name; New-Item -ItemType Directory $v | Out-Null
        $vz = Join-Path $v 'payload.zip'; Copy-Item $payloadZip $vz
        $z = [System.IO.Compression.ZipFile]::Open($vz, 'Update')
        try { & $edit $z } finally { $z.Dispose() }
        $exe = Join-Path $v 'Finestra-Setup-variant.exe'
        & $csc /nologo /target:winexe /platform:anycpu "/out:$exe" "/resource:$listFile,payload.list" "/resource:$vz,payload.zip" `
            "/resource:$uninstEmbedded,uninstall.exe" @setupDefs /reference:System.dll /reference:System.Windows.Forms.dll `
            /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll `
            (Join-Path $here '..\Setup.cs') | Out-Null
        if (-not (Test-Path $exe)) { throw "could not compile the '$name' variant of Setup.cs with $csc" }
        return $exe
    }
    $control = Variant 'control' { param($z) }
    $code = RunSetup @('--silent', "`"$dir`"", '--no-desktop') $control
    Check 'control: an untampered rebuild of Setup installs (exit 0)' ($code -eq 0) "exit $code"
    $before = @{}; foreach ($f in $list) { $before[$f.Name] = Hash (Join-Path $dir $f.Name) }
    $damaged = Variant 'damaged' { param($z) $e = $z.GetEntry('README.txt'); $e.Delete(); $n = $z.CreateEntry('README.txt'); $w = New-Object System.IO.StreamWriter($n.Open()); $w.Write('tampered'); $w.Dispose() }
    $code = RunSetup @('--silent', "`"$dir`"", '--no-desktop') $damaged
    $changed = @($list | Where-Object { (Hash (Join-Path $dir $_.Name)) -ne $before[$_.Name] })
    $news = @(Get-ChildItem $dir -Recurse -Filter '*.new' -ErrorAction SilentlyContinue)
    Check 'damaged payload -> exit 1, installed copy untouched' ($code -eq 1 -and $changed.Count -eq 0 -and $news.Count -eq 0) "exit $code, changed $($changed.Count), .new $($news.Count)"
    $extra = Variant 'extra' { param($z) $n = $z.CreateEntry('evil.txt'); $w = New-Object System.IO.StreamWriter($n.Open()); $w.Write('x'); $w.Dispose() }
    $code = RunSetup @('--silent', "`"$dir`"", '--no-desktop') $extra
    Check 'payload with an unlisted file -> exit 1, nothing written' ($code -eq 1 -and -not (Test-Path (Join-Path $dir 'evil.txt'))) "exit $code"
    $short = Variant 'short' { param($z) $z.GetEntry('LICENSE').Delete() }
    $code = RunSetup @('--silent', "`"$dir`"", '--no-desktop') $short
    Check 'payload missing a listed file -> exit 1, LICENSE kept' ($code -eq 1 -and (Hash (Join-Path $dir 'LICENSE')) -eq $before['LICENSE']) "exit $code"
    # Corruption of the shipped exe itself: one byte flipped inside its embedded payload.zip. The embedded zip is
    # found by its bytes (Latin-1 maps each byte to one char, so an ordinal string search is a byte search), and it
    # must occur exactly once or the flip would not be where this test says it is.
    $sBytes = [System.IO.File]::ReadAllBytes($Setup)
    $zBytes = [System.IO.File]::ReadAllBytes($payloadZip)
    $l1 = [System.Text.Encoding]::GetEncoding(28591)
    $sText = $l1.GetString($sBytes); $zHead = $l1.GetString($zBytes, 0, 4096)
    $at = $sText.IndexOf($zHead, [StringComparison]::Ordinal)
    $once = $at -ge 0 -and $at -eq $sText.LastIndexOf($zHead, [StringComparison]::Ordinal)
    $flipDir = Join-Path $Work 'flipped'; New-Item -ItemType Directory $flipDir | Out-Null
    $flipped = Join-Path $flipDir (Split-Path $Setup -Leaf)
    if ($once) { $pos = $at + [int]($zBytes.Length / 2); $sBytes[$pos] = $sBytes[$pos] -bxor 0xFF; [System.IO.File]::WriteAllBytes($flipped, $sBytes) }
    $code = if ($once) { RunSetup @('--silent', "`"$dir`"", '--no-desktop') $flipped } else { -1 }
    $changed = @($list | Where-Object { (Hash (Join-Path $dir $_.Name)) -ne $before[$_.Name] })
    Check 'one flipped byte in the shipped exe''s payload -> exit 1, untouched' ($once -and $code -eq 1 -and $changed.Count -eq 0) "payload found once: $once, exit $code, changed $($changed.Count)"
    # The exe needs nothing beside it.
    $alone = Join-Path $Work 'alone'; New-Item -ItemType Directory $alone | Out-Null; Copy-Item $Setup $alone
    $code = RunSetup @('--silent', "`"$dir`"", '--no-desktop') (Join-Path $alone (Split-Path $Setup -Leaf))
    Check 'the Setup exe alone in an empty folder installs (exit 0)' ($code -eq 0) "exit $code"
    # The payload-free uninstaller has nothing to install, whatever it is asked.
    $fromUninst = Join-Path $Work 'from-uninstaller'
    $code = RunSetup @('--silent', "`"$fromUninst`"", '--no-desktop') (Join-Path $dir 'uninstall.exe')
    Check 'uninstall.exe asked to install -> exit 1, nothing written' ($code -eq 1 -and -not (Test-Path $fromUninst)) "exit $code"

    # ── 4. upgrade in place: ours replaced, the user's own files (and their x86 engine) kept ─────────────────
    $userX86 = Join-Path $dir 'engine\x86\wfreerdp.exe'
    Set-Content $userX86 'a user-supplied 32-bit engine'
    $userX86Hash = Hash $userX86
    Set-Content (Join-Path $dir 'my-notes.txt') 'not ours'
    Set-Content (Join-Path $dir 'connections.json') '{"Profiles":[]}'
    Set-Content (Join-Path $dir 'Finestra.exe.new') 'left by an install cut off mid-swap'
    # SDL-ENGINE — what an earlier release left behind: every x64/arm engine name Owned.Shipped knows and this package
    # does not carry (public: the 1.0.2/1.0.3 wfreerdp.exe in both slots; private: the SDL names and anything in arm).
    $retiredEng = @(foreach ($a in 'x64', 'arm') { foreach ($n in $engNames) { $r = "engine\$a\$n"; if (@($list.Name) -notcontains $r) { $r } } })
    foreach ($r in $retiredEng) { New-Item -ItemType Directory -Force (Split-Path (Join-Path $dir $r)) | Out-Null; Set-Content (Join-Path $dir $r) 'an earlier release''s engine' }
    $code = RunSetup @('--silent', "`"$dir`"", '--no-desktop')
    Check 'upgrade in place exit 0' ($code -eq 0) "exit $code"
    Check 'upgrade keeps files Finestra never shipped' ((Test-Path (Join-Path $dir 'my-notes.txt')) -and (Test-Path (Join-Path $dir 'connections.json')))
    Check 'upgrade keeps a user-supplied engine\x86\wfreerdp.exe' ((Hash $userX86) -eq $userX86Hash)
    $notRetired = @($retiredEng | Where-Object { Test-Path (Join-Path $dir $_) })
    Check 'upgrade retires engines this package no longer ships' ($retiredEng.Count -ge 2 -and $notRetired.Count -eq 0) ("planted: " + ($retiredEng -join ', ') + $(if ($notRetired.Count) { "; still there: " + ($notRetired -join ', ') } else { '' }))
    Check 'upgrade replaces a stale Finestra.exe.new' (-not (Test-Path (Join-Path $dir 'Finestra.exe.new')))
    # Retiring, the general case: a file an older release shipped and this payload does not carry goes on upgrade.
    # (The wfreerdp check above is the real instance.) Drive Owned.RemoveRetired with this package minus VtNetCore.dll.
    $pl = $asm.GetType('FinestraSetup.Payload').GetMethod('List', $bf).Invoke($null, $null)
    $nameF = $asm.GetType('FinestraSetup.PayloadFile').GetField('Name')
    for ($i = $pl.Count - 1; $i -ge 0; $i--) { if ($nameF.GetValue($pl[$i]) -eq 'VtNetCore.dll') { $pl.RemoveAt($i) } }
    $asm.GetType('FinestraSetup.Owned').GetMethod('RemoveRetired', $bf).Invoke($null, [object[]]@([string]$dir, $pl.PSObject.BaseObject)) | Out-Null
    Check 'a retired file is removed, the user''s are not' (-not (Test-Path (Join-Path $dir 'VtNetCore.dll')) -and (Test-Path (Join-Path $dir 'my-notes.txt')) -and (Hash $userX86) -eq $userX86Hash)
    $code = RunSetup @('--silent', "`"$dir`"", '--no-desktop')
    Check 're-install restores it' ($code -eq 0 -and (Test-Path (Join-Path $dir 'VtNetCore.dll'))) "exit $code"
    $code = RunSetup @('--silent', '/S')
    Check '--silent with an unknown switch (/S) -> exit 2' ($code -eq 2) "exit $code"
    $code = RunSetup @('--silent', "`"$dir`"", '--no-dekstop')
    Check '--silent with a mistyped switch -> exit 2' ($code -eq 2) "exit $code"

    # ── 5. internal helpers (Setup.exe is a .NET assembly) ─────────────────────────────────────────────────────
    $un = $asm.GetType('FinestraSetup.Uninstaller')
    $exeOf = { param($v) $un.GetMethod('ExeOf', $bf).Invoke($null, @(,$v)) }
    $inside = { param($a, $b) $un.GetMethod('Inside', $bf).Invoke($null, [object[]]@($a, $b)) }
    $norm = { param($v) $P.GetMethod('Norm', $bf).Invoke($null, @(,$v)) }
    Check 'run value: quoted exe + /tray parsed' ((& $exeOf '"C:\a b\Finestra.exe" /tray') -eq 'C:\a b\Finestra.exe')
    Check 'run value: unquoted exe + /tray parsed' ((& $exeOf 'C:\x\Finestra.exe /tray') -eq 'C:\x\Finestra.exe')
    Check 'Inside: a file in the folder is inside' (& $inside 'C:\a\Finestra\Finestra.exe' 'C:\a\Finestra')
    Check 'Inside: ...\Finestra-Private is NOT inside ...\Finestra' (-not (& $inside 'C:\a\Finestra-Private\Finestra.exe' 'C:\a\Finestra'))
    Check 'Inside: empty / unreadable is not ours' (-not (& $inside '' 'C:\a') -and -not (& $inside 'C:\a\x' $null))
    Check 'Norm: trailing backslash dropped, empty -> null' ((& $norm 'C:\x\inst\') -eq 'C:\x\inst' -and $null -eq (& $norm '  '))

    # ── 6. folder safety: never write into or remove a folder that is not ours ──────────────────────────────────
    $st = $asm.GetType('FinestraSetup.SafeTarget').GetMethod('Problem', $bf)
    $prob = { param($d) $st.Invoke($null, @(,[string]$d)) }
    $docs0 = [Environment]::GetFolderPath('MyDocuments'); $desk0 = [Environment]::GetFolderPath('DesktopDirectory')
    $refused = @($docs0, $desk0, $env:USERPROFILE, 'C:\', $programsRoot, (Join-Path $env:USERPROFILE 'Downloads'), $env:WINDIR, (Join-Path $Work '50%off')) | Where-Object { -not (& $prob $_) }
    Check 'refuses Documents/Desktop/profile/drive/Programs/Downloads/Windows/%' ($refused.Count -eq 0) ($refused -join ', ')
    Check 'accepts a new folder under Documents' ($null -eq (& $prob (Join-Path $docs0 'Finestra-new-folder-that-does-not-exist')))
    $foreign = Join-Path $Work 'foreign'; New-Item -ItemType Directory $foreign | Out-Null; Set-Content (Join-Path $foreign 'keep.txt') 'user data'
    $code = RunSetup @('--silent', "`"$foreign`"", '--no-desktop')
    Check '--silent into a non-empty foreign folder -> exit 5, file kept' ($code -eq 5 -and (Test-Path (Join-Path $foreign 'keep.txt'))) "exit $code"
    $code = RunSetup @('--uninstall', "`"$foreign`"", '--silent')
    $portableLike = Join-Path $Work 'portable-like'; New-Item -ItemType Directory $portableLike | Out-Null
    Copy-Item (Join-Path $dir 'Finestra.exe') $portableLike; Set-Content (Join-Path $portableLike 'connections.json') '{}'
    $code2 = RunSetup @('--uninstall', "`"$portableLike`"", '--silent')
    Start-Sleep -Seconds 3
    Check '--uninstall of a foreign folder -> exit 5, file kept' ($code -eq 5 -and (Test-Path (Join-Path $foreign 'keep.txt'))) "exit $code"
    Check '--uninstall of Finestra.exe without uninstall.exe -> exit 5' ($code2 -eq 5 -and (Test-Path (Join-Path $portableLike 'Finestra.exe'))) "exit $code2"

    # ── 7. uninstall with the user's files in the folder: ours go, theirs stay (and so does the folder) ─────────
    Set-Content (Join-Path $dir 'Finestra.exe.new') 'left by an install cut off mid-swap'
    $lnkTarget = if (Test-Path $lnk) { $sh.CreateShortcut($lnk).TargetPath } else { '(none)' }
    $code = RunSetup @('--uninstall', "`"$dir`"", '--silent')
    Start-Sleep -Seconds 5   # the detached cmd deletes uninstall.exe after Setup exits
    $left = (Left $dir) -join ','
    Check 'uninstall with user files: exit 0, only theirs left' ($code -eq 0 -and $left -eq 'connections.json,engine,engine\x86,engine\x86\wfreerdp.exe,my-notes.txt') ("exit $code, left: $left")
    # On failure, say WHICH failure: our own .lnk left behind (its bytes still name the scratch folder - WScript may then
    # report a "resolved" look-alike target elsewhere) or a different .lnk written in its place.
    $lnkRaw = if (Test-Path $lnk) { $lb = [System.IO.File]::ReadAllBytes($lnk); $u0 = [System.Text.Encoding]::Unicode.GetString($lb); $u1 = [System.Text.Encoding]::Unicode.GetString($lb, 1, $lb.Length - 1)
        "; bytes name the scratch folder: " + ($u0.IndexOf($dir, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $u1.IndexOf($dir, [StringComparison]::OrdinalIgnoreCase) -ge 0) + ", written " + (Get-Item $lnk).LastWriteTime.ToString('HH:mm:ss.fff') } else { '' }
    Check 'our start menu shortcut removed' (-not (Test-Path $lnk)) ("before: $lnkTarget; after: " + $(if (Test-Path $lnk) { $sh.CreateShortcut($lnk).TargetPath } else { '(gone)' }) + $lnkRaw)
    Remove-Item $dir -Recurse -Force

    # ── 8. install to a path given with a trailing backslash: stored without it ────────────────────────────────
    $code = RunSetup @('--silent', "`"$dir\\`"", '--no-desktop')
    $k2 = Get-ItemProperty $ukey -ErrorAction SilentlyContinue
    Check 'trailing-backslash path: exit 0, normalised location' ($code -eq 0 -and $k2 -and $k2.InstallLocation -eq $dir -and (Test-Path (Join-Path $dir 'Finestra.exe'))) ("exit $code " + $k2.InstallLocation)
    Check 'trailing-backslash path: UninstallString has no \"' ($k2 -and $k2.UninstallString -notmatch '\\"') $k2.UninstallString

    # ── 9. clean uninstall through the REGISTERED QuietUninstallString ─────────────────────────────────────────
    # Ownership: every shared entry with a fixed name goes only when it points into the folder being removed.
    # Start menu: repoint our shortcut at "another copy" -> it must survive. Run values: an absent name gets "ours",
    # an existing value (the user's real one) or a planted other-copy value must survive unchanged.
    $sc = $sh.CreateShortcut($lnk); $sc.TargetPath = (Join-Path $other 'Finestra.exe'); $sc.Save()
    $ours = $null; $planted = @()
    foreach ($n in 'Finestra', 'FinestraRDP') {
        if ($null -eq $runBefore[$n]) {
            if (-not $ours) { $ours = $n; Set-ItemProperty $runKey -Name $n -Value ("`"" + (Join-Path $dir 'Finestra.exe') + "`" /tray") }
            else { $planted += $n; Set-ItemProperty $runKey -Name $n -Value ("`"" + (Join-Path $other 'Finestra.exe') + "`" /tray") }
        }
    }
    $runMid = @{ 'Finestra' = (RunVal 'Finestra'); 'FinestraRDP' = (RunVal 'FinestraRDP') }
    $quietCmd = (Get-ItemProperty $ukey).QuietUninstallString
    $m = [regex]::Match($quietCmd, '^"([^"]+)"\s+(.*)$')
    $code = RunSetup @($m.Groups[2].Value) $m.Groups[1].Value
    Start-Sleep -Seconds 5   # the detached cmd removes uninstall.exe and the folder after Setup exits
    Check 'registered QuietUninstallString: exit 0' ($code -eq 0) "exit $code"
    Check 'install folder removed' (-not (Test-Path $dir))
    Check 'uninstall entry removed' (-not (Test-Path $ukey))
    Check 'a start menu shortcut pointing at ANOTHER copy kept' ((Test-Path $lnk) -and (Inside $sh.CreateShortcut($lnk).TargetPath $other))
    if ($ours) { Check "our Run value '$ours' removed" ($null -eq (RunVal $ours)) }
    else { Check 'our Run value removed (not testable: both Run names already in use)' $true }
    $keptOk = $true; foreach ($n in 'Finestra', 'FinestraRDP') { if ($n -ne $ours -and (RunVal $n) -ne $runMid[$n]) { $keptOk = $false } }
    Check 'Run values of ANOTHER copy kept unchanged' $keptOk
    Check 'desktop shortcut untouched' ((Hash $dlnk) -eq $dlnkBefore)
    Check 'Documents\Finestra untouched' ((($docsFiles | ForEach-Object { Hash (Join-Path $docs $_) }) -join ',') -eq $docsBefore)

    # ── 10. portable zip: the same files plus the sentinel, and the app keeps its data beside the exe ───────────
    $port = Join-Path $Work 'portable'
    [System.IO.Compression.ZipFile]::ExtractToDirectory($portableZip[0].FullName, $port)
    $portNames = @(Get-ChildItem $port -File -Recurse | ForEach-Object { Rel $port $_.FullName } | Sort-Object)
    Check 'portable zip = payload + Finestra.portable' (($portNames -join '|') -eq ((@($list.Name) + 'Finestra.portable' | Sort-Object) -join '|')) "$($portNames.Count) vs $($list.Count)+1"
    & $csc /nologo "/out:$port\PortableProbe.exe" "/r:$port\Finestra.exe" "$here\PortableProbe.cs" | Out-Null
    $out = @(& "$port\PortableProbe.exe")
    Check 'portable: data dir is the exe folder' ($out.Count -ge 2 -and $out[0] -eq $port -and $out[1] -eq (Join-Path $port 'connections.json')) ($out -join ' | ')
}
finally {
    # Shortcuts: put back what was there; drop anything this test created that points into the scratch area.
    foreach ($s in @(@{ L = $lnk; Had = $hadLnk; B = (Join-Path $Work 'start.lnk.backup') }, @{ L = $dlnk; Had = $hadDLnk; B = (Join-Path $Work 'desktop.lnk.backup') })) {
        if ($s.Had -and (Test-Path $s.B)) { Copy-Item $s.B $s.L -Force }
        elseif (-not $s.Had -and (Test-Path $s.L) -and (Inside $sh.CreateShortcut($s.L).TargetPath $Work)) { Remove-Item $s.L -Force }
    }
    # Uninstall key: drop a test-made key (it points into the scratch area), then re-import the original.
    $kNow = Get-ItemProperty $ukey -ErrorAction SilentlyContinue
    if ($kNow -and (Inside $kNow.InstallLocation $Work)) { Remove-Item $ukey -Recurse -Force }
    if ($hadKey) { cmd /c "reg import `"$keyBackup`" >nul 2>&1"; if ($LASTEXITCODE -ne 0) { Write-Output "  !! could not re-import $keyBackup - import it by hand" } }
    # Run values: exactly as they were.
    foreach ($n in 'Finestra', 'FinestraRDP') {
        if ($null -eq $runBefore[$n]) { Remove-ItemProperty $runKey -Name $n -ErrorAction SilentlyContinue }
        elseif ((RunVal $n) -ne $runBefore[$n]) { Set-ItemProperty $runKey -Name $n -Value $runBefore[$n] }
    }
    Get-Process Sleeper -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    ''
    'restored: start menu -> ' + $(if (Test-Path $lnk) { $sh.CreateShortcut($lnk).TargetPath } else { '(none)' })
    'restored: uninstall key -> ' + $(if (Test-Path $ukey) { (Get-ItemProperty $ukey).InstallLocation } else { '(none)' }) + $(if ($hadKey) { '  (re-imported)' } else { '' })
    'restored: Run Finestra = ' + $(if ($null -ne (RunVal 'Finestra')) { RunVal 'Finestra' } else { '(none)' }) + ' ; FinestraRDP = ' + $(if ($null -ne (RunVal 'FinestraRDP')) { RunVal 'FinestraRDP' } else { '(none)' })
}
if ($fails -eq 0) { 'ALL OK' } else { "$fails FAILURE(S)" }
exit $fails
