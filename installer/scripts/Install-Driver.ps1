#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the pinned third-party UDE host-controller driver that VirtualThere's client needs.

.DESCRIPTION
    VirtualThere ships no kernel driver of its own (ARCHITECTURE.md section 3): the client's virtual
    USB host controller is the upstream usbip-win2 UDE driver, redistributed verbatim as the vendor's
    own signed package under third_party/usbip-win2-driver/. This script is the deployment step:

      1. verifies the vendor package against the pinned SHA-256 before executing anything;
      2. does nothing when the driver is already present (idempotent: this is what makes a repair
         install, and re-installing after an uninstall, safe);
      3. runs the vendor package silently and waits for it;
      4. verifies the driver is actually in the driver store, and fails loudly if it is not.

    It never re-signs, patches or repackages the vendor's binaries: the catalog signature is the
    vendor's and stays intact. See installer/PACKAGING.md section 4 for what that means for signing
    and for the test-mode caveat on machines that cannot accept the vendor's signature.

.PARAMETER DriverDirectory
    Directory holding the pinned vendor package. Defaults to the 'driver' folder next to the folder
    this script lives in, which is where the MSI and the portable layout both put it.

.PARAMETER LogDirectory
    Where the log goes. Defaults to %ProgramData%\VirtualThere\logs.

.PARAMETER Force
    Run the vendor package even when the driver is already present.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Install-Driver.ps1
#>
[CmdletBinding()]
param(
    # Defaults are resolved in the body, not here: PowerShell 5.1 does not populate $PSScriptRoot
    # while parameter defaults are being evaluated (see the note in Install-ClientTask.ps1).
    [string] $DriverDirectory,
    [string] $LogDirectory,
    [switch] $Force,
    # Report presence only, change nothing. Exit 0 = driver present, 1 = absent, 2 = could not tell.
    [switch] $Status
)

Set-StrictMode -Version Latest
# 'Continue' on purpose: these scripts drive native tools (pnputil, netsh, sc, schtasks, reg) that
# report ordinary conditions on stderr - a package that is not present, a rule that does not exist -
# and PowerShell 5.1 turns *redirected* native stderr into a terminating error under 'Stop'. Every
# failure path here is checked explicitly against an exit code or a state query instead, which is why
# this script was run unelevated against a machine with no driver and behaved correctly.
$ErrorActionPreference = 'Continue'

# Pinned upstream package. Must match third_party/usbip-win2-driver/SHA256SUMS, which is the file the
# repository records as the provenance of this binary. A mismatch is a hard failure, never a warning.
$VendorPackageName = 'USBip-0.9.8.1-x64.exe'
$PinnedSha256 = '38cad6d4432b52d5bb9409d9ad03b72fdffc4ada4cd3a48fbeca1a2752a8518a'

# The token the upstream product uses for its own driver. Both presence checks below are deliberately
# name-tolerant and based on that token rather than on an exact INF name, because the INF file names
# inside the vendor package are not published in the upstream README.
$UpstreamToken = 'usbip'
# Word boundary on purpose. Windows ships an in-box service named 'mausbip' ("MA-USB IP Filter
# Driver", mausbhost.inf) whose name *contains* 'usbip'; a bare substring match made the presence
# check report the driver as installed on a machine that never had it, and made the uninstaller treat
# that in-box service key as an orphan of ours. `\busbip` matches usbip2_ude / usbip2_filter /
# USBip-0.9.8.1 and not mausbip.
$UpstreamTokenPattern = '\busbip'

if (-not $DriverDirectory) { $DriverDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'driver' }
if (-not $LogDirectory) { $LogDirectory = Join-Path $env:ProgramData 'VirtualThere\logs' }

$null = New-Item -ItemType Directory -Force -Path $LogDirectory -ErrorAction SilentlyContinue
if (-not (Test-Path -LiteralPath $LogDirectory)) {
    Write-Host "VirtualThere: cannot create the log directory '$LogDirectory'"
    exit 1
}
$LogFile = Join-Path $LogDirectory 'driver-install.log'

function Write-Log {
    param([string] $Message, [string] $Level = 'info')
    $line = '{0} [{1}] {2}' -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'), $Level, $Message
    Write-Host $line
    Add-Content -LiteralPath $LogFile -Value $line -Encoding ASCII
}

