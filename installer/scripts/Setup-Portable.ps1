#Requires -Version 5.1
<#
.SYNOPSIS
    Makes a portable VirtualThere folder fully functional: driver, firewall, optional service.

.DESCRIPTION
    The portable build (dist/portable) is the no-installer path: the four executables run straight
    from a folder, with their configuration kept next to them because the folder contains
    'portable.marker'. Nothing in the folder needs an installer to start:

        .\vht-server.exe serve          server in the foreground
        .\vht-client.exe serve          client (needs the driver, and Administrator - see below)
        .\vht-tray.exe --mode server    management UI

    Three things are machine-wide and therefore privileged, and only those need this script:
      1. the virtual host controller driver (client side) - scripts\Install-Driver.ps1;
      2. the two Windows Firewall rules for the server's listening ports;
      3. optionally, the vht-server Windows service.

    Unelevated, this script does exactly what the card requires of every VirtualThere component:
    it fails with a clear, actionable message - including the exact commands to run from an
    Administrator prompt - instead of silently half-working. Nothing is started, no partial state
    is written, and the exit code is 3 (distinct from a real failure, so a wrapper script can tell
    "needs elevation" from "tried and failed").

    The firewall rule names and ports here are the same ones the MSI declares
    (util:FirewallException in installer\VirtualThere.wxs); Xtask's `verify` verb checks that the
    two agree, so the installed and portable layouts cannot drift.

.PARAMETER Action
    Install, Uninstall, or Status (the default). Status needs no elevation and is read-only.

.PARAMETER IncludeService
    Install/Uninstall: also register/remove the vht-server Windows service (via
    `vht-server install` / `vht-server uninstall`, the same code the MSI's service entry uses).

.PARAMETER PlaintextPort
    Server plaintext USB/IP port. Default 3240 (stock clients need no flags on this port).

.PARAMETER TlsPort
    Server TLS USB/IP port. Default 3241.

.PARAMETER WhatIf
    Print the privileged commands instead of running them. Implies no elevation requirement.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Setup-Portable.ps1 -Action Status
.EXAMPLE
    # from an Administrator prompt:
    powershell -NoProfile -ExecutionPolicy Bypass -File Setup-Portable.ps1 -Action Install -IncludeService
#>
[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall', 'Status')]
    [string] $Action = 'Status',
    [switch] $IncludeService,
    [int] $PlaintextPort = 3240,
    [int] $TlsPort = 3241,
    [switch] $WhatIf,
    [string] $LogDirectory = (Join-Path $env:ProgramData 'VirtualThere\logs')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ServiceName = 'vht-server'
$ClientTaskName = 'VirtualThere Client'

function Get-RuleName {
    param([int] $Port, [switch] $Tls)
    if ($Tls) { return "VirtualThere USB/IP TLS (TCP $Port)" }
    return "VirtualThere USB/IP (TCP $Port)"
}

# The portable layout puts this script next to the executables; the installed layout puts it in
# <install>\scripts\. Work out which one we are in.
$PortableRoot = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'vht-server.exe')) {
    $PSScriptRoot
}
else {
    (Split-Path -Parent $PSScriptRoot)
}

$null = New-Item -ItemType Directory -Force -Path $LogDirectory
$LogFile = Join-Path $LogDirectory 'portable-setup.log'
$ServerExe = Join-Path $PortableRoot 'vht-server.exe'
$ClientExe = Join-Path $PortableRoot 'vht-client.exe'
$DriverScript = Join-Path (Join-Path $PortableRoot 'scripts') 'Install-Driver.ps1'
$RemoveTaskScript = Join-Path (Join-Path $PortableRoot 'scripts') 'Remove-ClientTask.ps1'

function Write-Log {
    param([string] $Message, [string] $Level = 'info')
    $line = '{0} [{1}] {2}' -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'), $Level, $Message
    Write-Host $line
    Add-Content -LiteralPath $LogFile -Value $line -Encoding ASCII
}

function Test-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-FirewallCommands {
    param([switch] $Remove)
    $verb = if ($Remove) { 'delete' } else { 'add' }
    $rules = @(
        @{ Name = (Get-RuleName -Port $PlaintextPort); Port = $PlaintextPort },
        @{ Name = (Get-RuleName -Port $TlsPort -Tls); Port = $TlsPort }
    )

    $lines = @()
    foreach ($rule in $rules) {
        if ($Remove) {
            $lines += 'netsh advfirewall firewall delete rule name="{0}"' -f $rule.Name
        }
        else {
            $lines += 'netsh advfirewall firewall add rule name="{0}" dir=in action=allow protocol=TCP localport={1} profile=any program="{2}" enable=yes' -f
                $rule.Name, $rule.Port, $ServerExe
        }
    }
    return $lines
}

