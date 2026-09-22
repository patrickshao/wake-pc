param(
    [string]$TargetDirectory = "",
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$version = "7.0.2"
$downloadUrl = (
    "https://github.com/jrsoftware/issrc/releases/download/is-7_0_2/" +
    "innosetup-7.0.2-x64.exe"
)
$expectedSha256 = (
    "5ad54ca3def786f8f4212552e54cc6d8d61329e2d24a1cfee0571d42c2684ff1"
)
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
if (-not $TargetDirectory) {
    $TargetDirectory = Join-Path $root ".inno-setup"
}
$TargetDirectory = [IO.Path]::GetFullPath($TargetDirectory)
$compiler = Join-Path $TargetDirectory "ISCC.exe"

if ((Test-Path -LiteralPath $compiler -PathType Leaf) -and -not $Force) {
    Write-Host "Using existing repo-local Inno Setup compiler: $compiler"
    exit 0
}

$temporaryInstaller = Join-Path (
    [IO.Path]::GetTempPath()
) ("wake-pc-inno-{0}.exe" -f [Guid]::NewGuid().ToString("N"))
$temporaryInstaller = [IO.Path]::GetFullPath($temporaryInstaller)
try {
    Write-Host "Downloading official Inno Setup $version..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $temporaryInstaller

    $actualSha256 = (
        Get-FileHash -LiteralPath $temporaryInstaller -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw (
            "Inno Setup download hash mismatch. Expected $expectedSha256; " +
            "received $actualSha256."
        )
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $temporaryInstaller
    if ($signature.Status -ne "Valid" -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notlike "CN=Pyrsys B.V.*") {
        throw "Inno Setup download does not have the expected valid Pyrsys signature."
    }

    $process = Start-Process `
        -FilePath $temporaryInstaller `
        -ArgumentList @(
            "/PORTABLE=1",
            "/VERYSILENT",
            "/SUPPRESSMSGBOXES",
            "/NORESTART",
            "/CURRENTUSER",
            "/NOICONS",
            ('/DIR="{0}"' -f $TargetDirectory)
        ) `
        -Wait `
        -PassThru `
        -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "Portable Inno Setup extraction failed with code $($process.ExitCode)."
    }
    if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "Portable Inno Setup extraction did not produce ISCC.exe."
    }

    Write-Host "Inno Setup $version compiler ready: $compiler"
}
finally {
    Remove-Item `
        -LiteralPath $temporaryInstaller `
        -Force `
        -ErrorAction SilentlyContinue
}
