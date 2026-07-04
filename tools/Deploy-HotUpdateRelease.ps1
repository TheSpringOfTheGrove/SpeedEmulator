param(
    [string]$HostName = 'financial',

    [string]$RemoteBase = '/var/www/html/speedemulator',

    [string]$LocalReleaseDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\hot-update\releases'),

    [string]$PublicBaseUrl = 'http://159.75.125.68/speedemulator',

    [ValidateRange(1, 20)]
    [int]$KeepFullReleases = 1,

    [ValidateRange(0, 20)]
    [int]$KeepDeltaReleases = 1,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(ValueFromRemainingArguments = $true)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE"
    }
}

function ConvertTo-ReleaseVersionKey {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $baseVersion = ($Version -split '-', 2)[0]
    try {
        [version]$baseVersion
    } catch {
        throw "Unable to parse release version '$Version'."
    }
}

function Get-ReleasePackageInfo {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File
    )

    if ($File.Name -notmatch '^SpeedEmulator-(?<Version>.+)-(?<Kind>full|delta)\.nupkg$') {
        return $null
    }

    $version = $Matches.Version
    [pscustomobject]@{
        File       = $File
        FileName   = $File.Name
        Version    = $version
        VersionKey = ConvertTo-ReleaseVersionKey $version
        Kind       = $Matches.Kind
    }
}

function Copy-PrunedMetadata {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ReleasePath,

        [Parameter(Mandatory = $true)]
        [string]$StagingPath,

        [Parameter(Mandatory = $true)]
        [hashtable]$SelectedPackageNames
    )

    $assetsPath = Join-Path $ReleasePath 'assets.win.json'
    $releasesPath = Join-Path $ReleasePath 'RELEASES'
    $jsonPath = Join-Path $ReleasePath 'releases.win.json'

    foreach ($path in @($assetsPath, $releasesPath, $jsonPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Required release metadata is missing: $path"
        }
    }

    Copy-Item -LiteralPath $assetsPath -Destination (Join-Path $StagingPath 'assets.win.json') -Force

    $releaseLines = @(Get-Content -LiteralPath $releasesPath | Where-Object {
        $parts = $_ -split '\s+'
        $parts.Count -ge 2 -and $SelectedPackageNames.ContainsKey($parts[1])
    })
    if ($releaseLines.Count -eq 0) {
        throw 'No RELEASES entries remain after pruning.'
    }
    Set-Content -LiteralPath (Join-Path $StagingPath 'RELEASES') -Value $releaseLines -Encoding ASCII

    $releaseJson = Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json
    $releaseJson.Assets = @($releaseJson.Assets | Where-Object {
        $SelectedPackageNames.ContainsKey($_.FileName)
    })
    if ($releaseJson.Assets.Count -eq 0) {
        throw 'No releases.win.json assets remain after pruning.'
    }
    $releaseJsonContent = $releaseJson | ConvertTo-Json -Depth 10 -Compress
    $releaseJsonTarget = Join-Path $StagingPath 'releases.win.json'
    [System.IO.File]::WriteAllText($releaseJsonTarget, $releaseJsonContent + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
}

if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) {
    throw 'ssh was not found. Please verify Windows OpenSSH is available.'
}

if (-not (Get-Command scp -ErrorAction SilentlyContinue)) {
    throw 'scp was not found. Please verify Windows OpenSSH is available.'
}

$releasePath = (Resolve-Path -LiteralPath $LocalReleaseDir).Path
$stamp = Get-Date -Format 'yyyyMMddHHmmss'
$stagingRoot = Join-Path (Split-Path -Parent $releasePath) 'upload-staging'
$stagingPath = Join-Path $stagingRoot $stamp
New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null

$packageInfos = @(Get-ChildItem -LiteralPath $releasePath -File -Filter 'SpeedEmulator-*.nupkg' |
    ForEach-Object { Get-ReleasePackageInfo $_ } |
    Where-Object { $_ -ne $null })

