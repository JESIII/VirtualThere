#Requires -Version 5.1
<#
.SYNOPSIS
    Gives the VirtualThere client the privilege it needs, without a UAC prompt at every logon.

.DESCRIPTION
    vht-client must run with Administrator rights: opening the virtual host controller's device
    interface for IOCTLs is an Administrator-only operation (Vht.Client.Interop.Ude.UdeNative
    says so and fails with a clear message when it cannot). Running it as a Windows service is not
    an option in v1, because the client binary has no service-control dispatcher (it runs
    `vht-client serve` in the foreground) - registering it with the SCM would fail with error 1053.

    The supported deployment is therefore a logon task with the highest run level: it is created
    once, by the installer, which is already elevated, and it launches the client elevated at every
    logon with no prompt. `schtasks /query /tn "VirtualThere Client" /v /fo list` shows it.

    Fallback, deliberately implemented: if the highest run level cannot be set (a policy, or an
    unelevated caller), the script retries without it, logs a warning, and the client will then
    report "the client needs Administrator to load/use the virtual host controller" instead of
    silently misbehaving. That is the fail-loud contract from the card, and it is also what the
    unelevated case in this repository's own test run exercises.

.PARAMETER ClientExe
    Full path to vht-client.exe. Defaults to the copy next to this script's parent folder.

.PARAMETER TaskName
    Scheduled task name. Defaults to 'VirtualThere Client'.

.PARAMETER LogDirectory
    Where the log goes. Defaults to %ProgramData%\VirtualThere\logs.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Install-ClientTask.ps1 "C:\Program Files\VirtualThere\vht-client.exe"
#>
[CmdletBinding()]
param(
    # Defaults are resolved in the body, not here: PowerShell 5.1 does not populate $PSScriptRoot
    # while parameter defaults are evaluated, so a default that calls Split-Path on it binds an empty
    # string and the script dies before running (found by running it unelevated from a portable
    # folder, which is exactly the case this script exists for).
    [string] $ClientExe,
    [string] $TaskName = 'VirtualThere Client',
    [string] $LogDirectory
)

Set-StrictMode -Version Latest
# See the note in Install-Driver.ps1: native tools report ordinary conditions on stderr, and
# PowerShell 5.1 turns redirected native stderr into a terminating error under 'Stop'.
$ErrorActionPreference = 'Continue'

if (-not $ClientExe) { $ClientExe = Join-Path (Split-Path -Parent $PSScriptRoot) 'vht-client.exe' }
if (-not $LogDirectory) { $LogDirectory = Join-Path $env:ProgramData 'VirtualThere\logs' }

$null = New-Item -ItemType Directory -Force -Path $LogDirectory -ErrorAction SilentlyContinue
if (-not (Test-Path -LiteralPath $LogDirectory)) {
    Write-Host "VirtualThere: cannot create the log directory '$LogDirectory'"
    exit 1
}
$LogFile = Join-Path $LogDirectory 'client-task.log'

function Write-Log {
    param([string] $Message, [string] $Level = 'info')
    $line = '{0} [{1}] {2}' -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'), $Level, $Message
    Write-Host $line
    Add-Content -LiteralPath $LogFile -Value $line -Encoding ASCII
}

function Get-TaskState {
    $query = (& schtasks.exe /query /tn $TaskName /fo list /v 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { return [pscustomobject]@{ Exists = $false; Text = $query } }
    return [pscustomobject]@{ Exists = $true; Text = $query }
}

function New-ClientTask {
    param([switch] $Highest)
    $command = '"{0}" serve' -f $ClientExe
    $arguments = @('/create', '/f', '/tn', $TaskName, '/tr', $command, '/sc', 'onlogon')
    if ($Highest) { $arguments += @('/rl', 'highest') }

    Write-Log "schtasks $($arguments -join ' ')"
    $output = (& schtasks.exe @arguments 2>&1 | Out-String)
    $exit = $LASTEXITCODE
    foreach ($line in ($output -split "`r?`n")) { if ($line.Trim()) { Write-Log "schtasks: $($line.Trim())" } }
    return $exit
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdmin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Log "Install-ClientTask.ps1 starting as '$($identity.Name)'; elevated $isAdmin"

    if (-not (Test-Path -LiteralPath $ClientExe)) {
        Write-Log "vht-client.exe not found at '$ClientExe'" 'error'
        exit 1
    }

    if ($identity.Name -match '^(NT AUTHORITY\\|.*\$)$') {
        Write-Log "the current context is a machine/service account ($($identity.Name)); a logon task for an interactive user cannot be created from here." 'error'
        Write-Log 'Deploy the client with a Scheduled Task Group Policy or remove the vht-server service account assumption; see installer/PACKAGING.md section 5.' 'error'
        exit 1
    }

    $exit = New-ClientTask -Highest
    if ($exit -ne 0) {
        Write-Log 'creating the logon task with the highest run level failed; retrying unelevated' 'warn'
        $exit = New-ClientTask
        if ($exit -ne 0) {
            Write-Log 'could not create the logon task at all' 'error'
            exit 1
        }

        Write-Log 'the client logon task was created WITHOUT the highest run level: it will not be able to load the virtual host controller.' 'warn'
        Write-Log 'Fix it from an elevated prompt with: schtasks /create /f /tn "VirtualThere Client" /sc onlogon /rl highest /tr "\"<install>\vht-client.exe\" serve"' 'warn'
    }

    $state = Get-TaskState
    if (-not $state.Exists) {
        Write-Log 'the logon task was not found after creating it' 'error'
        exit 1
    }

    foreach ($line in ($state.Text -split "`r?`n")) {
        if ($line -match '^\s*(TaskName|Task To Run|Run As User|Schedule Type|Logon Mode)\s*:') { Write-Log "  $($line.Trim())" }
    }

    Write-Log 'Install-ClientTask.ps1 finished'
    exit 0
}
catch {
    Write-Log "unhandled failure: $($_.Exception.Message)" 'error'
    exit 1
}