function Test-DriverPresent {
    # 1. The driver store (this is the authoritative "is the package installed" question).
    $pnputil = (& pnputil.exe /enum-drivers 2>$null | Out-String)
    $storeHits = @()
    foreach ($line in ($pnputil -split "`r?`n")) {
        if ($line -match '^\s*(Published|Original|Provider) Name\s*:' -and $line -match $script:UpstreamTokenPattern) {
            $storeHits += $line.Trim()
        }
    }

    # 2. A service key left by the driver (covers the case where the package is present but the
    #    INF list is unparsable on this Windows build).
    $serviceHits = @()
    $serviceNames = (& reg.exe query 'HKLM\SYSTEM\CurrentControlSet\Services' 2>$null | Out-String)
    foreach ($line in ($serviceNames -split "`r?`n")) {
        if ($line -match $script:UpstreamTokenPattern) { $serviceHits += $line.Trim() }
    }

    return [pscustomobject]@{
        Present     = ($storeHits.Count -gt 0 -or $serviceHits.Count -gt 0)
        StoreHits   = $storeHits
        ServiceHits = $serviceHits
        Dump        = $pnputil
    }
}

try {
    Write-Log "Install-Driver.ps1 starting; driver directory '$DriverDirectory', elevated $([bool](New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"

    if ($Status) {
        $state = Test-DriverPresent
        if ($state.Present) {
            foreach ($hit in ($state.StoreHits + $state.ServiceHits)) { Write-Log "  installed: $hit" }
            Write-Log 'the virtual host controller driver is present'
            exit 0
        }

        Write-Log 'no driver package and no service key matching the upstream driver token was found'
        exit 1
    }

    $elevated = (New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $elevated) {
        Write-Log 'installing a driver package needs Administrator; run this from an elevated prompt:' 'error'
        Write-Log '    powershell -NoProfile -ExecutionPolicy Bypass -File Install-Driver.ps1' 'error'
        Write-Log 'Nothing was changed.' 'error'
        exit 3
    }

    $vendor = Join-Path $DriverDirectory $VendorPackageName
    if (-not (Test-Path -LiteralPath $vendor)) {
        Write-Log "vendor package not found at '$vendor'" 'error'
        exit 1
    }

    $actual = (Get-FileHash -LiteralPath $vendor -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $PinnedSha256) {
        Write-Log "SHA-256 mismatch for '$vendor': expected $PinnedSha256, found $actual. Refusing to execute it." 'error'
        exit 1
    }
    Write-Log "vendor package verified: $VendorPackageName sha256=$actual"

    $state = Test-DriverPresent
    if ($state.Present -and -not $Force) {
        Write-Log "the virtual host controller driver is already installed; nothing to do"
        foreach ($hit in ($state.StoreHits + $state.ServiceHits)) { Write-Log "  $hit" }
        exit 0
    }

    $vendorLog = Join-Path $LogDirectory 'driver-vendor-install.log'
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCANCEL', '/SP-', "/LOG=$vendorLog")
    Write-Log "running the vendor package: $VendorPackageName $($arguments -join ' ')"
    $process = Start-Process -FilePath $vendor -ArgumentList $arguments -Wait -PassThru
    $code = $process.ExitCode
    Write-Log "vendor package exit code: $code (0 = ok; 1641/3010 = ok, restart pending; see $vendorLog)"

    # Inno Setup exit codes: 0 success, 1641/3010 success with restart, 6 suspended for restart.
    $accepted = @(0, 6, 1641, 3010)
    if ($accepted -notcontains $code) {
        Write-Log "the vendor package reported failure (exit code $code)" 'error'
        exit 1
    }
    if ($code -ne 0) {
        Write-Log "the vendor package asked for a restart before the driver is usable" 'warn'
    }

    $state = Test-DriverPresent
    if (-not $state.Present) {
        Write-Log 'the vendor package reported success but no driver package could be matched in the driver store' 'error'
        Write-Log 'full pnputil /enum-drivers output follows:' 'error'
        foreach ($line in ($state.Dump -split "`r?`n")) { Write-Log "  $line" 'error' }
        exit 1
    }

    foreach ($hit in ($state.StoreHits + $state.ServiceHits)) { Write-Log "  installed: $hit" }
    Write-Log 'Install-Driver.ps1 finished: the virtual host controller driver is installed'
    exit 0
}
catch {
    Write-Log "unhandled failure: $($_.Exception.Message)" 'error'
    exit 1
}