function Invoke-Netsh {
    param([string[]] $Lines)
    $failed = @()
    foreach ($line in $Lines) {
        Write-Log "netsh: $line"
        if ($WhatIf) { continue }
        $output = (& netsh.exe advfirewall firewall ($line -replace '^netsh advfirewall firewall ', '') 2>&1 | Out-String)
        foreach ($l in ($output -split "`r?`n")) { if ($l.Trim()) { Write-Log "  $l" } }
        if ($LASTEXITCODE -ne 0) { $failed += $line }
    }
    return $failed
}

function Get-FirewallState {
    foreach ($port in @($PlaintextPort, $TlsPort)) {
        $rule = if ($port -eq $TlsPort) { Get-RuleName -Port $port -Tls } else { Get-RuleName -Port $port }
        $found = (& netsh.exe advfirewall firewall show rule name="$rule" 2>&1 | Out-String)
        $state = if ($found -match 'Rule Name') { 'present' } else { 'absent' }
        Write-Log ("firewall rule '{0}': {1}" -f $rule, $state)
    }
}

function Get-DriverState {
    if (-not (Test-Path -LiteralPath $DriverScript)) {
        Write-Log "driver status unknown: '$DriverScript' not found" 'warn'
        return
    }

    $output = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $DriverScript -Status 2>&1 | Out-String)
    foreach ($line in ($output -split "`r?`n")) { if ($line.Trim()) { Write-Log "  $line" } }
    if ($LASTEXITCODE -eq 0) { Write-Log 'virtual host controller driver: present' }
    else { Write-Log 'virtual host controller driver: NOT installed (the client cannot attach anything without it)' 'warn' }
}

function Get-ServiceState {
    $query = (& sc.exe query $ServiceName 2>&1 | Out-String)
    if ($query -match 'SERVICE_NAME') {
        $stateLine = ($query -split "`r?`n" | Where-Object { $_ -match 'STATE' } | Select-Object -First 1)
        Write-Log "service '$ServiceName': $($stateLine.Trim())"
    }
    else {
        Write-Log "service '$ServiceName': not installed"
    }
}

function Get-TaskState {
    $null = (& schtasks.exe /query /tn $ClientTaskName 2>&1 | Out-String)
    Write-Log ("scheduled task '{0}': {1}" -f $ClientTaskName, $(if ($LASTEXITCODE -eq 0) { 'present' } else { 'absent' }))
}

