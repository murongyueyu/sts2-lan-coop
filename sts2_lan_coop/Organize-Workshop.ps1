# Local, post-exit folder organization. No downloads or subscription changes.
param([Parameter(Mandatory=$true)][string]$Plan,[switch]$Restore,[switch]$NoRestart)
$ErrorActionPreference='Stop'
function Full([string]$Path) { [IO.Path]::GetFullPath($Path).TrimEnd('\') }
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Under([string]$Root,[string]$Path) {
    $rootPath=Full $Root; $value=Full $Path
    if (-not $value.StartsWith($rootPath+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Path outside allowed root'}
    $cursor=$value
    while ($cursor -and $cursor.Length -ge $rootPath.Length) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {throw 'Links are not allowed'}
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
    return $value
}
function Relative([string]$Path) {
    if (-not $Path -or $Path.Length -gt 240 -or $Path.Contains('\')) {throw 'Invalid relative path'}
    foreach ($part in $Path.Split('/')) {
        if ($part -in @('','.','..') -or $part -match '[\x00-\x1f<>:"|?*]' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') {throw 'Invalid relative path'}
    }
    return $Path
}
function Files([string]$Root) {
    foreach ($entry in Get-ChildItem -LiteralPath $Root -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {throw 'Links are not allowed'}
        if ($entry.PSIsContainer) {Files $entry.FullName} else {$entry}
    }
}
function Verify([string]$Root,$Expected) {
    $actual=@(Files $Root)
    if ($actual.Count -ne @($Expected).Count) {throw 'Directory file list changed; close Steam updates and inspect again'}
    foreach ($file in $Expected) {
        $path=Under $Root (Join-Path $Root (Relative $file.Name))
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $file.Size -or (Hash $path) -ne $file.Hash) {throw 'File changed since confirmation'}
    }
}
$planPath=Full $Plan
$p=Get-Content -LiteralPath $planPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($p.Schema -ne 1 -or $p.Id -notmatch '^[a-f0-9]{32}$') {throw 'Invalid organization plan'}
$storage=Full $p.Storage
$transaction=Under $storage (Join-Path $storage ('organization\'+$p.Id))
if ($planPath -ne (Join-Path $transaction 'plan.json')) {throw 'Plan outside its local transaction'}
$workshop=Full $p.WorkshopRoot
if ([IO.Path]::GetFileName($workshop) -ne '2868840' -or [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($workshop)) -ne 'content' -or [IO.Path]::GetFileName([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($workshop))) -ne 'workshop') {throw 'Not the STS2 Workshop content root'}
if ($storage.StartsWith($workshop+'\',[StringComparison]::OrdinalIgnoreCase) -or $workshop.StartsWith($storage+'\',[StringComparison]::OrdinalIgnoreCase) -or $storage -eq $workshop) {throw 'Backup must be outside Workshop'}
$game=Full $p.GameRoot
if (-not (Test-Path -LiteralPath (Join-Path $game 'SlayTheSpire2.exe') -PathType Leaf)) {throw 'Game executable not found'}
$journalPath=Join-Path $transaction 'journal.json'
$journal=@{State='prepared';Units=@();Error=''}
if ($Restore) {
    # A corrupt journal must remain available for manual recovery. Read it before
    # entering the handler that records new failures.
    $old=Get-Content -LiteralPath $journalPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($old.State -notin @('applied','applying','failed') -or $null -eq $old.Units) {throw 'Journal is not recoverable'}
    $old.PSObject.Properties | ForEach-Object {$journal[$_.Name]=$_.Value}
} elseif (Test-Path -LiteralPath $journalPath) {
    throw 'Transaction already started; its existing journal was preserved'
}
function Save-Journal {
    $journal | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath ($journalPath+'.tmp') -Encoding UTF8
    Move-Item -LiteralPath ($journalPath+'.tmp') -Destination $journalPath -Force
}
function Paths($Unit) {
    if ($Unit.Item -notmatch '^[0-9]+$' -or $Unit.ModId.Contains('/')) {throw 'Invalid Workshop item or mod id'}
    $source=Under $workshop (Join-Path $workshop $Unit.Item)
    $destination=Under $source (Join-Path $source (Relative $Unit.ModId))
    return @{Source=$source;Destination=$destination}
}
function Recover {
    $reverse=@($journal.Units)
    [array]::Reverse($reverse)
    foreach ($unit in $reverse) {
        $paths=Paths $unit
        foreach ($child in @($unit.Children)) {
            $source=Under $paths.Source (Join-Path $paths.Source (Relative $child))
            $target=Under $paths.Destination (Join-Path $paths.Destination (Relative $child))
            if (-not (Test-Path -LiteralPath $target)) {continue}
            if (Test-Path -LiteralPath $source) {throw 'Original path recreated; restore stopped, backup retained'}
            $expected=@($unit.Files | Where-Object {($_.Name.Split('/')[0]) -eq $child})
            $actual=@(if (Test-Path -LiteralPath $target -PathType Container) {Files $target} else {Get-Item -LiteralPath $target})
            if ($actual.Count -ne $expected.Count) {throw 'Organized files changed; restore stopped'}
            foreach ($file in $expected) {
                $filePath=Under $paths.Destination (Join-Path $paths.Destination (Relative $file.Name))
                if (-not (Test-Path -LiteralPath $filePath -PathType Leaf) -or (Hash $filePath) -ne $file.Hash) {throw 'Organized file changed; restore stopped'}
            }
            Move-Item -LiteralPath $target -Destination $source
        }
        if ((Test-Path -LiteralPath $paths.Destination) -and @(Get-ChildItem -LiteralPath $paths.Destination -Force).Count -eq 0) {
            # Already checked beneath the Workshop item; only remove an empty directory.
            Remove-Item -LiteralPath $paths.Destination
        }
    }
    $journal.State='restored'; Save-Journal
}
try {
    if ($p.GamePid -gt 0) {
        $gameProcess=Get-Process -Id $p.GamePid -ErrorAction SilentlyContinue
        if ($gameProcess -and $gameProcess.StartTime.ToUniversalTime().Ticks -eq $p.GameStartedUtcTicks) {$gameProcess.WaitForExit()}
    }
    if ($Restore) {
        Recover
    } else {
        if (@($p.Units).Count -eq 0 -or @($p.Units).Count -gt 1024) {throw 'Invalid number of Workshop items'}
        $seen=@{}
        foreach ($unit in $p.Units) {
            $paths=Paths $unit
            if ($seen.ContainsKey($unit.Item)) {throw 'Duplicate Workshop item'}
            $seen[$unit.Item]=$true
            if (Test-Path -LiteralPath $paths.Destination) {throw 'Mod folder already exists; inspect again'}
            Verify $paths.Source $unit.Files
        }
        # Back up every unit before moving any live files.
        foreach ($unit in $p.Units) {
            $paths=Paths $unit
            $backup=Under $transaction (Join-Path $transaction ('backup\'+$unit.Item))
            [IO.Directory]::CreateDirectory($backup) | Out-Null
            foreach ($file in $unit.Files) {
                $source=Under $paths.Source (Join-Path $paths.Source (Relative $file.Name))
                $destination=Under $backup (Join-Path $backup (Relative $file.Name))
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
                Copy-Item -LiteralPath $source -Destination $destination
            }
            Verify $backup $unit.Files
        }
        $journal.State='applying'; Save-Journal
        foreach ($unit in $p.Units) {
            $paths=Paths $unit
            Verify $paths.Source $unit.Files
            $children=@(Get-ChildItem -LiteralPath $paths.Source -Force | ForEach-Object {$_.Name})
            if ($children -contains $unit.ModId) {throw 'Destination appeared during organization'}
            $journal.Units+=@{Item=$unit.Item;ModId=$unit.ModId;Files=$unit.Files;Children=$children}; Save-Journal
            [IO.Directory]::CreateDirectory($paths.Destination) | Out-Null
            foreach ($child in $children) {
                $source=Under $paths.Source (Join-Path $paths.Source (Relative $child))
                $destination=Under $paths.Destination (Join-Path $paths.Destination (Relative $child))
                if (Test-Path -LiteralPath $destination) {throw 'Destination appeared during organization'}
                Move-Item -LiteralPath $source -Destination $destination
            }
            Verify $paths.Destination $unit.Files
            if (@(Get-ChildItem -LiteralPath $paths.Source -Force).Count -ne 1) {throw 'Workshop item changed during organization'}
        }
        $journal.State='applied'; Save-Journal
    }
    if (-not $NoRestart) {Start-Process -FilePath (Join-Path $game 'SlayTheSpire2.exe') -WorkingDirectory $game -WindowStyle Hidden}
} catch {
    $failure=$_.Exception.Message
    try {if ($journal.State -eq 'applying' -and -not $Restore) {Recover}} catch {$failure+='; restore needs attention: '+$_.Exception.Message}
    $journal.Error=$failure; $journal.State='failed'; Save-Journal
    exit 1
}