$fullPackages = @($packageInfos |
    Where-Object { $_.Kind -eq 'full' } |
    Sort-Object -Property VersionKey -Descending |
    Select-Object -First $KeepFullReleases)
if ($fullPackages.Count -eq 0) {
    throw "No full release package found: $releasePath"
}

$deltaPackages = @()
if ($KeepDeltaReleases -gt 0) {
    $deltaPackages = @($packageInfos |
        Where-Object { $_.Kind -eq 'delta' } |
        Sort-Object -Property VersionKey -Descending |
        Select-Object -First $KeepDeltaReleases)
}

$selectedPackages = @($fullPackages + $deltaPackages)
$selectedPackageNames = @{}
foreach ($package in $selectedPackages) {
    $selectedPackageNames[$package.FileName] = $true
    Copy-Item -LiteralPath $package.File.FullName -Destination (Join-Path $stagingPath $package.FileName) -Force
}

Copy-PrunedMetadata -ReleasePath $releasePath -StagingPath $stagingPath -SelectedPackageNames $selectedPackageNames

$releaseFiles = @(Get-ChildItem -LiteralPath $stagingPath -File)
if ($releaseFiles.Count -eq 0) {
    throw "No release files found: $stagingPath"
}

$remoteTemp = "$RemoteBase/upload-$stamp"
$sshOptions = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15')

Write-Host 'Release upload set:'
foreach ($file in ($releaseFiles | Sort-Object Name)) {
    Write-Host ("  {0} ({1:N0} bytes)" -f $file.Name, $file.Length)
}
Write-Host ""
Write-Host "Local staging: $stagingPath"
Write-Host ""

if ($DryRun) {
    Write-Host 'Dry run only. No files were uploaded.'
    return
}

Invoke-Native ssh @sshOptions $HostName "mkdir -p '$remoteTemp' '$RemoteBase/download'"

foreach ($file in $releaseFiles) {
    Invoke-Native scp @sshOptions $file.FullName "${HostName}:$remoteTemp/"
}

$finalizeCommand = "set -e; rm -rf '$RemoteBase/updates.prev'; if [ -d '$RemoteBase/updates' ]; then mv '$RemoteBase/updates' '$RemoteBase/updates.prev'; fi; mv '$remoteTemp' '$RemoteBase/updates'; mkdir -p '$RemoteBase/download'"
Invoke-Native ssh @sshOptions $HostName $finalizeCommand

$setup = Get-ChildItem -LiteralPath $releasePath -File -Filter '*Setup*.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($setup -ne $null) {
    Invoke-Native scp @sshOptions $setup.FullName "${HostName}:$RemoteBase/download/SpeedEmulator-Setup.exe"
}

$indexPath = Join-Path (Split-Path -Parent $releasePath) 'download-index.html'
$index = @"
<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8" />
  <title>SpeedEmulator Download</title>
  <style>
    body { font-family: "Microsoft YaHei", Arial, sans-serif; margin: 40px; color: #1e2a32; }
    a { color: #0a7f78; font-size: 18px; }
    code { background: #f3f6f7; padding: 2px 6px; }
  </style>
</head>
<body>
  <h1>SpeedEmulator Download</h1>
  <p>Download and run the installer on a new computer. Installed clients check updates on startup.</p>
  <p><a href="./SpeedEmulator-Setup.exe">Download SpeedEmulator Setup</a></p>
  <p>Update feed: <code>$PublicBaseUrl/updates/</code></p>
</body>
</html>
"@
Set-Content -LiteralPath $indexPath -Value $index -Encoding UTF8
Invoke-Native scp @sshOptions $indexPath "${HostName}:$RemoteBase/download/index.html"

Write-Host ""
Write-Host "Update feed: $PublicBaseUrl/updates/"
Write-Host "Installer: $PublicBaseUrl/download/SpeedEmulator-Setup.exe"
Write-Host "Download page: $PublicBaseUrl/download/"

if (Test-Path -LiteralPath $stagingPath -PathType Container) {
    Remove-Item -LiteralPath $stagingPath -Recurse -Force
    Write-Host "Cleaned local staging: $stagingPath"
}
