# addon_off.ps1 - switches the BybitHistoryAddOn OFF for this PC (renames addon_enabled.txt -> addon_enabled.txt.off).
# A cycle that is already running is finished first (a few minutes at most); after that the AddOn writes nothing more to NinjaTrader's database.
# This does NOT stop the bridge or the External connection: close the bridge and disconnect External yourself.
# Optional: -Pc <computername> to switch another PC (e.g. through a synced folder).
param([string]$Pc = $env:COMPUTERNAME)
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$m = Join-Path $root "machines\$Pc"
$on  = Join-Path $m "addon_enabled.txt"
$off = Join-Path $m "addon_enabled.txt.off"

if (Test-Path $on)       { Rename-Item $on "addon_enabled.txt.off" -Force; Write-Host "AddOn is now OFF for $Pc." -ForegroundColor Yellow }
elseif (Test-Path $off)  { Write-Host "AddOn was already OFF for $Pc." -ForegroundColor Yellow }
else { Write-Warning "No AddOn config found in $m. Run setup.ps1 first." }

Write-Host "A running cycle is finished first; after that the AddOn does nothing. See machines\$Pc\addon.log."
Start-Sleep -Seconds 5
