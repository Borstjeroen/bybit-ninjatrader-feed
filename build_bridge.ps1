# build_bridge.ps1 - compiles BybitBridge.cs into BybitBridge.exe (needs no SDK: uses the .NET Framework compiler that ships with Windows).
# NinjaTrader.Client.dll is NinjaTrader's own file and is NOT included in this repository; it is copied from your own NinjaTrader installation.
# Optional: -NtBin "D:\path\to\NinjaTrader 8\bin"  if NinjaTrader is not installed in the default location.
param([string]$NtBin = "")

$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $NtBin) {
    $NtBin = @("$env:ProgramFiles\NinjaTrader 8\bin", "${env:ProgramFiles(x86)}\NinjaTrader 8\bin") |
        Where-Object { $_ -and (Test-Path "$_\NinjaTrader.Client.dll") } | Select-Object -First 1
}
if (-not $NtBin -or -not (Test-Path "$NtBin\NinjaTrader.Client.dll")) {
    Write-Error "NinjaTrader.Client.dll not found. Install NinjaTrader 8, or run again with -NtBin <path to its bin folder>."
    exit 1
}

$csc = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { Write-Error ".NET Framework 4.x compiler (csc.exe) not found."; exit 1 }

Copy-Item "$NtBin\NinjaTrader.Client.dll" $root -Force
Write-Host "Copied NinjaTrader.Client.dll from $NtBin"

$out = Join-Path $root "BybitBridge.exe"
$icon = Join-Path $root "BybitBridge.ico"
$args = @("/nologo", "/target:exe", "/out:$out",
          "/r:$root\NinjaTrader.Client.dll", "/r:System.dll", "/r:System.Core.dll", "/r:System.Web.Extensions.dll")
if (Test-Path $icon) { $args += "/win32icon:$icon" }
$args += (Join-Path $root "BybitBridge.cs")

& $csc @args
if ($LASTEXITCODE -ne 0) { Write-Error "Compilation failed."; exit 1 }
Write-Host "Built $out" -ForegroundColor Green
