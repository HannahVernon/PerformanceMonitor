<#
.SYNOPSIS
  Builds pg-runtime.zip - the bundled PostgreSQL 17 + TimescaleDB runtime that the Darling
  service unpacks, initializes, and manages on first run (see DarlingManagedPostgres).

.DESCRIPTION
  Invoked by PACKAGING/CI before shipping the Darling service. It is NOT run on user machines
  and NOT run at install time: users receive the finished pg-runtime.zip beside
  PerformanceMonitor.Darling.Service.exe, and the service's first start extracts it to
  pg-runtime\pgsql\ and runs initdb/pg_ctl from there (postgres.managed = true in darling.json).
  The binaries are deliberately NOT committed to the repository.

  Steps:
    1. Download the pinned EDB PostgreSQL Windows x64 BINARIES zip (the no-installer, no-admin
       archive) and the pinned TimescaleDB Windows release zip (the GitHub release asset - the
       'gh release download timescale/timescaledb' artifact, fetched by direct URL so the script
       has no gh/auth dependency).
    2. Verify both SHA256 pins. A mismatch deletes the download and fails hard.
    3. Assemble pg-runtime\pgsql\ = EDB's pgsql\{bin,lib,share} only (pgAdmin, StackBuilder,
       doc, include and the extra license baggage are deliberately dropped) with the
       TimescaleDB files merged in per the proven recipe:
         timescaledb*.dll                          -> pgsql\lib\
         timescaledb.control + timescaledb--*.sql  -> pgsql\share\extension\
    4. Zip the assembly to <OutputDirectory>\pg-runtime.zip (archive root = pgsql\, which is
       exactly what DarlingManagedPostgres expects when it extracts beside the service binary).

  Downloads are cached in <WorkDirectory>\downloads and re-verified on every run, so re-runs
  are cheap and a corrupted cache self-heals. Extract/assemble directories are rebuilt from
  scratch every run.

  HASH PROVENANCE: the SHA256 pins below were computed 2026-07-02 from artifacts downloaded
  from the exact URLs pinned here (EDB zip 333,925,750 bytes; TimescaleDB zip 7,792,491 bytes) -
  the same artifacts that built this repo's live managed-Postgres dev fixture. Bumping a version
  means updating URL + hash TOGETHER, from a fresh download you hashed yourself.

.PARAMETER OutputDirectory
  Where pg-runtime.zip lands. Defaults to Darling\artifacts (gitignored). Packaging copies or
  points the zip beside the service's published output.

.PARAMETER WorkDirectory
  Scratch space for downloads (cached) and extraction (rebuilt). Defaults to
  <OutputDirectory>\pg-runtime-work.

.PARAMETER KeepWork
  Keep the extract/assemble directories after a successful run (the downloads cache is always
  kept) - useful for inspecting the assembled tree or pointing DARLING_TEST_PGRUNTIME at
  <WorkDirectory>\assemble\pg-runtime for the gated bootstrap E2E test.

.EXAMPLE
  pwsh -File Darling\tools\fetch-pg-runtime.ps1
#>

# pwsh 7+ only, and not for style: under Windows PowerShell 5.1 this script runs on .NET
# Framework, whose ZipFile.CreateFromDirectory writes BACKSLASH entry separators — a
# non-conformant zip that Info-ZIP/unzip mangles. CI runs this step under `shell: pwsh`;
# requiring 7 here makes a local build byte-behave like the shipped one.
#Requires -Version 7.0
[CmdletBinding()]
param(
    # Defaults resolved in the body rather than in param() so they key off $PSScriptRoot.
    [string]$OutputDirectory = "",
    [string]$WorkDirectory = "",
    [switch]$KeepWork
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\artifacts'
}

# ---- Pinned artifacts (update URL + SHA256 together; see HASH PROVENANCE above) -------------
$pgVersion = '17.10'
$pgUrl = 'https://get.enterprisedb.com/postgresql/postgresql-17.10-1-windows-x64-binaries.zip'
$pgSha256 = 'F9AAFCA58E7026A1EF2CAEEE711ACF761671E57904D430ADC85F468374F5A821'

$tsVersion = '2.28.1'
$tsUrl = 'https://github.com/timescale/timescaledb/releases/download/2.28.1/timescaledb-postgresql-17-windows-amd64.zip'
$tsSha256 = '0B3C31A4A45B2F623E58D25F73A5980093BB920960077B369284D0B9049CF26A'
# ----------------------------------------------------------------------------------------------

Add-Type -AssemblyName System.IO.Compression.FileSystem
# PS 5.1 defaults to TLS 1.0/1.1 on some machines; both hosts require TLS 1.2+.
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls12

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if ([string]::IsNullOrWhiteSpace($WorkDirectory)) {
    $WorkDirectory = Join-Path $OutputDirectory 'pg-runtime-work'
}
$WorkDirectory = [System.IO.Path]::GetFullPath($WorkDirectory)

$downloadDirectory = Join-Path $WorkDirectory 'downloads'
$extractDirectory = Join-Path $WorkDirectory 'extract'
$assembleDirectory = Join-Path $WorkDirectory 'assemble'

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $downloadDirectory | Out-Null

foreach ($rebuilt in @($extractDirectory, $assembleDirectory)) {
    if (Test-Path $rebuilt) {
        Remove-Item -Recurse -Force $rebuilt
    }
    New-Item -ItemType Directory -Force -Path $rebuilt | Out-Null
}

