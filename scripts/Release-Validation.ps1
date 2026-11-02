# Shared by the publisher and deferred swapper. No mutations on import.
function Complete-MuseSwapTask([string]$WorkspaceRoot) {
    # Unregister only this workspace's job, including the old visible launcher.
    # A failed/missing scheduler never invalidates an already verified release.
    try {
        $task = Get-ScheduledTask -TaskName 'MuseSwapRelease' -TaskPath '\' -ErrorAction SilentlyContinue
        if (!$task) { return }
        $swap = [IO.Path]::GetFullPath((Join-Path $WorkspaceRoot 'scripts/Swap-Release.ps1'))
        $launcher = [IO.Path]::GetFullPath((Join-Path $WorkspaceRoot 'scripts/Run-Swap-Hidden.vbs'))
        $owned = @($task.Actions | Where-Object { $_.Arguments -and [IO.Path]::GetFileName($_.Execute) -in @('powershell.exe','wscript.exe') -and
            ($_.Arguments.IndexOf('"' + $swap + '"', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $_.Arguments.IndexOf('"' + $launcher + '"', [StringComparison]::OrdinalIgnoreCase) -ge 0) })
        if ($owned.Count) { Unregister-ScheduledTask -TaskName 'MuseSwapRelease' -TaskPath '\' -Confirm:$false -ErrorAction Stop }
    }
    catch { Write-Warning 'Không dọn được tác vụ đổi bản; bản cài đặt và dữ liệu cá nhân vẫn được giữ.' }
}

function Get-MuseFileHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
    finally { $hasher.Dispose(); $stream.Dispose() }
}

function Assert-MuseReleasePath([string]$Path, [string]$DistributionRoot) {
    $root = [IO.Path]::GetFullPath($DistributionRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $root) { throw "Unsafe release path: $resolved" }
    if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'dist must not be a junction/link.' }
    if (Test-Path -LiteralPath $resolved) {
        if ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Release is a link: $resolved" }
        $links = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
        if ($links.Count) { throw "Release contains links: $resolved" }
    }
    return $resolved
}

function Write-MuseReleaseValidation([string]$Path, [string]$DistributionRoot, [string]$Version) {
    $releasePath = Assert-MuseReleasePath $Path $DistributionRoot
    if (!(Test-Path -LiteralPath (Join-Path $releasePath 'Muse.exe'))) { throw 'Validated release needs Muse.exe.' }
    $manifestPath = Join-Path $releasePath 'release-verified.json'
    $files = @(Get-ChildItem -LiteralPath $releasePath -File -Recurse -Force | Where-Object { $_.FullName -ne $manifestPath } | Sort-Object FullName | ForEach-Object {
        @{ Path = $_.FullName.Substring($releasePath.Length + 1); Sha256 = (Get-MuseFileHash $_.FullName) }
    })
    @{ Schema = 1; Version = $Version; VerifiedAtUtc = [DateTime]::UtcNow.ToString('o'); Files = $files } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
}

function Test-MuseReleaseValidation([string]$Path, [string]$DistributionRoot) {
    try {
        $releasePath = Assert-MuseReleasePath $Path $DistributionRoot
        $manifestPath = Join-Path $releasePath 'release-verified.json'
        if (!(Test-Path -LiteralPath $manifestPath) -or (Get-Item -LiteralPath $manifestPath).Length -gt 2097152) { return $false }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.Schema -ne 1 -or !$manifest.Version -or !$manifest.Files) { return $false }
        if (!(Test-Path -LiteralPath (Join-Path $releasePath 'Muse.exe'))) { return $false }
        $actual = @(Get-ChildItem -LiteralPath $releasePath -File -Recurse -Force | Where-Object { $_.FullName -ne $manifestPath })
        $expected = @{}
        foreach ($entry in $manifest.Files) {
            if (!$entry.Path -or $entry.Sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $expected.ContainsKey([string]$entry.Path)) { return $false }
            $expected[[string]$entry.Path] = [string]$entry.Sha256
        }
        if ($actual.Count -ne $expected.Count) { return $false }
        foreach ($file in $actual) {
            $relative = $file.FullName.Substring($releasePath.Length + 1)
            if (!$expected.ContainsKey($relative) -or (Get-MuseFileHash $file.FullName) -ne $expected[$relative]) { return $false }
        }
        return $true
    }
    catch { return $false }
}
