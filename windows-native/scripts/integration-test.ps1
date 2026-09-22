param([string]$DotNet = "dotnet")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
if (-not $env:DOTNET_CLI_HOME) {
    $env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-cli-home"
}
if (-not $env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES = Join-Path $root ".nuget-packages"
}

& $DotNet run `
    --project (Join-Path $root "windows-native\WakePcBridge.SelfTest\WakePcBridge.SelfTest.csproj") `
    --configuration Release `
    --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Windows loopback integration tests failed."
}
