param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$Runtime = 'win-x64',

    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'SpeedEmulator.csproj'
$artifactsRoot = Join-Path $projectRoot 'artifacts\hot-update'
$publishDir = Join-Path $artifactsRoot "publish\$Version"
$releaseDir = Join-Path $artifactsRoot 'releases'
$iconPath = Join-Path $projectRoot 'Assets\SpeedEmulatorIcon.ico'
$packTitle = -join ([char[]](0x6781, 0x901F, 0x8D22, 0x52A1))

if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDir, $releaseDir | Out-Null

dotnet restore $projectPath
dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $publishDir `
    /p:Version=$Version `
    /p:InformationalVersion=$Version `
    /p:PublishSingleFile=false `
    /p:PublishReadyToRun=false

dotnet tool restore

dotnet vpk pack `
    --packId SpeedEmulator `
    --packVersion $Version `
    --packDir $publishDir `
    --mainExe SpeedEmulator.exe `
    --outputDir $releaseDir `
    --runtime $Runtime `
    --packAuthors SpeedEmulator `
    --packTitle $packTitle `
    --icon $iconPath `
    --channel win `
    --shortcuts "Desktop,StartMenuRoot" `
    --yes

$files = Get-ChildItem -LiteralPath $releaseDir -File | Sort-Object Name
$manifest = $files | Select-Object Name, Length, LastWriteTime, @{ Name = 'SHA256'; Expression = { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } }
$manifest | Format-Table -AutoSize

Write-Host ""
Write-Host "Release directory: $releaseDir"
