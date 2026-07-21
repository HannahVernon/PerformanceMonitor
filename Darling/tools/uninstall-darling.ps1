<#
.SYNOPSIS
Uninstalls the PerformanceMonitor Darling service and removes the viewer shortcuts.

.DESCRIPTION
Run from an ELEVATED PowerShell. Stops and deletes the Windows service and removes the Desktop /
Start Menu 'Darling Viewer' shortcuts. DELIBERATELY LEFT IN PLACE: the extracted program folder,
darling.json, and everything under %ProgramData%\PerformanceMonitorDarling (the PostgreSQL store
with all collected history, the DPAPI credential files, and the service logs) - so an uninstall
is reversible by simply re-running install-darling.ps1. To destroy the collected data too, pass
-PurgeData and answer the confirmation.

.PARAMETER PurgeData
ALSO delete %ProgramData%\PerformanceMonitorDarling - the entire PostgreSQL store (all collected
history), the generated store credentials, and the service logs. Irreversible; prompts.
#>
[CmdletBinding()]
param(
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'
$serviceName = 'PerformanceMonitor Darling'

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'ERROR: Run this script from an ELEVATED PowerShell.' -ForegroundColor Red
    exit 1
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Write-Host 'Stopping the service (a clean stop checkpoints the bundled store)...'
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90))
    }

    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Host "sc delete failed ($LASTEXITCODE)." -ForegroundColor Red; exit 1 }
    Write-Host "Service '$serviceName' removed."
}
else {
    Write-Host "Service '$serviceName' is not installed - nothing to remove."
}

foreach ($lnkPath in @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Darling Viewer.lnk'),
    (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\Darling Viewer.lnk')
)) {
    if (Test-Path $lnkPath) {
        Remove-Item $lnkPath -Force
        Write-Host "Removed shortcut $lnkPath"
    }
}

$dataRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'PerformanceMonitorDarling'
if ($PurgeData) {
    if (Test-Path $dataRoot) {
        Write-Host ''
        Write-Host "-PurgeData will DELETE $dataRoot - the ENTIRE PostgreSQL store (all collected history)," -ForegroundColor Yellow
        Write-Host 'the generated store credentials, and the service logs. This cannot be undone.' -ForegroundColor Yellow
        $answer = Read-Host "Type the word DELETE to confirm"
        if ($answer -ceq 'DELETE') {
            Remove-Item $dataRoot -Recurse -Force
            Write-Host 'Store, credentials, and logs deleted.'
        }
        else {
            Write-Host 'Purge NOT confirmed - data left in place.'
        }
    }
}
else {
    Write-Host ''
    Write-Host "Left in place (re-running install-darling.ps1 fully restores the install):"
    Write-Host "  - this program folder and its darling.json"
    Write-Host "  - $dataRoot (the store with all collected history, credentials, logs)"
}
