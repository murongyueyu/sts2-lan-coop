# Production post-exit installer. Host data never selects commands or absolute destination paths.
param([Parameter(Mandatory=$true)][string]$Plan, [switch]$Restore, [switch]$NoRestart)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Full([string]$Path) { [IO.Path]::GetFullPath($Path).TrimEnd('\') }
function Under([string]$Root,[string]$Path) {
    $value=Full $Path
    if (-not $value.StartsWith((Full $Root)+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Path outside allowed root' }
    $cursor=$value
    while ($cursor -and $cursor.Length -ge (Full $Root).Length) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Links are not allowed' }
        $cursor=[IO.Path]::GetDirectoryName($cursor)
    }
    return $value
}
function Relative([string]$Path) {
    if ($Path.Length -eq 0 -or $Path.Length -gt 240 -or $Path.Contains('\')) { throw 'Invalid archive path' }
    foreach ($part in $Path.Split('/')) {
        if ($part -in @('','.','..') -or $part -match '[\x00-\x1f<>:"|?*]' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') { throw 'Invalid archive path' }
        if ($part -in @('.git','.svn','.hg','.ssh','.aws','.codex','steam_settings','credentials.json','tokens.json','.env') -or $part.StartsWith('.env.',[StringComparison]::OrdinalIgnoreCase)) {throw 'Private configuration is not a mod resource'}
    }
    $leaf=[IO.Path]::GetFileName($Path)
    if ($leaf -in @('sts2.dll','GodotSharp.dll','0Harmony.dll','steam_api.dll','steam_api64.dll') -or [IO.Path]::GetExtension($leaf) -in @('.exe','.com','.cmd','.bat','.ps1','.vbs','.lnk','.url','.reg','.save','.key','.pem','.log','.pdb','.user')) { throw 'Forbidden executable/account file' }
    return $Path
}
$planPath=Full $Plan
$p=Get-Content -LiteralPath $planPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($p.Schema -ne 1 -or $p.Id -notmatch '^[a-f0-9]{32}$') { throw 'Invalid local transaction' }
$storage=Full $p.Storage
$transaction=Under $storage (Join-Path $storage ('transactions\'+$p.Id))
if ($planPath -ne (Join-Path $transaction 'plan.json')) { throw 'Plan must be in local transaction directory' }
$game=Full $p.GameRoot
$mods=Join-Path $game 'mods'
$settings=Full $p.Settings
if ([IO.Path]::GetFileName($settings) -ne 'settings.save' -or $settings.StartsWith($mods+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid settings destination' }
if ($storage.StartsWith($mods+'\',[StringComparison]::OrdinalIgnoreCase) -or $transaction.StartsWith($mods+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Backup must be outside mod scan' }
$journalPath=Join-Path $transaction 'journal.json'
$journal=@{State='prepared'; Moves=@(); Installed=@(); SettingsApplied=$false; SettingsAfter=''; Error=''}
function Save-Journal { $journal | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath ($journalPath+'.tmp') -Encoding UTF8; Move-Item -LiteralPath ($journalPath+'.tmp') -Destination $journalPath -Force }
function Recover {
    # Refuse to overwrite changes made after this transaction.
    foreach ($installed in @($journal.Installed)) {
        $target=Under $mods $installed.Path
        if (Test-Path -LiteralPath $target) {
            $items=@(Get-ChildItem -LiteralPath $target -Recurse -Force)
            if (@($items|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count -gt 0) {throw 'Installed directory contains a link'}
            if (@($items|Where-Object {-not $_.PSIsContainer}).Count -ne @($installed.Files).Count) {throw 'Installed directory contains new files'}
            foreach ($file in $installed.Files) {
                $path=Under $target (Join-Path $target (Relative $file.Name))
                if (-not (Test-Path -LiteralPath $path) -or (Hash $path) -ne $file.Hash) {throw 'Installed files changed; automatic restore refused'}
            }
        }
    }
    foreach ($move in @($journal.Moves)) {
        $original=Under $mods $move.From; $backup=Under $transaction $move.To
        if ((Test-Path -LiteralPath $backup) -and (Test-Path -LiteralPath $original)) {throw 'Original mod directory recreated; restore refused'}
    }
    if ($journal.SettingsApplied) {
        $current=Hash $settings
        if ($current -ne $journal.SettingsAfter -and $current -ne $p.SettingsHash) {
            $now=Get-Content -LiteralPath $settings -Raw -Encoding UTF8|ConvertFrom-Json
            $after=Get-Content -LiteralPath (Join-Path $transaction 'settings.after') -Raw -Encoding UTF8|ConvertFrom-Json
            $before=Get-Content -LiteralPath (Join-Path $transaction 'settings.before') -Raw -Encoding UTF8|ConvertFrom-Json
            $list=@($now.mod_settings.mod_list)
            foreach ($change in $p.SettingsChanges) {
                $source=$p.SourceTokens.($change.Source.ToString())|ConvertFrom-Json
                $currentEntries=@($list|Where-Object {$_.id -eq $change.Id -and $_.source -eq $source})
                $expectedEntries=@($after.mod_settings.mod_list|Where-Object {$_.id -eq $change.Id -and $_.source -eq $source})
                if ($currentEntries.Count -ne $expectedEntries.Count -or @($currentEntries|Where-Object {$_.is_enabled -ne $change.Enabled}).Count -gt 0) {throw 'Affected mod settings changed; automatic restore refused'}
                $list=@($list|Where-Object {-not ($_.id -eq $change.Id -and $_.source -eq $source)})
                $list+=@($before.mod_settings.mod_list|Where-Object {$_.id -eq $change.Id -and $_.source -eq $source})
            }
            $now.mod_settings.mod_list=$list
            $now|ConvertTo-Json -Depth 100|Set-Content -LiteralPath ($settings+'.modsync.tmp') -Encoding UTF8
            Move-Item -LiteralPath ($settings+'.modsync.tmp') -Destination $settings -Force
        } elseif ($current -ne $p.SettingsHash) { Copy-Item -LiteralPath (Join-Path $transaction 'settings.before') -Destination $settings -Force }
        $journal.SettingsApplied=$false; Save-Journal
    }
    foreach ($installed in @($journal.Installed)) {
        $target=Under $mods $installed.Path
        if (Test-Path -LiteralPath $target) {
            foreach ($file in $installed.Files) {
                $path=Under $target (Join-Path $target (Relative $file.Name))
                if (-not (Test-Path -LiteralPath $path) -or (Hash $path) -ne $file.Hash) { throw 'Installed files changed; automatic restore refused' }
            }
            $actual=@(Get-ChildItem -LiteralPath $target -Recurse -File -Force)
            if ($actual.Count -ne @($installed.Files).Count) { throw 'Installed directory contains new files; automatic restore refused' }
            Move-Item -LiteralPath $target -Destination (Join-Path $transaction ('restored-'+[guid]::NewGuid().ToString('N')))
        }
    }
    foreach ($move in @($journal.Moves)) {
        $original=Under $mods $move.From; $backup=Under $transaction $move.To
        if (Test-Path -LiteralPath $backup) {
            if (Test-Path -LiteralPath $original) { throw 'Original mod directory has been recreated; restore refused' }
            Move-Item -LiteralPath $backup -Destination $original
        }
    }
    $journal.State='restored'; Save-Journal
}
try {
    if ($p.GamePid -gt 0) {
        $process=Get-Process -Id $p.GamePid -ErrorAction SilentlyContinue
        if ($process -and $process.StartTime.ToUniversalTime().Ticks -eq $p.GameStartedUtcTicks) { $process.WaitForExit() }
    }
    if ($Restore) {
        $old=Get-Content -LiteralPath $journalPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $journal=@{}; $old.PSObject.Properties | ForEach-Object { $journal[$_.Name]=$_.Value }
        Recover
        $pending=Join-Path $storage 'pending.json'
        if (Test-Path -LiteralPath $pending) {
            $pendingPlan=Get-Content -LiteralPath $pending -Raw -Encoding UTF8|ConvertFrom-Json
            if ($pendingPlan.Id -eq $p.Id) {Remove-Item -LiteralPath $pending}
        }
        if (-not $NoRestart) {Start-Process -FilePath (Join-Path $game 'SlayTheSpire2.exe') -WorkingDirectory $game -WindowStyle Hidden}
        exit 0
    }
    if (Test-Path -LiteralPath $journalPath) { throw 'Transaction already started; use Restore to recover it' }
    if ((Hash $settings) -ne $p.SettingsHash) { throw 'Settings changed since confirmation; reopen the game and inspect again' }
    $staged=@(); $seen=@{}
    foreach ($install in $p.Installs) {
        if ($install.Id -eq 'sts2_lan_coop') { throw 'LAN core cannot be replaced' }
        $target=Under $mods (Join-Path $mods (Relative $install.Target))
        if ($seen.ContainsKey($target)) { throw 'Duplicate target directory' }; $seen[$target]=$true
        if ((Test-Path -LiteralPath $target) -and $target -notin @($install.BackupDirectories)) { throw 'Target directory already exists without backup' }
        $archive=Under (Join-Path $storage 'cache') $install.Archive
        if ((Hash $archive) -ne $install.ArchiveHash) { throw 'Archive hash mismatch' }
        $stage=Under $transaction (Join-Path $transaction ('stage-'+$staged.Count))
        New-Item -ItemType Directory -Path $stage | Out-Null
        $expected=@{}; [long]$total=0
        foreach ($file in $install.Files) {
            $name=Relative $file.Name
            if ($expected.ContainsKey($name) -or $file.Size -lt 0 -or $file.Hash -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid file manifest' }
            $expected[$name]=$file; $total+=$file.Size
        }
        if ($total -gt 4294967296 -or $expected.Count -eq 0 -or $expected.Count -gt 30000) { throw 'Package size limit' }
        $zip=[IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $visited=@{}
            foreach ($entry in $zip.Entries) {
                $name=Relative $entry.FullName
                if ($visited.ContainsKey($name) -or -not $expected.ContainsKey($name) -or $entry.Length -ne $expected[$name].Size -or (($entry.ExternalAttributes -shr 16) -band 61440) -eq 40960) { throw 'Unexpected archive entry' }
                $visited[$name]=$true
                $destination=Under $stage (Join-Path $stage $name)
                New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
                $inputStream=$entry.Open();$outputStream=[IO.File]::Open($destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
                try {
                    $buffer=New-Object byte[] 65536;[long]$count=0
                    while(($read=$inputStream.Read($buffer,0,$buffer.Length)) -gt 0) {
                        $count+=$read;if($count -gt $expected[$name].Size){throw 'Uncompressed entry exceeds declared size'}
                        $outputStream.Write($buffer,0,$read)
                    }
                } finally {$outputStream.Dispose();$inputStream.Dispose()}
                if ((Get-Item -LiteralPath $destination).Length -ne $expected[$name].Size -or (Hash $destination) -ne $expected[$name].Hash) { throw 'File hash mismatch' }
            }
            if ($visited.Count -ne $expected.Count) { throw 'Incomplete archive' }
        } finally { $zip.Dispose() }
        $staged+=@{Stage=$stage;Target=$target;Install=$install}
    }
    Copy-Item -LiteralPath $settings -Destination (Join-Path $transaction 'settings.before')
    $journal.State='applying'; Save-Journal
    foreach ($stage in $staged) {
        foreach ($old in $stage.Install.BackupDirectories) {
            $original=Under $mods $old
            if ($original -eq $mods -or (Get-ChildItem -LiteralPath $original -Filter 'sts2_lan_coop.json' -Recurse -File)) { throw 'Cannot move LAN core' }
            foreach ($file in $stage.Install.BackupFiles.$old) {
                $oldFile=Under $original (Join-Path $original (Relative $file.Name))
                if (-not (Test-Path -LiteralPath $oldFile) -or (Hash $oldFile) -ne $file.Hash) { throw 'Original files changed since confirmation' }
            }
            $backup=Join-Path $transaction ('backup-'+$journal.Moves.Count)
            $journal.Moves+=@{From=$original;To=$backup}; Save-Journal
            Move-Item -LiteralPath $original -Destination $backup
        }
        $journal.Installed+=@{Path=$stage.Target;Files=$stage.Install.Files}; Save-Journal
        Move-Item -LiteralPath $stage.Stage -Destination $stage.Target
    }
    $config=Get-Content -LiteralPath $settings -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $config.mod_settings) { throw 'Missing mod settings' }
    $list=@($config.mod_settings.mod_list)
    foreach ($change in $p.SettingsChanges) {
        if ($change.Id -eq 'sts2_lan_coop' -and -not $change.Enabled) { throw 'Cannot disable LAN core' }
        if ($change.Source -notin @(1,2)) { throw 'Unknown mod source' }
        $source=$p.SourceTokens.($change.Source.ToString()) | ConvertFrom-Json
        $list=@($list | Where-Object { -not ($_.id -eq $change.Id -and ($_.source -eq $source -or $_.source -eq $change.Source)) })
        $list+=@{id=$change.Id;source=$source;is_enabled=[bool]$change.Enabled}
    }
    $config.mod_settings.mod_list=$list
    $newSettings=Join-Path $transaction 'settings.after'
    $config | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $newSettings -Encoding UTF8
    $journal.SettingsAfter=Hash $newSettings; $journal.SettingsApplied=$true; Save-Journal
    Copy-Item -LiteralPath $newSettings -Destination ($settings+'.modsync.tmp') -Force
    Move-Item -LiteralPath ($settings+'.modsync.tmp') -Destination $settings -Force
    $journal.State='applied'; Save-Journal
    Copy-Item -LiteralPath $planPath -Destination (Join-Path $storage 'pending.json') -Force
    if (-not $NoRestart) {
        Start-Process -FilePath (Join-Path $game 'SlayTheSpire2.exe') -WorkingDirectory $game -WindowStyle Hidden
    }
} catch {
    $failure=$_.Exception.Message
    try {
        if ($journal.State -eq 'applying') { Recover }
    } catch { $failure+='; restore needs attention: '+$_.Exception.Message }
    $journal.Error=$failure; $journal.State='failed'; Save-Journal
    exit 1
}
