param(
    [string]$HostName = 'financial',

    [string]$RemoteBase = '/var/www/html/speedemulator',

    [string]$LocalReleaseDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\hot-update\releases'),

    [string]$PublicBaseUrl = 'http://159.75.125.68/speedemulator'
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

if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) {
    throw 'ssh was not found. Please verify Windows OpenSSH is available.'
}

if (-not (Get-Command scp -ErrorAction SilentlyContinue)) {
    throw 'scp was not found. Please verify Windows OpenSSH is available.'
}

$releasePath = (Resolve-Path -LiteralPath $LocalReleaseDir).Path
$releaseFiles = Get-ChildItem -LiteralPath $releasePath -File |
    Where-Object { $_.Name -notlike '*Portable.zip' -and $_.Name -notlike '*Setup.exe' }
if ($releaseFiles.Count -eq 0) {
    throw "No release files found: $releasePath"
}

$stamp = Get-Date -Format 'yyyyMMddHHmmss'
$remoteTemp = "$RemoteBase/upload-$stamp"
$sshOptions = @('-o', 'BatchMode=yes', '-o', 'ConnectTimeout=15')

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
