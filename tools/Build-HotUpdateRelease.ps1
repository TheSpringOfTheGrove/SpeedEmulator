param(
    [string]$Version = 'next',

    [string]$Runtime = 'win-x64',

    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'SpeedEmulator.csproj'
$artifactsRoot = Join-Path $projectRoot 'artifacts\hot-update'
$releaseDir = Join-Path $artifactsRoot 'releases'
$iconPath = Join-Path $projectRoot 'Assets\SpeedEmulatorIcon.ico'
$packTitle = (-join ([char[]](0x5C0F, 0x592A, 0x9633))) + [char]::ConvertFromUtf32(0x1F31E)

function Get-VersionKey {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $baseVersion = ($Value -split '-', 2)[0]
    try {
        [version]$baseVersion
    } catch {
        throw "Invalid version '$Value'. Use x.y.z, for example 1.0.9."
    }
}

function Get-LatestReleaseVersion {
    if (-not (Test-Path -LiteralPath $releaseDir -PathType Container)) {
        return $null
    }

    $versions = @(Get-ChildItem -LiteralPath $releaseDir -File -Filter 'SpeedEmulator-*-full.nupkg' |
        ForEach-Object {
            if ($_.Name -match '^SpeedEmulator-(?<Version>\d+\.\d+\.\d+)-full\.nupkg$') {
                [pscustomobject]@{
                    Version = $Matches.Version
                    Key     = Get-VersionKey $Matches.Version
                }
            }
        } |
        Where-Object { $_ -ne $null })

    if ($versions.Count -eq 0) {
        return $null
    }

    return ($versions | Sort-Object -Property Key -Descending | Select-Object -First 1).Version
}

function Get-NextReleaseVersion {
    param(
        [string]$LatestVersion
    )

    if ([string]::IsNullOrWhiteSpace($LatestVersion)) {
        return '1.0.0'
    }

    $key = Get-VersionKey $LatestVersion
    $major = $key.Major
    $minor = $key.Minor
    $patch = $key.Build

    if ($patch -ge 9) {
        $minor += 1
        $patch = 0
    } else {
        $patch += 1
    }

    return "$major.$minor.$patch"
}

if ([string]::IsNullOrWhiteSpace($Version) -or $Version -eq 'next') {
    $latestVersion = Get-LatestReleaseVersion
    $Version = Get-NextReleaseVersion $latestVersion
    if ([string]::IsNullOrWhiteSpace($latestVersion)) {
        Write-Host "No existing release package found. Using initial version $Version."
    } else {
        Write-Host "Auto version: $latestVersion -> $Version"
    }
} elseif ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Invalid version '$Version'. Use x.y.z, for example 1.0.9, or pass 'next'."
}

$publishDir = Join-Path $artifactsRoot "publish\$Version"

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
