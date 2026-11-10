<#
  infinite.ps1 - keeps item counts topped up in the running Hookshot game.

  How it works: the game keeps a live item table in memory (entries 0x28 bytes apart, count at +0x1C with a
  duplicate at +0x20). This script starts the trainer CLI, finds that table by its marker bytes, checks the
  layout looks right, then locks each count. It re-finds the table every run, so it does not depend on any
  address staying the same between launches.

  Usage:  start the game, load your save, then run   infinite.bat   (or this script).
  Stop:   close the game (the script ends by itself) or press Ctrl+C.

  Nothing is written to your save files; the game saves its own live numbers as usual.
#>
param(
    [int]$ItemCount  = 99,     # held value for ammo / consumables
    [int]$RupeeCount = 1000,   # held value for rupees (your wallet's limit)
    [int]$WaitMinutes = 15     # how long to wait for the game and its item table to appear
)

$ErrorActionPreference = 'Stop'
$GameDir = Split-Path -Parent $PSScriptRoot
$Image   = Join-Path $GameDir 'image.bin'
$Offsets = Join-Path $PSScriptRoot 'offsets.json'
$Dll     = Join-Path $PSScriptRoot 'OotTrainer.Cli\bin\Release\net9.0\OotTrainer.Cli.dll'
$Work    = Join-Path $env:TEMP 'oot-infinite'
$CmdFile = Join-Path $Work 'cmd.txt'
$OutFile = Join-Path $Work 'out.txt'
$LogFile = Join-Path $PSScriptRoot 'infinite.log'

# Item table layout (found by diffing saves and scanning memory; index 0 = first entry matched by the marker).
$Marker = '18 07 C4 84 00 00 00 00 20 00 00 00 01 00 00 00'
$MarkerDword = -2067527912                    # first 4 bytes of the marker as a signed int32
$Stride = 0x28
$Items = [ordered]@{
    rupees  = @{ Index = -9; Value = $RupeeCount }
    bombs   = @{ Index = 36; Value = $ItemCount }
    bombchu = @{ Index = 40; Value = $ItemCount }
    nuts    = @{ Index = 48; Value = $ItemCount }
    seeds   = @{ Index = 49; Value = $ItemCount }
    sticks  = @{ Index = 50; Value = $ItemCount }
    scrub   = @{ Index = 86; Value = $ItemCount }
}

function Log([string]$m) {
    $line = "{0:HH:mm:ss} {1}" -f (Get-Date), $m
    Write-Host $line
    Add-Content -Path $LogFile -Value $line
}

function Read-Lines([string]$path) {
    for ($i = 0; $i -lt 20; $i++) {
        try {
            $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
            try { $sr = New-Object IO.StreamReader($fs); $text = $sr.ReadToEnd() } finally { $fs.Dispose() }
            if ($text.Length -eq 0) { return @() }
            return $text -split "`r?`n"
        } catch { Start-Sleep -Milliseconds 100 }
    }
    return @()
}

function Send([string[]]$cmds) {
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $fs = [IO.File]::Open($CmdFile, 'Append', 'Write', 'ReadWrite')
            try { $sw = New-Object IO.StreamWriter($fs); foreach ($c in $cmds) { $sw.WriteLine($c) }; $sw.Flush() } finally { $fs.Dispose() }
            return
        } catch { Start-Sleep -Milliseconds 100 }
    }
    throw "Could not write to $CmdFile"
}

# Send commands and return the output lines the CLI printed for them. Waits until $until matches or $timeout seconds pass.
function Ask([string[]]$cmds, [string]$until, [int]$timeout = 60) {
    # The CLI's last line is always a bare "> " prompt that the next output continues, so start one line back.
    $before = [Math]::Max(0, (Read-Lines $OutFile).Count - 1)
    Send $cmds
    $sw = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 300
        $all = Read-Lines $OutFile
        $new = if ($all.Count -gt $before) { $all[$before..($all.Count - 1)] } else { @() }
        if (($new -join "`n") -match $until) { return $new }
    } while ($sw.Elapsed.TotalSeconds -lt $timeout)
    return $new
}

New-Item -ItemType Directory -Force $Work | Out-Null
Remove-Item $CmdFile, $OutFile -ErrorAction SilentlyContinue
if (-not (Test-Path $Image)) { throw "image.bin not found at $Image" }
if (-not (Test-Path $Dll)) {
    Log 'Trainer not built yet; building (one-time)...'
    & dotnet build (Join-Path $PSScriptRoot 'OotTrainer.Cli') -c Release --nologo -v q
    if (-not (Test-Path $Dll)) { throw 'Build failed.' }
}

