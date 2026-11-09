# addon_on.ps1 - switches the BybitHistoryAddOn ON for this PC (renames addon_enabled.txt.off -> addon_enabled.txt).
# The AddOn checks this file every cycle (about every 5 minutes); NinjaTrader does not need a restart or a recompile.
# Optional: -Pc <computername> to switch another PC (e.g. through a synced folder).
param([string]$Pc = $env:COMPUTERNAME)
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$m = Join-Path $root "machines\$Pc"
$on  = Join-Path $m "addon_enabled.txt"
$off = Join-Path $m "addon_enabled.txt.off"

if (Test-Path $on)       { Write-Host "AddOn was already ON for $Pc." -ForegroundColor Green }
elseif (Test-Path $off)  { Rename-Item $off "addon_enabled.txt" -Force; Write-Host "AddOn is now ON for $Pc." -ForegroundColor Green }
else { Write-Warning "No AddOn config found in $m. Run setup.ps1 first." }

Write-Host "Takes effect within about 5 minutes (the AddOn checks every cycle). See machines\$Pc\addon.log."
Start-Sleep -Seconds 5
