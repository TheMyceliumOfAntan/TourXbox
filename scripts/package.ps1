# Package a release build: publish -> verify required files -> zip
# Usage: powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -Version 1.0.0

param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "TourXbox\TourXbox\TourXbox.csproj"
$publishDir = Join-Path $root "publish"
$zipName = "TourXbox-$Version-win-x64.zip"
$zipPath = Join-Path $root $zipName

Write-Host "==> Clean"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "==> Publish $Version"
dotnet publish $project -c Release -p:Platform=x64 -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# WinUI needs these; if any is missing the app crashes on startup (0xC000027B)
$required = @(
    "TourXbox.exe",
    "TourXbox.pri",
    "App.xbf",
    "MainWindow.xbf",
    "OsdWindow.xbf",
    "Microsoft.UI.Xaml.dll",
    "Microsoft.WindowsAppRuntime.dll",
    "Assets\app.ico",
    "Presets\photoshop.json",
    "Presets\sai.json"
)

Write-Host "==> Verify"
$missing = @()
foreach ($item in $required) {
    if (-not (Test-Path (Join-Path $publishDir $item))) { $missing += $item }
}

if ($missing.Count -gt 0) {
    throw "Missing files: $($missing -join ', ')"
}
Write-Host "    all present"

Write-Host "==> Zip"
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "==> Done: $zipName ($size MB)"
