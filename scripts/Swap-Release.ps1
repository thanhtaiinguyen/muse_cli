[CmdletBinding()]
param([string]$WorkspaceRoot = '', [switch]$ScheduledRun)
$ErrorActionPreference = 'Stop'

# Invoked every minute by the MuseSwapRelease scheduled task, which
# Publish-Desktop.ps1 registers when a publish happens while Muse is
# running. As soon as no Muse process is running from dist anymore, the
# newest validated staged build (dist\Muse.stage.*) is swapped into
# dist\Muse: the previous release is renamed to Muse.old.* first and
# deleted only after the new one is in place. Nothing is force-closed
# and no folder outside dist is ever touched.
if (!$WorkspaceRoot) { $WorkspaceRoot = Split-Path -Parent $PSScriptRoot }
$workspaceRoot = [IO.Path]::GetFullPath($WorkspaceRoot)
. (Join-Path $PSScriptRoot 'Release-Validation.ps1')
$distributionRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot 'dist'))
$artifactRoot = Join-Path $workspaceRoot 'artifacts'
$currentRelease = Join-Path $distributionRoot 'Muse'
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
$logPath = Join-Path $artifactRoot 'swap-release.log'

function Write-SwapLog([string]$Message) {
    Add-Content -LiteralPath $logPath -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' ' + $Message)
}

function Test-PersonalData([string]$Path) {
    $found = @(Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -in @('accounts.json','api_key','keys.json','requests.db','ui_config.json') -or $_.DirectoryName -match 'browser-sessions|local-conversations' })
    return $found.Count -gt 0
}

function Remove-ReleaseDir([string]$Path) {
    $Path = Assert-MuseReleasePath $Path $distributionRoot
    if (Test-PersonalData $Path) { Write-SwapLog "kept release containing personal data: $Path"; return }
    Remove-Item -LiteralPath $Path -Recurse -Force
    Write-SwapLog "removed: $Path"
}

$lockStream = $null
try { $lockStream = [IO.File]::Open((Join-Path $artifactRoot 'swap-release.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
catch { return } # Another swap run is active.
try {
    $distPrefix = $distributionRoot + [IO.Path]::DirectorySeparatorChar
    $running = @(Get-Process -Name Muse -ErrorAction SilentlyContinue | Where-Object { !$_.Path -or $_.Path.StartsWith($distPrefix, [StringComparison]::OrdinalIgnoreCase) })
    $candidates = @(Get-ChildItem -LiteralPath $distributionRoot -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^Muse\.stage\.[a-f0-9]{32}$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'release-verified.json')) })
    if (!$candidates.Count) { if ($ScheduledRun) { Complete-MuseSwapTask $workspaceRoot }; return }
    if ($running.Count) { return } # Muse still in use; try again next minute.
    $stages = @($candidates | Where-Object { Test-MuseReleaseValidation $_.FullName $distributionRoot } | Sort-Object LastWriteTime -Descending)
    if (!$stages.Count) { if ($ScheduledRun) { Complete-MuseSwapTask $workspaceRoot }; return }
    $stage = $stages[0]
    $null = Assert-MuseReleasePath $stage.FullName $distributionRoot
    $null = Assert-MuseReleasePath $currentRelease $distributionRoot
    if (Test-PersonalData $stage.FullName) { Write-SwapLog 'Staged release contains personal data; keeping current release.'; return }
    if ((Test-Path -LiteralPath $currentRelease) -and (Test-PersonalData $currentRelease)) { Write-SwapLog 'Current release contains personal data; leaving it untouched.'; return }

    $backupPath = $null
    if (Test-Path -LiteralPath $currentRelease) {
        $backupPath = Join-Path $distributionRoot ('Muse.old.' + [Guid]::NewGuid().ToString('N'))
        Move-Item -LiteralPath $currentRelease -Destination $backupPath
    }
    try { Move-Item -LiteralPath $stage.FullName -Destination $currentRelease }
    catch {
        if ($backupPath -and !(Test-Path -LiteralPath $currentRelease)) { Move-Item -LiteralPath $backupPath -Destination $currentRelease }
        throw
    }
    Write-SwapLog "swapped $($stage.FullName) into $currentRelease"
    if ($backupPath) { try { Remove-ReleaseDir $backupPath } catch { Write-SwapLog "old release cleanup failed: $($_.Exception.Message)" } }
    foreach ($backup in @(Get-ChildItem -LiteralPath $distributionRoot -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^Muse\.old\.[a-f0-9]{32}$' })) {
        try { Remove-ReleaseDir $backup.FullName } catch { Write-SwapLog "cleanup failed for $($backup.FullName): $($_.Exception.Message)" }
    }
    foreach ($extra in $stages | Select-Object -Skip 1) {
        try { Remove-ReleaseDir $extra.FullName } catch { Write-SwapLog "stale stage cleanup failed: $($_.Exception.Message)" }
    }
    if ($ScheduledRun) { Complete-MuseSwapTask $workspaceRoot }
}
catch { Write-SwapLog "error: $($_.Exception.Message)" }
finally { if ($lockStream) { $lockStream.Dispose() } }
