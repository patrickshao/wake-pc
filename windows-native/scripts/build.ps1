param(
    [string]$DotNet = "dotnet",
    [string]$InnoCompiler = "",
    [string]$SignToolCommand = "",
    [switch]$SkipTests,
    [switch]$SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$artifactDirectory = Join-Path $root "windows-native\artifacts"
$publishDirectory = Join-Path $artifactDirectory "publish"
$runtimeProject = Join-Path $root "windows-native\WakePcBridge\WakePcBridge.csproj"
$testProject = Join-Path $root "windows-native\WakePcBridge.SelfTest\WakePcBridge.SelfTest.csproj"
$installerScript = Join-Path $root "windows\WakePcSleepBridge.iss"
$nugetConfig = Join-Path $root "NuGet.Config"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
if (-not $env:DOTNET_CLI_HOME) {
    $env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-cli-home"
}
if (-not $env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES = Join-Path $root ".nuget-packages"
}

function Resolve-InnoCompiler {
    param([string]$RequestedPath)

    $candidates = @()
    if ($RequestedPath) {
        $candidates += $RequestedPath
    }
    if ($env:INNO_SETUP_COMPILER) {
        $candidates += $env:INNO_SETUP_COMPILER
    }
    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $candidates += $command.Source
    }
    $candidates += @(
        (Join-Path $root ".inno-setup\ISCC.exe"),
        (Join-Path ${env:ProgramFiles} "Inno Setup 7\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7\ISCC.exe")
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }

    throw (
        "Inno Setup 7 compiler not found. Install Inno Setup 7.0.2, set " +
        "INNO_SETUP_COMPILER, or pass -InnoCompiler. A repo-local portable " +
        "compiler may be placed at .inno-setup\ISCC.exe."
    )
}

function Get-NumericVersion {
    param([string]$Version)

    $parts = @($Version.Split("."))
    if ($parts.Count -gt 4 -or
        $parts.Count -lt 1 -or
        @($parts | Where-Object { $_ -notmatch '^\d+$' }).Count -ne 0) {
        throw "Companion version must contain one to four numeric components."
    }
    while ($parts.Count -lt 4) {
        $parts += "0"
    }
    return $parts -join "."
}

$InnoCompiler = Resolve-InnoCompiler -RequestedPath $InnoCompiler
$versionPath = Join-Path $root "windows-native\version.txt"
$version = (Get-Content -LiteralPath $versionPath -Raw).Trim()
$numericVersion = Get-NumericVersion -Version $version

if (-not $SkipRestore) {
    & $DotNet restore $testProject `
        --configfile $nugetConfig `
        --locked-mode
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed."
    }
}

if (-not $SkipTests) {
    & $DotNet run `
        --project $testProject `
        --configuration Release `
        --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Native self-tests failed."
    }
}

if (Test-Path -LiteralPath $artifactDirectory) {
    Remove-Item -LiteralPath $artifactDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

& $DotNet publish $runtimeProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $publishDirectory `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=false `
    -p:ContinuousIntegrationBuild=true `
    -p:Version=$version `
    -p:DebugType=embedded
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File)
if ($publishedFiles.Count -ne 1 -or
    $publishedFiles[0].Name -ne "WakePcBridge.exe") {
    $inventory = ($publishedFiles.Name | Sort-Object) -join ", "
    throw "Publish inventory must contain only WakePcBridge.exe. Found: $inventory"
}

$selfTestProcess = Start-Process `
    -FilePath $publishedFiles[0].FullName `
    -ArgumentList @("self-test") `
    -Wait `
    -PassThru `
    -WindowStyle Hidden
if ($selfTestProcess.ExitCode -ne 0) {
    throw "Published executable self-test failed."
}
$selfTestReport = Join-Path $publishDirectory "WakePcBridge.self-test.json"
$selfTest = Get-Content -LiteralPath $selfTestReport -Raw | ConvertFrom-Json
if (-not $selfTest.ok) {
    throw "Published executable reported a self-test failure."
}
Remove-Item -LiteralPath $selfTestReport -Force

$installerName = "wake-pc-sleep-bridge-v$version-setup"
$installerPath = Join-Path $artifactDirectory "$installerName.exe"
$innoArguments = @(
    "/Qp",
    "/O$artifactDirectory",
    "/F$installerName",
    "/DAppVersion=$version",
    "/DAppVersionNumeric=$numericVersion",
    "/DSourceExe=$($publishedFiles[0].FullName)"
)
if ($SignToolCommand) {
    $innoArguments += "/DUseSignTool=1"
    $innoArguments += "/Swakepc=$SignToolCommand"
}
$innoArguments += $installerScript

& $InnoCompiler @innoArguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed."
}
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Inno Setup did not produce the expected installer: $installerPath"
}

$installerBytes = [IO.File]::ReadAllBytes($installerPath)
if ($installerBytes.Length -lt 1MB -or
    $installerBytes[0] -ne [byte][char]'M' -or
    $installerBytes[1] -ne [byte][char]'Z') {
    throw "Compiled Setup.exe is missing or implausible."
}

$executableHash = Get-FileHash `
    -LiteralPath $publishedFiles[0].FullName `
    -Algorithm SHA256
$installerHash = Get-FileHash `
    -LiteralPath $installerPath `
    -Algorithm SHA256
"$($executableHash.Hash.ToLowerInvariant())  WakePcBridge.exe" |
    Set-Content `
        -LiteralPath (Join-Path $artifactDirectory "WakePcBridge.exe.sha256") `
        -Encoding ascii
"$($installerHash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($installerPath))" |
    Set-Content `
        -LiteralPath "$installerPath.sha256" `
        -Encoding ascii

Write-Host "Published: $($publishedFiles[0].FullName)"
Write-Host "Installer: $installerPath"
Write-Host "Inno Setup compiler: $InnoCompiler"
Write-Host "WakePcBridge.exe SHA-256: $($executableHash.Hash.ToLowerInvariant())"
Write-Host "Setup.exe SHA-256: $($installerHash.Hash.ToLowerInvariant())"
if (-not $SignToolCommand) {
    Write-Warning (
        "Installer is unsigned, as expected for hobby releases. " +
        "Optional: pass -SignToolCommand to sign Setup.exe and its uninstaller."
    )
}
