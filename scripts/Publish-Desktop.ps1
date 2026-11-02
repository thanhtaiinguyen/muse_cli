[CmdletBinding()]
param([string]$DotNet = 'dotnet', [switch]$Preview)
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$distributionRoot = [IO.Path]::GetFullPath((Join-Path $workspaceRoot 'dist'))
$currentRelease = Join-Path $distributionRoot 'Muse'
$stagingRelease = Join-Path $distributionRoot ('Muse.stage.' + [Guid]::NewGuid().ToString('N'))
$artifactRoot = Join-Path $workspaceRoot 'artifacts'
. (Join-Path $PSScriptRoot 'Release-Validation.ps1')
$localSdk = Join-Path $artifactRoot 'dotnet10/dotnet.exe'
if ($DotNet -eq 'dotnet' -and (Test-Path -LiteralPath $localSdk)) { $DotNet = $localSdk }
$originalBrowserTests = $env:MUSE_BROWSER_TESTS
$originalHeadless = $env:MUSE2API_HEADLESS
$originalBrowser = $env:MUSE2API_CHROMIUM_PATH
$originalComputerTests = $env:MUSE_COMPUTER_TESTS

function Assert-ReleasePath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $distributionRoot -or $resolved -eq $workspaceRoot) { throw "Unsafe release path: $resolved" }
    if ((Get-Item -LiteralPath $distributionRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'dist must not be a junction/link.' }
    if (Test-Path -LiteralPath $resolved) {
        if ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Release is a link: $resolved" }
        $links = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
        if ($links.Count) { throw "Release contains links; refusing cleanup: $resolved" }
    }
    return $resolved
}

function Queue-ReleaseSwap {
    $launcher = Join-Path $workspaceRoot 'scripts/Run-Swap-Hidden.vbs'
    $taskAction = New-ScheduledTaskAction -Execute (Join-Path $env:WINDIR 'System32/wscript.exe') -Argument ('//B //NoLogo "' + $launcher + '"')
    $taskTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName 'MuseSwapRelease' -Action $taskAction -Trigger $taskTrigger -Description 'Swaps a validated staged Muse release into dist\Muse once Muse has exited.' -Force | Out-Null
    Write-Output ('RELEASE_PENDING=' + $stagingRelease)
    Write-Output ('VERSION=' + $expectedVersion)
    Write-Output ('PREVIEW=' + [bool]$Preview)
    Write-Output 'SWAP_MODE=deferred-until-muse-exits'
    Write-Output 'Bản mới đã kiểm chứng và đang chờ thay an toàn. Đóng rồi mở lại Muse; không cần chạy lại publish.'
}

