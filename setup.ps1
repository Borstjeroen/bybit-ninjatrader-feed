# setup.ps1 - run once on every PC where the Bybit feed should run in NinjaTrader 8.
# Right-click > "Run with PowerShell" (or: powershell -ExecutionPolicy Bypass -File setup.ps1)
# Optional: -NtFolder "D:\path\to\NinjaTrader 8"   (the user data folder, e.g. Documents\NinjaTrader 8) if the automatic choice is wrong.
param([string]$NtFolder = "")

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pc   = $env:COMPUTERNAME
$m    = Join-Path $root "machines\$pc"

Write-Host "== Bybit feed setup for $pc ==" -ForegroundColor Cyan
Write-Host "Project folder: $root"

# 1. one folder per PC + settings from the templates
New-Item -ItemType Directory -Force "$m\state" | Out-Null
foreach ($f in 'symbols.txt', 'addon_enabled.txt') {
    if (-not (Test-Path "$m\$f")) { Copy-Item "$root\$f" "$m\$f"; Write-Host "  copied $f to machines\$pc\" }
    else { Write-Host "  $f already exists in machines\$pc\ (left untouched)" }
}

# 2. build the bridge (copies NinjaTrader.Client.dll from your own NinjaTrader installation)
& "$root\build_bridge.ps1"
if ($LASTEXITCODE -ne 0) { Write-Warning "Building the bridge failed - see the message above."; }

# 3. tell the AddOn where this folder is (read by NinjaTrader at startup)
[Environment]::SetEnvironmentVariable("BYBIT_FEED_DIR", $root, "User")
Write-Host "Set user environment variable BYBIT_FEED_DIR = $root" -ForegroundColor Green

# 4. find the NinjaTrader user data folder (only folders that really contain bin\Custom\AddOns)
if (-not $NtFolder) {
    $cands = @(
        (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'NinjaTrader 8'),
        (Join-Path $env:USERPROFILE 'Documents\NinjaTrader 8'),
        (Join-Path $env:USERPROFILE 'OneDrive\Documents\NinjaTrader 8'),
        (Join-Path $env:USERPROFILE 'OneDrive\Documenten\NinjaTrader 8')
    ) | Select-Object -Unique | Where-Object { Test-Path (Join-Path $_ 'bin\Custom\AddOns') }
    if (-not $cands) { Write-Warning "No NinjaTrader 8 folder found. Start NinjaTrader once, or pass -NtFolder." }
    else {
        $info = $cands | ForEach-Object {
            $db = Join-Path $_ 'db\NinjaTrader.sqlite'
            [pscustomobject]@{ Path = $_; Db = $(if (Test-Path $db) { (Get-Item $db).LastWriteTime } else { [datetime]::MinValue }) }
        } | Sort-Object Db -Descending
        Write-Host "NinjaTrader folders found:"; $info | ForEach-Object { Write-Host ("  {0}  (database last modified: {1})" -f $_.Path, $_.Db) }
        if ($info.Count -eq 1) { $NtFolder = $info[0].Path }
        else {
            # With cloud sync another PC's NinjaTrader folder can show up here too; 'most recent database' is then not reliable, so you choose.
            Write-Warning "Several NinjaTrader folders found. Pick the one NinjaTrader really uses on THIS PC."
            for ($i = 0; $i -lt $info.Count; $i++) { Write-Host ("  [{0}] {1}" -f ($i + 1), $info[$i].Path) }
            $choice = Read-Host "Number (or Enter to cancel)"
            if ($choice -match '^\d+$' -and [int]$choice -ge 1 -and [int]$choice -le $info.Count) { $NtFolder = $info[[int]$choice - 1].Path }
            else { Write-Warning "No choice made; AddOn not copied. Run again with -NtFolder." }
        }
    }
}

# 5. copy the AddOn and the trading-hours template
if ($NtFolder) {
    $dest = Join-Path $NtFolder 'bin\Custom\AddOns'
    New-Item -ItemType Directory -Force $dest | Out-Null
    # trading-hours template FIRST, so it is there when NinjaTrader compiles the AddOn
    $tplDir = Join-Path $NtFolder 'templates\TradingHours'
    if (Test-Path "$root\Bybit 24 x 7 UTC.xml") {
        New-Item -ItemType Directory -Force $tplDir | Out-Null
        Copy-Item "$root\Bybit 24 x 7 UTC.xml" $tplDir -Force
        Write-Host "Trading-hours template 'Bybit 24 x 7 UTC' copied to: $tplDir" -ForegroundColor Green
    }
    Copy-Item "$root\BybitHistoryAddOn.cs" $dest -Force
    Write-Host "AddOn copied to: $dest" -ForegroundColor Green
    Write-Host "  -> NinjaTrader compiles it itself (or press F5 in the NinjaScript Editor)."
}

# 6. desktop shortcuts
& "$root\create_shortcuts.ps1"

# 7. connectivity check
try { $t = Invoke-RestMethod "https://api.bybit.com/v5/market/time" -TimeoutSec 10; Write-Host "Bybit reachable (REST OK)." -ForegroundColor Green }
catch { Write-Warning "Bybit NOT reachable from this PC: $($_.Exception.Message)" }

Write-Host ""
Write-Host "Still to do by hand in NinjaTrader (once):" -ForegroundColor Yellow
Write-Host "  1. BACK UP your NinjaTrader 8 folder first (the AddOn writes into NinjaTrader's database)."
Write-Host "  2. Restart NinjaTrader (needed to pick up the trading-hours template and BYBIT_FEED_DIR)."
Write-Host "  3. Tools > Settings > Automated trading interface: switch ATI on (port 36973), restart NinjaTrader."
Write-Host "  4. Tools > Connections: add 'External' and connect it (only that connection, no other one at the same time)."
Write-Host "  5. Start the shortcut 'Bybit Bridge (NinjaTrader)'."
Write-Host "See README.md for details."
