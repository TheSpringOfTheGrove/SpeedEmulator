param(
    [string]$Source = "",
    [string]$Destination = ""
)

$ErrorActionPreference = "Stop"

function Get-VendorProductName {
    return [string]::Concat([char[]](0x771F, 0x8BDA, 0x8D22, 0x52A1, 0x8F6F, 0x4EF6))
}

function Get-VendorDllName {
    return "$(Get-VendorProductName).dll"
}

function Test-VendorRuntimeDirectory {
    param([string]$Directory)

    if ([string]::IsNullOrWhiteSpace($Directory)) {
        return $false
    }

    $mainDll = Join-Path $Directory (Get-VendorDllName)
    return Test-Path -LiteralPath $mainDll
}

function Resolve-InstalledRuntimeDirectory {
    $shortcutName = "$(Get-VendorProductName).lnk"
    $shortcut = Join-Path "C:\ProgramData\Microsoft\Windows\Start Menu\Programs" $shortcutName
    if (Test-Path -LiteralPath $shortcut) {
        try {
            $shell = New-Object -ComObject WScript.Shell
            $link = $shell.CreateShortcut($shortcut)

            if (Test-VendorRuntimeDirectory $link.WorkingDirectory) {
                return $link.WorkingDirectory
            }

            if (-not [string]::IsNullOrWhiteSpace($link.TargetPath)) {
                $targetDirectory = Split-Path -Parent $link.TargetPath
                if (Test-VendorRuntimeDirectory $targetDirectory) {
                    return $targetDirectory
                }
            }
        }
        catch {
            Write-Warning "Failed to resolve Start Menu shortcut. Falling back to default install path."
        }
    }

    $defaultInstallDirectory = Join-Path "C:\Program Files" (Get-VendorProductName)
    if (Test-VendorRuntimeDirectory $defaultInstallDirectory) {
        return $defaultInstallDirectory
    }

    return $null
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $projectRoot "VendorRuntime\Zhencheng"
}

if ([string]::IsNullOrWhiteSpace($Source)) {
    $Source = Resolve-InstalledRuntimeDirectory
}

if ([string]::IsNullOrWhiteSpace($Source)) {
    throw "Current installed vendor runtime was not found. Pass -Source explicitly."
}

$Source = (Resolve-Path -LiteralPath $Source).Path
$mainDll = Join-Path $Source (Get-VendorDllName)
if (-not (Test-Path -LiteralPath $mainDll)) {
    throw "Vendor runtime main DLL was not found: $mainDll"
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$robocopyArgs = @(
    $Source,
    $Destination,
    "/E",
    "/R:1",
    "/W:1",
    "/XD",
    "data",
    "logs",
    "temp",
    "tmp",
    "/XF",
    "*.log",
    "*.tmp",
    "*.bak",
    "*.cache",
    "Stimulsoft*.dll"
)

Write-Host "Source: $Source"
Write-Host "Destination: $Destination"
& robocopy @robocopyArgs | Out-Host
$exitCode = $LASTEXITCODE
if ($exitCode -ge 8) {
    throw "Copy failed, robocopy exit code: $exitCode"
}

Write-Host "Vendor runtime copied to: $Destination"
exit 0