try {
    $elevated = Test-Elevated
    Write-Log "VirtualThere portable setup: action=$Action elevated=$elevated root='$PortableRoot' whatIf=$WhatIf"

    if (-not (Test-Path -LiteralPath $ServerExe)) {
        Write-Log "vht-server.exe not found in '$PortableRoot'; point this script at a portable VirtualThere folder" 'error'
        exit 1
    }
    if (-not (Test-Path -LiteralPath $ClientExe)) {
        Write-Log "vht-client.exe not found in '$PortableRoot'" 'warn'
    }

    if ($Action -eq 'Status') {
        Get-DriverState
        Get-FirewallState
        Get-ServiceState
        Get-TaskState
        Write-Log "binaries: vht-server.exe=$(Test-Path -LiteralPath $ServerExe) vht-client.exe=$(Test-Path -LiteralPath $ClientExe) vht-tray.exe=$(Test-Path -LiteralPath (Join-Path $PortableRoot 'vht-tray.exe')) vht.exe=$(Test-Path -LiteralPath (Join-Path $PortableRoot 'vht.exe'))"
        Write-Log 'Status is read-only and needs no elevation. Use -Action Install / -Action Uninstall (elevated) to change the machine-wide parts.'
        exit 0
    }

    if (-not $elevated -and -not $WhatIf) {
        Write-Log 'this action needs Administrator: it installs a driver package, edits the Windows Firewall, and optionally registers a service.' 'error'
        Write-Log 'Nothing was changed. Run the same command from a prompt opened with "Run as administrator":' 'error'
        Write-Log ('    powershell -NoProfile -ExecutionPolicy Bypass -File "{0}" -Action {1}{2}' -f
            $PSCommandPath, $Action, $(if ($IncludeService) { ' -IncludeService' } else { '' })) 'error'
        Write-Log 'To see the exact privileged commands without running them: add -WhatIf' 'error'
        exit 3
    }

    if ($Action -eq 'Install') {
        Write-Log 'installing the virtual host controller driver'
        if (-not $WhatIf) {
            if (-not (Test-Path -LiteralPath $DriverScript)) {
                Write-Log "'$DriverScript' not found; ship the whole scripts folder with the portable build" 'error'
                exit 1
            }
            $output = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $DriverScript 2>&1 | Out-String)
            foreach ($line in ($output -split "`r?`n")) { if ($line.Trim()) { Write-Log "driver: $($line.Trim())" } }
            if ($LASTEXITCODE -ne 0) {
                Write-Log 'the driver could not be installed; the rest of the setup is not attempted' 'error'
                exit 1
            }
        }

        Write-Log 'adding the firewall rules'
        $failed = Invoke-Netsh -Lines (Get-FirewallCommands)
        if ($failed.Count -gt 0) {
            Write-Log "failed netsh commands: $($failed -join '; ')" 'error'
            exit 1
        }

        Write-Log 'creating the client logon task (the client needs Administrator to load the virtual host controller)'
        if (-not $WhatIf) {
            $taskScript = Join-Path (Join-Path $PortableRoot 'scripts') 'Install-ClientTask.ps1'
            if (Test-Path -LiteralPath $taskScript) {
                $output = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $taskScript -ClientExe $ClientExe 2>&1 | Out-String)
                foreach ($line in ($output -split "`r?`n")) { if ($line.Trim()) { Write-Log "task: $($line.Trim())" } }
                if ($LASTEXITCODE -ne 0) { Write-Log 'the client logon task could not be created; the client can still be started manually from an elevated prompt' 'warn' }
            }
            else {
                Write-Log "'$taskScript' not found; skipping the client logon task" 'warn'
            }
        }

        if ($IncludeService) {
            Write-Log "registering the service with '$ServiceName.exe install'"
            if (-not $WhatIf) {
                $output = (& $ServerExe install 2>&1 | Out-String)
                foreach ($line in ($output -split "`r?`n")) { if ($line.Trim()) { Write-Log "  $line" } }
                if ($LASTEXITCODE -ne 0) { Write-Log 'the service could not be registered' 'error'; exit 1 }

                $output = (& $ServerExe start 2>&1 | Out-String)
                foreach ($line in ($output -split "`r?`n")) { if ($line.Trim()) { Write-Log "  $line" } }
                if ($LASTEXITCODE -ne 0) { Write-Log 'the service was registered but not started' 'warn' }
            }
        }

        Write-Log 'portable setup finished. Start the server with .\vht-server.exe serve, or use the service if -IncludeService was given.'
        exit 0
    }

    # Uninstall
    Write-Log 'removing the VirtualThere pieces the portable setup added'
    $output = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $RemoveTaskScript 2>&1 | Out-String)
    foreach ($line in ($output -split "`r?`n")) { if ($line.Trim()) { Write-Log "task: $($line.Trim())" } }

    if ($IncludeService) {
        Write-Log "stopping and removing the service with '$ServiceName.exe uninstall'"
        if (-not $WhatIf) {
            $output = (& $ServerExe uninstall 2>&1 | Out-String)
            foreach ($line in ($output -split "`r?`n")) { if ($line.Trim()) { Write-Log "  $line" } }
        }
    }

    $failed = Invoke-Netsh -Lines (Get-FirewallCommands -Remove)
    if ($failed.Count -gt 0) { Write-Log "failed netsh commands: $($failed -join '; ')" 'error'; exit 1 }

    $output = (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path (Join-Path $PortableRoot 'scripts') 'Uninstall-Driver.ps1') 2>&1 | Out-String)
    foreach ($line in ($output -split "`r?`n")) { if ($line.Trim()) { Write-Log "driver: $($line.Trim())" } }
    if ($LASTEXITCODE -ne 0) { Write-Log 'the driver could not be fully removed; see the log above' 'error'; exit 1 }

    Write-Log 'portable teardown finished: no firewall rule, no service and no driver package left behind by VirtualThere'
    exit 0
}
catch {
    Write-Log "unhandled failure: $($_.Exception.Message)" 'error'
    exit 1
}