function Get-VerifiedDownload {
    param(
        [string]$Url,
        [string]$ExpectedSha256,
        [string]$Destination
    )

    if (Test-Path $Destination) {
        $cachedHash = (Get-FileHash -Algorithm SHA256 -Path $Destination).Hash
        if ($cachedHash -eq $ExpectedSha256) {
            Write-Host "  cached + verified: $Destination"
            return
        }
        Write-Warning "  cached file hash mismatch - deleting and re-downloading: $Destination"
        Remove-Item -Force $Destination
    }

    Write-Host "  downloading $Url"
    $temporary = "$Destination.partial"
    if (Test-Path $temporary) {
        Remove-Item -Force $temporary
    }
    Invoke-WebRequest -Uri $Url -OutFile $temporary -UseBasicParsing

    $actualHash = (Get-FileHash -Algorithm SHA256 -Path $temporary).Hash
    if ($actualHash -ne $ExpectedSha256) {
        Remove-Item -Force $temporary
        throw "SHA256 mismatch for $Url : expected $ExpectedSha256, got $actualHash. Refusing to package an unverified runtime."
    }

    Move-Item -Force $temporary $Destination
    Write-Host "  verified SHA256 $actualHash"
}

Write-Host "PostgreSQL $pgVersion binaries:"
$pgZip = Join-Path $downloadDirectory "postgresql-$pgVersion-windows-x64-binaries.zip"
Get-VerifiedDownload -Url $pgUrl -ExpectedSha256 $pgSha256 -Destination $pgZip

Write-Host "TimescaleDB $tsVersion (PG17, Windows x64):"
$tsZip = Join-Path $downloadDirectory "timescaledb-$tsVersion-postgresql-17-windows-amd64.zip"
Get-VerifiedDownload -Url $tsUrl -ExpectedSha256 $tsSha256 -Destination $tsZip

Write-Host "Extracting..."
$pgExtract = Join-Path $extractDirectory 'pg'
$tsExtract = Join-Path $extractDirectory 'ts'
[System.IO.Compression.ZipFile]::ExtractToDirectory($pgZip, $pgExtract)
[System.IO.Compression.ZipFile]::ExtractToDirectory($tsZip, $tsExtract)

$pgSource = Join-Path $pgExtract 'pgsql'
if (-not (Test-Path (Join-Path $pgSource 'bin\initdb.exe'))) {
    throw "The EDB archive did not contain pgsql\bin\initdb.exe - its layout changed; update this script."
}
$tsSource = Join-Path $tsExtract 'timescaledb'
if (-not (Test-Path (Join-Path $tsSource 'timescaledb.control'))) {
    throw "The TimescaleDB archive did not contain timescaledb\timescaledb.control - its layout changed; update this script."
}

Write-Host "Assembling pg-runtime\pgsql (bin + lib + share, TimescaleDB merged)..."
$runtimeRoot = Join-Path $assembleDirectory 'pg-runtime'
$pgsqlTarget = Join-Path $runtimeRoot 'pgsql'
New-Item -ItemType Directory -Force -Path $pgsqlTarget | Out-Null

foreach ($keep in @('bin', 'lib', 'share')) {
    Copy-Item -Recurse -Path (Join-Path $pgSource $keep) -Destination (Join-Path $pgsqlTarget $keep)
}

$extensionTarget = Join-Path $pgsqlTarget 'share\extension'
New-Item -ItemType Directory -Force -Path $extensionTarget | Out-Null
Copy-Item -Path (Join-Path $tsSource 'timescaledb*.dll') -Destination (Join-Path $pgsqlTarget 'lib')
Copy-Item -Path (Join-Path $tsSource 'timescaledb.control') -Destination $extensionTarget
Copy-Item -Path (Join-Path $tsSource 'timescaledb--*.sql') -Destination $extensionTarget

# Sanity: everything DarlingManagedPostgres and TimescaleSupport depend on must be present.
$requiredFiles = @(
    'bin\initdb.exe',
    'bin\pg_ctl.exe',
    'bin\postgres.exe',
    'bin\libpq.dll',
    'lib\timescaledb.dll',
    "lib\timescaledb-$tsVersion.dll",
    "lib\timescaledb-tsl-$tsVersion.dll",
    'share\extension\timescaledb.control',
    "share\extension\timescaledb--$tsVersion.sql"
)
foreach ($required in $requiredFiles) {
    if (-not (Test-Path (Join-Path $pgsqlTarget $required))) {
        throw "Assembled runtime is missing $required - refusing to package."
    }
}

$zipPath = Join-Path $OutputDirectory 'pg-runtime.zip'
Write-Host "Zipping to $zipPath ..."
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $runtimeRoot, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$zipInfo = Get-Item $zipPath
$zipHash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash
Write-Host ""
Write-Host "pg-runtime.zip ready:"
Write-Host "  path   : $($zipInfo.FullName)"
Write-Host ("  size   : {0:N1} MB" -f ($zipInfo.Length / 1MB))
Write-Host "  sha256 : $zipHash"
Write-Host "  ship it beside PerformanceMonitor.Darling.Service.exe (postgres.managed = true unpacks it on first run)"

if (-not $KeepWork) {
    Remove-Item -Recurse -Force $extractDirectory
    Remove-Item -Recurse -Force $assembleDirectory
}
else {
    Write-Host "  kept work tree: $runtimeRoot (DARLING_TEST_PGRUNTIME candidate for the gated bootstrap E2E)"
}
