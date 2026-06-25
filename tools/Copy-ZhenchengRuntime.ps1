param(
    [string]$Source = "D:\真诚财务软件",
    [string]$Destination = ""
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $projectRoot "VendorRuntime\Zhencheng"
}

$mainDll = Join-Path $Source "真诚财务软件.dll"
if (-not (Test-Path -LiteralPath $mainDll)) {
    throw "未找到真诚运行时主 DLL：$mainDll"
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
    "*.cache"
)

& robocopy @robocopyArgs | Out-Host
$exitCode = $LASTEXITCODE
if ($exitCode -ge 8) {
    throw "复制真诚运行时失败，robocopy exit code: $exitCode"
}

Write-Host "真诚运行时已复制到：$Destination"
exit 0
