# Builds a self-contained Convert.exe for Windows.
#
#   .\build.ps1                 # win-x64, ready to zip
#   .\build.ps1 -Runtime win-arm64
#   .\build.ps1 -FrameworkDependent   # smaller, requires the .NET 8 Desktop Runtime
#
# The result is written to .\publish\<runtime>\ and contains Convert.exe plus everything
# it needs at run time. ffmpeg is not bundled; the app downloads it on first use.

[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",

    [switch]$FrameworkDependent,

    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\Convert\Convert.csproj"
$out = Join-Path $root "publish\$Runtime"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET 8 SDK wasn't found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0"
}

Write-Host "Building Convert for $Runtime…" -ForegroundColor Cyan

$publishArgs = @(
    "publish", $project,
    "-c", "Release",
    "-r", $Runtime,
    "-o", $out,
    "-p:DebugType=None",
    "-p:DebugSymbols=false"
)

if ($FrameworkDependent) {
    $publishArgs += @("--self-contained", "false")
} else {
    $publishArgs += @("--self-contained", "true", "-p:PublishReadyToRun=true")
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Copy-Item (Join-Path $root "README.md") $out -Force
Copy-Item (Join-Path $root "LICENSE") $out -Force

if (-not $SkipZip) {
    $zip = Join-Path $root "publish\Convert-$Runtime.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
    Write-Host "Created $zip" -ForegroundColor Green
}

Write-Host "Done: $out\Convert.exe" -ForegroundColor Green
