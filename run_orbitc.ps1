Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Fg2 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
"@

$sai = Get-Process sai2 -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $sai) { Write-Output "SAI not running"; exit 1 }

[Fg2]::ShowWindow($sai.MainWindowHandle, 9) | Out-Null
[Fg2]::SetForegroundWindow($sai.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 1000

$exe = "D:\CSharp\TourXbox\Tests\TourXbox.SelfTest\bin\Debug\net8.0-windows\TourXbox.SelfTest.exe"
$log = "C:\Users\tmoam\AppData\Local\Temp\opencode\orbitc.log"
Remove-Item $log -ErrorAction SilentlyContinue

Start-Process -FilePath $exe -ArgumentList '--orbitc', '240', '1' -WindowStyle Hidden -RedirectStandardOutput $log
Start-Sleep -Seconds 8
Get-Content $log -ErrorAction SilentlyContinue
