#Requires -Version 5.1
<#
.SYNOPSIS
    Removes the third-party UDE host-controller driver that VirtualThere's client installed.

.DESCRIPTION
    The counterpart of Install-Driver.ps1, and the reason VirtualThere's uninstaller leaves no
    orphaned driver. Three independent passes, then a verification:

      1. the vendor package's own uninstaller, found in the registry, run silently;
      2. a pnputil backstop: any driver package in the driver store whose INF or provider name
         carries the upstream token is deleted with /uninstall /force, which also removes the
         device nodes using it;
      3. orphan cleanup: a leftover service key for the upstream driver is deleted only when no
         driver package remains (i.e. only when it really is an orphan);
      4. verification: if a driver package still matches, the script exits 1 so the uninstall can
         report it, and prints the manual recovery commands.

    Idempotent: running it on a machine that never had the driver exits 0.

.PARAMETER LogDirectory
    Where the log goes. Defaults to %ProgramData%\VirtualThere\logs.

.PARAMETER TimeoutSeconds
    How long to wait for the vendor uninstaller's files to disappear.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Uninstall-Driver.ps1
#>
[CmdletBinding()]
param(
    [string] $LogDirectory = (Join-Path $env:ProgramData 'VirtualThere\logs'),
    [int] $TimeoutSeconds = 180
)

$UpstreamToken = 'usbip'
$TaskMarker = 'VirtualThere'

$null = New-Item -ItemType Directory -Force -Path $LogDirectory
$LogFile = Join-Path $LogDirectory 'driver-uninstall.log'

function Write-Log {
    param([string] $Message, [string] $Level = 'info')
    $line = '{0} [{1}] {2}' -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'), $Level, $Message
    Write-Host $line
    Add-Content -LiteralPath $LogFile -Value $line -Encoding ASCII
}

function Get-UpstreamDriverPackages {
    $pnputil = (& pnputil.exe /enum-drivers 2>$null | Out-String)
    $packages = @()
    $current = $null
    foreach ($line in ($pnputil -split "`r?`n")) {
        if ($line -match '^\s*Published Name\s*:\s*(\S+)') {
            if ($current) { $packages += $current }
            $current = [pscustomobject]@{ Published = $Matches[1]; Original = ''; Provider = '' }
        }
        elseif ($current -and $line -match '^\s*Original Name\s*:\s*(\S+)') { $current.Original = $Matches[1] }
        elseif ($current -and $line -match '^\s*Provider Name\s*:\s*(.+?)\s*$') { $current.Provider = $Matches[1] }
    }
    if ($current) { $packages += $current }

    return @($packages | Where-Object {
            ($_.Original -match $script:UpstreamToken) -or ($_.Provider -match $script:UpstreamToken)
        })
}

function Get-UpstreamServiceKeys {
    $found = @()
    $query = (& reg.exe query 'HKLM\SYSTEM\CurrentControlSet\Services' 2>$null | Out-String)
    foreach ($line in ($query -split "`r?`n")) {
        if ($line -match ('HKEY_LOCAL_MACHINE\\.*\\([^\\]+)\s*$') -and $Matches[1] -match $script:UpstreamToken) {
            $found += $Matches[1]
        }
    }
    return $found
}

function Invoke-VendorUninstaller {
    $uninstaller = $null
    $hives = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )

    foreach ($hive in $hives) {
        $entries = Get-ItemProperty -Path $hive -ErrorAction SilentlyContinue
        foreach ($entry in $entries) {
            $name = [string]$entry.DisplayName
            $where = [string]$entry.InstallLocation
            if (($name -match $script:UpstreamToken) -or ($where -match $script:UpstreamToken)) {
                $candidate = [string]$entry.UninstallString
                if ($candidate) {
                    Write-Log "found vendor uninstaller entry: '$(if ($name) { $name } else { $where })' -> $candidate"
                    $uninstaller = $candidate
                }
            }
        }
    }

    if (-not $uninstaller) {
        Write-Log 'no vendor uninstaller is registered on this machine'
        return $true
    }

    $exe = $uninstaller.Trim('"')
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Log "the registered uninstaller '$exe' does not exist; continuing with the pnputil backstop" 'warn'
        return $true
    }

    Write-Log "running the vendor uninstaller silently: $exe"
    # Inno Setup's uninstaller copies itself to %TEMP% and returns early, so wait for the *file* it
    # was launched from to disappear rather than only for the process to exit.
    $process = Start-Process -FilePath $exe -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES',
        '/NORESTART', "/LOG=$(Join-Path $LogDirectory 'driver-vendor-uninstall.log')") -Wait -PassThru
    Write-Log "vendor uninstaller exit code: $($process.ExitCode)"

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Test-Path -LiteralPath $exe) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if (Test-Path -LiteralPath $exe) {
        Write-Log "'$exe' still exists after $TimeoutSeconds s; the uninstaller may have scheduled its own removal for the next boot" 'warn'
    }
    return $true
}

try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $isAdmin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Log "Uninstall-Driver.ps1 starting as '$($identity.Name)'; elevated $isAdmin"
    if (-not $isAdmin) {
        Write-Log 'removing a driver package needs Administrator; run this from an elevated prompt:' 'error'
        Write-Log '    powershell -NoProfile -ExecutionPolicy Bypass -File Uninstall-Driver.ps1' 'error'
        exit 1
    }

    $before = Get-UpstreamDriverPackages
    if ($before.Count -eq 0 -and (Get-UpstreamServiceKeys).Count -eq 0) {
        Write-Log 'no upstream driver package and no upstream service key is present; nothing to remove'
        exit 0
    }

    foreach ($package in $before) {
        Write-Log "before: published '$($package.Published)' original '$($package.Original)' provider '$($package.Provider)'"
    }

    $null = Invoke-VendorUninstaller

    foreach ($package in (Get-UpstreamDriverPackages)) {
        Write-Log "deleting driver package $($package.Published) ($($package.Original)) from the driver store"
        $output = (& pnputil.exe /delete-driver $package.Published /uninstall /force 2>&1 | Out-String)
        Write-Log "pnputil: $($output.Trim())"
        if ($LASTEXITCODE -ne 0) {
            Write-Log "pnputil could not delete $($package.Published) (exit $LASTEXITCODE)" 'warn'
        }
    }

    $remaining = Get-UpstreamDriverPackages
    if ($remaining.Count -eq 0) {
        foreach ($serviceKey in (Get-UpstreamServiceKeys)) {
            Write-Log "the upstream service key '$serviceKey' is an orphan (no driver package references it); removing it"
            & reg.exe delete "HKLM\SYSTEM\CurrentControlSet\Services\$serviceKey" /f 2>&1 | ForEach-Object { Write-Log "reg: $_" }
        }
    }

    $remaining = Get-UpstreamDriverPackages
    if ($remaining.Count -gt 0) {
        Write-Log 'the upstream driver package is still in the driver store after both passes' 'error'
        Write-Log 'manual recovery (elevated prompt):' 'error'
        Write-Log "    pnputil /enum-drivers                     # find the line whose Original/Provider name contains '$UpstreamToken'" 'error'
        Write-Log '    pnputil /delete-driver <oemNN.inf> /uninstall /force' 'error'
        Write-Log 'then re-run this script.' 'error'
        exit 1
    }

    Write-Log 'Uninstall-Driver.ps1 finished: no upstream driver package remains'
    exit 0
}
catch {
    Write-Log "unhandled failure: $($_.Exception.Message)" 'error'
    exit 1
}
