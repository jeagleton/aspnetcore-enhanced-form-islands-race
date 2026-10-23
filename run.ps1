# Publishes the app (Release) and runs the tests against it: the server-side check (no browser) and the browser
# check, each on the three /profile pages.
#   ./run.ps1                       as shipped: NavigateTo doesn't throw (.NET 11 default). /profile fails.
#   ./run.ps1 -ThrowingNavigation   BlazorDisableThrowNavigationException=false (the workaround). Everything passes.
param([switch]$ThrowingNavigation)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$dir = if ($ThrowingNavigation) { 'artifacts/app-throw' } else { 'artifacts/app' }
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src/IslandsRace -c Release -o $dir "-p:BlazorDisableThrowNavigationException=$(-not $ThrowingNavigation)"
dotnet build test/IslandsRace.Tests -c Release
pwsh test/IslandsRace.Tests/bin/Release/net11.0/playwright.ps1 install chromium

$env:REPRO_APP_DIR = $dir
dotnet test --project test/IslandsRace.Tests -c Release --no-build