$deadline = (Get-Date).AddMinutes($WaitMinutes)
Log "Waiting for the game (hookshot.exe). Start it and load your save."
while (-not (Get-Process -Name hookshot -ErrorAction SilentlyContinue)) {
    if ((Get-Date) -gt $deadline) { throw 'Timed out waiting for the game.' }
    Start-Sleep -Seconds 2
}
Log 'Game found. Attaching the trainer...'
Start-Sleep -Seconds 3   # let the guest memory finish mapping

$cli = Start-Process dotnet -ArgumentList @("`"$Dll`"", "`"$Image`"", "`"$Offsets`"", '--serve', "`"$CmdFile`"", "`"$OutFile`"") -WindowStyle Hidden -PassThru
try {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not ((Read-Lines $OutFile) -join "`n" -match 'Attached to')) {
        if ($cli.HasExited -or $sw.Elapsed.TotalSeconds -gt 90) { throw 'The trainer could not attach to the game.' }
        Start-Sleep -Milliseconds 500
    }
    Log ((Read-Lines $OutFile | Where-Object { $_ -match 'Attached to' } | Select-Object -First 1))

    # Find the item table. It only exists once a save is loaded, so retry until it shows up.
    $base = $null
    Log 'Looking for the item table (load your save and get into the world)...'
    while (-not $base) {
        if ($cli.HasExited) { throw 'The game closed.' }
        if ((Get-Date) -gt $deadline) { throw 'Timed out looking for the item table.' }
        $out = Ask @("aob $Marker") 'match\(es\)' 120
        $count = 0
        foreach ($l in $out) { if ($l -match '^\s*(?:>\s*)?(\d+) match\(es\)') { $count = [int]$Matches[1] } }
        $hits = @($out | ForEach-Object { if ($_ -match '^\s*0x([0-9A-Fa-f]+)\s*$') { [Convert]::ToUInt64($Matches[1], 16) } } | Sort-Object)
        if ($count -ge 100 -and $hits.Count -gt 0) {
            $set = @{}; foreach ($h in $hits) { $set[$h] = $true }
            foreach ($h in $hits) {
                if ($set.ContainsKey([uint64]($h + $Stride)) -and $set.ContainsKey([uint64]($h + 2 * $Stride)) -and -not $set.ContainsKey([uint64]($h - $Stride))) { $base = $h; break }
            }
        }
        if (-not $base) { Log "Item table not found yet ($count marker matches); retrying in 5s..."; Start-Sleep -Seconds 5 }
    }
    Log ("Item table starts at guest 0x{0:X} ({1} entries)." -f $base, $count)

    # Sanity check: every entry we are about to touch must carry the marker.
    $ok = @{}
    foreach ($name in $Items.Keys) {
        $entry = [uint64]([int64]$base + [int64]$Items[$name].Index * $Stride)
        $out = Ask @(("read 0x{0:X} i32" -f $entry)) '= -?\d+|unreadable' 15
        $ok[$name] = ($out -join "`n") -match [regex]::Escape("= $MarkerDword")
        if (-not $ok[$name]) { Log "WARNING: layout check failed for $name at 0x$('{0:X}' -f $entry); skipping it." }
    }

    $cmds = @()
    foreach ($name in $Items.Keys) {
        if (-not $ok[$name]) { continue }
        $entry = [uint64]([int64]$base + [int64]$Items[$name].Index * $Stride)
        foreach ($d in @(@{o = 0x1C; s = 'a' }, @{o = 0x20; s = 'b' })) {
            $v = "live_${name}_$($d.s)"
            $cmds += ("save $v i32 0x{0:X}" -f ($entry + $d.o))
            $cmds += ("lock $v $($Items[$name].Value)")
        }
    }
    if ($cmds.Count -eq 0) { throw 'No item passed the layout check; nothing was locked.' }
    $cmds += 'on inventory'
    $cmds += 'features'
    $out = Ask $cmds 'levitate' 60
    Log (($out | Where-Object { $_ -match 'inventory\s+on' } | Select-Object -First 1))
    Log ("Holding: " + (($Items.Keys | Where-Object { $ok[$_] } | ForEach-Object { "$_=$($Items[$_].Value)" }) -join ', '))
    Log 'Running. Close the game to stop (or press Ctrl+C).'

    while (-not $cli.HasExited -and (Get-Process -Name hookshot -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 2 }
    Log 'Game closed. Stopping.'
}
finally {
    if ($cli -and -not $cli.HasExited) { Stop-Process -Id $cli.Id -Force -ErrorAction SilentlyContinue }
}
