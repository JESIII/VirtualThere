#Requires -Version 5.1
<#
.SYNOPSIS
    Removes the VirtualThere client logon task created by Install-ClientTask.ps1.

.DESCRIPTION
    Idempotent: a machine that never had the task exits 0. If the task is still running (the
    elevated client from the previous logon), its process is stopped first so that the MSI can
    replace the binary - a running vht-client.exe would otherwise be a "file in use" during an
    upgrade or an uninstall.

.PARAMETER TaskName
    Scheduled task name. Defaults to 'VirtualThere Client'.

.PARAMETER LogDirectory
    Where the log goes. Defaults to %ProgramData%\VirtualThere\logs.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Remove-ClientTask.ps1
#>
[CmdletBinding()]
param(
    [string] $TaskName = 'VirtualThere Client',
    [string] $LogDirectory = (Join-Path $env:ProgramData 'VirtualThere\logs')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$null = New-Item -ItemType Directory -Force -Path $LogDirectory
$LogFile = Join-Path $LogDirectory 'client-task.log'

function Write-Log {
    param([string] $Message, [string] $Level = 'info')
    $line = '{0} [{1}] {2}' -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'), $Level, $Message
    Write-Host $line
    Add-Content -LiteralPath $LogFile -Value $line -Encoding ASCII
}

try {
    Write-Log 'Remove-ClientTask.ps1 starting'

    $query = (& schtasks.exe /query /tn $TaskName 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        Write-Log "no scheduled task named '$TaskName' is registered; nothing to remove"
        exit 0
    }

    # Stop the process the task started, so the binary is not in use while the MSI works.
    foreach ($process in @(Get-Process -Name 'vht-client' -ErrorAction SilentlyContinue)) {
        Write-Log "stopping vht-client.exe pid $($process.Id)"
        try { Stop-Process -Id $process.Id -Force -ErrorAction Stop }
        catch { Write-Log "could not stop pid $($process.Id): $($_.Exception.Message)" 'warn' }
    }

    $output = (& schtasks.exe /delete /f /tn $TaskName 2>&1 | Out-String)
    foreach ($line in ($output -split "`r?`n")) { if ($line.Trim()) { Write-Log "schtasks: $($line.Trim())" } }
    if ($LASTEXITCODE -ne 0) {
        Write-Log "schtasks /delete failed with exit $LASTEXITCODE" 'error'
        exit 1
    }

    $null = (& schtasks.exe /query /tn $TaskName 2>&1 | Out-String)
    if ($LASTEXITCODE -eq 0) {
        Write-Log "the scheduled task '$TaskName' is still registered" 'error'
        exit 1
    }

    Write-Log "Remove-ClientTask.ps1 finished: '$TaskName' is gone"
    exit 0
}
catch {
    Write-Log "unhandled failure: $($_.Exception.Message)" 'error'
    exit 1
}