Push-Location -LiteralPath $workspaceRoot
$releaseLock = $null
try {
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    $releaseLock = [IO.File]::Open((Join-Path $artifactRoot 'swap-release.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $env:MUSE_BROWSER_TESTS = '1'; $env:MUSE2API_HEADLESS = '1'
    $env:MUSE_COMPUTER_TESTS = '0' # Interactive fixtures require a separate human test run.
    if (!$env:MUSE2API_CHROMIUM_PATH -and (Test-Path -LiteralPath 'C:\Program Files\Google\Chrome\Application\chrome.exe')) { $env:MUSE2API_CHROMIUM_PATH = 'C:\Program Files\Google\Chrome\Application\chrome.exe' }
    & $DotNet test tests/Muse2Api.Tests/Muse2Api.Tests.csproj -c Release --artifacts-path $artifactRoot --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; old releases were kept.' }
    & $DotNet build tests/Muse2Api.WinForms.Checks/Muse2Api.WinForms.Checks.csproj -c Release --artifacts-path $artifactRoot --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Native offscreen checks did not build; old releases were kept.' }
    & $DotNet (Join-Path $artifactRoot 'bin/Muse2Api.WinForms.Checks/release/Muse2Api.WinForms.Checks.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Native offscreen checks failed; old releases were kept.' }
    & $DotNet test tests/Muse2Api.Computer.Tests/Muse2Api.Computer.Tests.csproj -c Release --artifacts-path $artifactRoot --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Windows computer tests failed; old releases were kept.' }
    & $DotNet publish src/Muse2Api.WinForms/Muse2Api.WinForms.csproj -c Release -r win-x64 --self-contained true --artifacts-path $artifactRoot -o $stagingRelease -p:DebugType=None -p:DebugSymbols=false --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed; old releases were kept.' }
    $stagingRelease = Assert-ReleasePath $stagingRelease
    if ($Preview) { Set-Content -LiteralPath (Join-Path $stagingRelease 'muse-preview.profile') -Value 'Muse2Api.WinForms-Preview-Net10' -Encoding ascii }
    $executable = Join-Path $stagingRelease 'Muse.exe'
    [xml]$project = Get-Content -LiteralPath (Join-Path $workspaceRoot 'src/Muse2Api.WinForms/Muse2Api.WinForms.csproj') -Raw
    $expectedVersion = [string]$project.Project.PropertyGroup.Version
    if ((Get-Item -LiteralPath $executable).VersionInfo.ProductVersion.Split('+')[0] -ne $expectedVersion) { throw 'Unexpected published version; cleanup aborted.' }
    $check = Start-Process -FilePath $executable -ArgumentList '--check-installation' -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $artifactRoot 'desktop-installation.log') -RedirectStandardError (Join-Path $artifactRoot 'desktop-installation-error.log')
    if ($check.ExitCode -ne 0) { throw 'Installation check failed; old releases were kept.' }
    $privateFiles = @(Get-ChildItem -LiteralPath $stagingRelease -Recurse -File | Where-Object { $_.Name -in @('accounts.json','api_key','keys.json','requests.db','cookies.json','storage-state.json','auth.json') -or $_.DirectoryName -match 'browser-sessions|local-conversations' })
    if ($privateFiles.Count) { throw 'Personal data found in package; cleanup aborted.' }
    $oldReleases = @(Get-ChildItem -LiteralPath $distributionRoot -Directory | Where-Object { $_.FullName -ne $stagingRelease -and ($_.Name -match '^Muse(?:-\d+\.\d+\.\d+)?$' -or $_.Name -match '^Muse\.stage\.[a-f0-9]{32}$' -or $_.Name -match '^Muse\.old\.[a-f0-9]{32}$') })
    foreach ($release in $oldReleases) {
        $null = Assert-ReleasePath $release.FullName
        $personal = @(Get-ChildItem -LiteralPath $release.FullName -Recurse -File | Where-Object { $_.Name -in @('accounts.json','api_key','keys.json','requests.db','ui_config.json') -or $_.DirectoryName -match 'browser-sessions|local-conversations' })
        if ($personal.Count) { throw "Old release contains personal data; migrate it before cleanup: $($release.FullName)" }
    }
    # Stamp only after every test, installation and data check passed. The
    # deferred swapper must never infer validation from Muse.exe existing.
    Write-MuseReleaseValidation $stagingRelease $distributionRoot $expectedVersion
    # Publishing while Muse is running is allowed and never force-closes
    # the app. When a Muse process is running from dist (or its path cannot
    # be read), the validated build stays staged and the MuseSwapRelease
    # scheduled task (scripts/Swap-Release.ps1) swaps it into dist\Muse
    # automatically once Muse exits; the user only closes and reopens
    # Muse, with no publish re-run needed.
    $distPrefix = $distributionRoot + [IO.Path]::DirectorySeparatorChar
    $appInUse = @(Get-Process -Name Muse -ErrorAction SilentlyContinue | Where-Object { !$_.Path -or $_.Path.StartsWith($distPrefix, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    if ($appInUse) {
        Queue-ReleaseSwap
        return
    }
    $backupPath = $null
    if (Test-Path -LiteralPath $currentRelease) {
        $backupPath = Join-Path $distributionRoot ('Muse.old.' + [Guid]::NewGuid().ToString('N'))
        $null = Assert-ReleasePath $backupPath
        try { Move-Item -LiteralPath $currentRelease -Destination $backupPath }
        catch { Queue-ReleaseSwap; return } # App may have opened after the process check.
    }
    try { Move-Item -LiteralPath $stagingRelease -Destination $currentRelease }
    catch {
        if ($backupPath -and !(Test-Path -LiteralPath $currentRelease)) { Move-Item -LiteralPath $backupPath -Destination $currentRelease }
        Queue-ReleaseSwap; return
    }
    $oldReleases = @($oldReleases | Where-Object { $_.FullName -ne $currentRelease })
    if ($backupPath) { $oldReleases += Get-Item -LiteralPath $backupPath }
    $freedBytes = 0L
    foreach ($release in $oldReleases) { $freedBytes += (Get-ChildItem -LiteralPath $release.FullName -Recurse -File | Measure-Object Length -Sum).Sum }
    # Remove backups only after the new verified release is in place.
    foreach ($release in $oldReleases) {
        $null = Assert-ReleasePath $release.FullName
        try { Remove-Item -LiteralPath $release.FullName -Recurse -Force }
        catch { Write-Warning "Giữ lại bản cũ đang bị khóa: $($release.FullName)" }
    }
    Write-Output ('RELEASE=' + (Join-Path $currentRelease 'Muse.exe'))
    # A previous deferred job has no work after immediate installation.
    Complete-MuseSwapTask $workspaceRoot
    Write-Output ('VERSION=' + $expectedVersion)
    Write-Output ('PREVIEW=' + [bool]$Preview)
    Write-Output ('OLD_RELEASES_REMOVED=' + $oldReleases.Count)
    Write-Output ('OLD_RELEASE_BYTES_REMOVED=' + $freedBytes)
}
finally {
    if ($releaseLock) { $releaseLock.Dispose() }
    $env:MUSE_BROWSER_TESTS = $originalBrowserTests; $env:MUSE2API_HEADLESS = $originalHeadless; $env:MUSE2API_CHROMIUM_PATH = $originalBrowser
    $env:MUSE_COMPUTER_TESTS = $originalComputerTests
    Pop-Location
}
