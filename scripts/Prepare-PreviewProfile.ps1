[CmdletBinding()]
param(
    [string]$SourceRoot = (Join-Path $env:LOCALAPPDATA 'Muse2Api.WinForms'),
    [string]$DestinationRoot = (Join-Path $env:LOCALAPPDATA 'Muse2Api.WinForms-Preview-Net10')
)
$ErrorActionPreference = 'Stop'
$sourceProfile = [IO.Path]::GetFullPath($SourceRoot)
$previewProfile = [IO.Path]::GetFullPath($DestinationRoot)
$productionProfile = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Muse2Api.WinForms'))
if ($previewProfile.Equals($productionProfile, [StringComparison]::OrdinalIgnoreCase) -or
    $previewProfile.StartsWith($productionProfile + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    $previewProfile.Equals($sourceProfile, [StringComparison]::OrdinalIgnoreCase) -or
    $previewProfile.StartsWith($sourceProfile + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Preview must be separate from the original profile.'
}
function Assert-ProfilePath([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.Equals($Root, [StringComparison]::OrdinalIgnoreCase) -and
        !$full.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe profile path.' }
    for ($cursor = $full; $cursor -and $cursor.Length -ge $Root.Length; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Profile links are not supported.' }
    }
    return $full
}
$copied = 0; $retained = 0; $sourceHashes = @{}
function Copy-PreviewFile([string]$Relative) {
    $originalFile = Assert-ProfilePath (Join-Path $sourceProfile $Relative) $sourceProfile
    $previewFile = Assert-ProfilePath (Join-Path $previewProfile $Relative) $previewProfile
    if (!(Test-Path -LiteralPath $originalFile -PathType Leaf)) { return }
    $sourceHashes[$originalFile] = (Get-FileHash -LiteralPath $originalFile -Algorithm SHA256).Hash
    if (Test-Path -LiteralPath $previewFile) { $script:retained++; return }
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($previewFile)) -Force
    Copy-Item -LiteralPath $originalFile -Destination $previewFile
    $script:copied++
}
foreach ($relative in @('Gateway/accounts.json', 'Gateway/api_key', 'Gateway/ui_theme.txt')) { Copy-PreviewFile $relative }
$browserStates = Assert-ProfilePath (Join-Path $sourceProfile 'Gateway/browser-sessions') $sourceProfile
if (Test-Path -LiteralPath $browserStates -PathType Container) {
    foreach ($state in Get-ChildItem -LiteralPath $browserStates -File -Filter '*.json') { Copy-PreviewFile ('Gateway/browser-sessions/' + $state.Name) }
}
foreach ($file in $sourceHashes.Keys) {
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $sourceHashes[$file]) { throw 'Original profile changed during preparation; retry with Muse closed.' }
}
# Copy only saved-login material; do not clone browser caches, histories or workspace selection.
# The preview app migrates these copies to DPAPI on first use, never the source files.
Write-Output ('PREVIEW_PROFILE=' + $previewProfile)
Write-Output ('SAVED_LOGIN_FILES_COPIED=' + $copied)
Write-Output ('EXISTING_PREVIEW_FILES_RETAINED=' + $retained)
Write-Output 'ORIGINAL_PROFILE_UNCHANGED=True'
