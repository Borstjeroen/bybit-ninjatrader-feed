# Creates the desktop shortcuts of the current user, with paths based on the folder this script lives in:
#   "Bybit Bridge (NinjaTrader)"   - starts the bridge
#   "Bybit AddOn ON" / "OFF"       - switches the BybitHistoryAddOn on/off for THIS PC (renames addon_enabled.txt)
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$desk = [Environment]::GetFolderPath('Desktop')
$ws = New-Object -ComObject WScript.Shell

$l = $ws.CreateShortcut("$desk\Bybit Bridge (NinjaTrader).lnk")
$l.TargetPath = "$dir\BybitBridge.exe"
$l.WorkingDirectory = $dir
$l.WindowStyle = 1
$l.Description = "Bybit data to NinjaTrader (External connection)"
if (Test-Path "$dir\BybitBridge.ico") { $l.IconLocation = "$dir\BybitBridge.ico,0" }
$l.Save()

foreach ($s in @(@{N='Bybit AddOn ON';  F='addon_on.ps1';  I='AddOn_on.ico';  D='Switch the Bybit AddOn ON for this PC'},
                 @{N='Bybit AddOn OFF'; F='addon_off.ps1'; I='AddOn_off.ico'; D='Switch the Bybit AddOn OFF for this PC'})) {
    if (-not (Test-Path "$dir\$($s.F)")) { continue }
    $k = $ws.CreateShortcut("$desk\$($s.N).lnk")
    $k.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $k.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$dir\$($s.F)`""
    $k.WorkingDirectory = $dir
    $k.WindowStyle = 1
    $k.Description = $s.D
    if (Test-Path "$dir\$($s.I)") { $k.IconLocation = "$dir\$($s.I),0" }
    $k.Save()
}
Write-Host "Shortcuts created on: $desk"
