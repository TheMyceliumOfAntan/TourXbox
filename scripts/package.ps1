# Build release artifacts: single-file exe (+ optional folder zip)
# Usage: powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -Version 1.0.0 [-SkipFolder]
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads BOM-less scripts as ANSI,
#       and non-ASCII characters corrupt the parser.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$SkipFolder
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "TourXbox\TourXbox\TourXbox.csproj"
$singleDir = Join-Path $root "publish-single"
$folderDir = Join-Path $root "publish"
$exePath = Join-Path $root "TourXbox-$Version-win-x64.exe"
$zipPath = Join-Path $root "TourXbox-$Version-win-x64.zip"

foreach ($p in @($singleDir, $folderDir)) {
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}
foreach ($p in @($exePath, $zipPath)) {
    if (Test-Path $p) { Remove-Item $p -Force }
}

# ---------- 1. single-file exe ----------
Write-Host "==> Publish single-file $Version"
dotnet publish $project -c Release -p:Platform=x64 -p:Version=$Version `
    -p:EnableMsixTooling=true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true `
    -p:PublishReadyToRun=false -o $singleDir
if ($LASTEXITCODE -ne 0) { throw "single-file publish failed" }

Copy-Item (Join-Path $singleDir "TourXbox.exe") $exePath -Force
$singleMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Host "    $(Split-Path -Leaf $exePath) ($singleMb MB)"

if (-not $SkipFolder) {
    # ---------- 2. folder zip ----------
    Write-Host "==> Publish folder $Version"
    dotnet publish $project -c Release -p:Platform=x64 -p:Version=$Version -o $folderDir
    if ($LASTEXITCODE -ne 0) { throw "folder publish failed" }

    # WinUI needs these; missing any of them crashes at startup (0xC000027B)
    $required = @(
        "TourXbox.exe", "TourXbox.pri", "App.xbf", "MainWindow.xbf", "OsdWindow.xbf",
        "Microsoft.UI.Xaml.dll", "Microsoft.WindowsAppRuntime.dll",
        "Assets\app.ico", "Presets\photoshop.json", "Presets\sai.json"
    )

    $missing = @()
    foreach ($item in $required) {
        if (-not (Test-Path (Join-Path $folderDir $item))) { $missing += $item }
    }
    if ($missing.Count -gt 0) { throw "Missing files: $($missing -join ', ')" }

    Write-Host "==> Zip"
    Compress-Archive -Path (Join-Path $folderDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
    $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "    $(Split-Path -Leaf $zipPath) ($zipMb MB)"
}

Write-Host "==> Done"
