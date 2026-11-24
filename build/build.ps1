#requires -Version 5.1
<#
.SYNOPSIS
    Builds the distributable Intune Wrapper package.

.DESCRIPTION
    1. Runs the unit tests (Release).
    2. Builds src/IntuneWrapper in Release.
    3. Copies IntuneWrapper.exe to dist/, optionally signs it.
    4. Writes IntuneWrapper.exe.sha256 next to it.
    5. Zips exe + checksum + README into dist/IntuneWrapper-<version>.zip.

    The version comes from Directory.Build.props (single source of truth).

.PARAMETER CertThumbprint
    Thumbprint of a code-signing certificate in Cert:\CurrentUser\My (or LocalMachine\My).
    When given, the exe is signed with signtool before the checksum is computed.

.PARAMETER Integration
    Also run the integration test that downloads IntuneWinAppUtil.exe and packages a file (needs network).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\build.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\build.ps1 -CertThumbprint 0123ABCD...
#>
[CmdletBinding()]
param(
    [string]$CertThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$SkipTests,
    [switch]$Integration
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'

function Find-Dotnet {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $default = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $default) { return $default }
    throw 'The .NET SDK was not found. Install it with: winget install Microsoft.DotNet.SDK.8'
}

function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

$dotnet = Find-Dotnet
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
Write-Host "Intune Wrapper $version" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    if ($Integration) { $env:INTUNEWRAPPER_INTEGRATION = '1' }
    try {
        Invoke-Checked $dotnet @('test', (Join-Path $root 'IntuneWrapper.sln'), '-c', 'Release', '--nologo', '-v', 'q')
    } finally {
        Remove-Item Env:\INTUNEWRAPPER_INTEGRATION -ErrorAction SilentlyContinue
    }
}

Write-Host 'Building...' -ForegroundColor Cyan
Invoke-Checked $dotnet @('build', (Join-Path $root 'src\IntuneWrapper\IntuneWrapper.csproj'), '-c', 'Release', '--nologo', '-v', 'q')

$built = Join-Path $root 'src\IntuneWrapper\bin\Release\net48\IntuneWrapper.exe'
if (-not (Test-Path $built)) { throw "Build output not found: $built" }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null
$exe = Join-Path $dist 'IntuneWrapper.exe'
Copy-Item $built $exe

if ($CertThumbprint) {
    Write-Host 'Signing...' -ForegroundColor Cyan
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK signing tools.' }
    Invoke-Checked $signtool.FullName @('sign', '/sha1', $CertThumbprint, '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', $exe)
}

$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
# sha256sum-compatible format: "<hash>  <file>"
Set-Content -Path "$exe.sha256" -Value "$hash  IntuneWrapper.exe" -Encoding ASCII
# Documents for distribution: license, notices and docs, with the same layout as the repository so links work.
$docs = 'README.md', 'LICENSE', 'CHANGELOG.md', 'THIRD-PARTY-NOTICES.md', 'SECURITY.md', 'CONTRIBUTING.md', 'CODE_OF_CONDUCT.md', 'SPEC.md'
foreach ($d in $docs) { Copy-Item (Join-Path $root $d) $dist }
Copy-Item (Join-Path $root 'docs') (Join-Path $dist 'docs') -Recurse

$zip = Join-Path $dist "IntuneWrapper-$version.zip"
$zipItems = @($exe, "$exe.sha256") + ($docs | ForEach-Object { Join-Path $dist $_ }) + (Join-Path $dist 'docs')
Compress-Archive -Path $zipItems -DestinationPath $zip

$fileVersion = (Get-Item $exe).VersionInfo.FileVersion
Write-Host ''
Write-Host "Done." -ForegroundColor Green
Write-Host "  Exe:      $exe  (file version $fileVersion, $([math]::Round((Get-Item $exe).Length / 1KB)) KB)"
Write-Host "  SHA-256:  $hash"
Write-Host "  Zip:      $zip"
if (-not $CertThumbprint) { Write-Host '  Unsigned: see docs\INSTALLATION.md (SmartScreen) before distributing.' -ForegroundColor Yellow }
