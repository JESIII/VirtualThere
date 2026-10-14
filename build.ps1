# build.ps1 — thin wrapper around the one build command (ARCHITECTURE.md §9).
#
#   .\build.ps1                      # same as `release`
#   .\build.ps1 release              # tests, portable build, MSI, manifest, hashes
#   .\build.ps1 portable             # just dist/portable
#   .\build.ps1 installer            # just the MSI
#   .\build.ps1 verify               # decompile the MSI and grep every artifact
#   .\build.ps1 test | hash | clean | version | help
#
# Everything real lives in tools/Xtask; this file exists so a PowerShell prompt does not have to
# remember the `dotnet run --project ... --` incantation.

[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $ArgumentList = @('release')
)

$repo = $PSScriptRoot
$project = Join-Path $repo 'tools/Xtask/Xtask.csproj'

if (-not (Test-Path -LiteralPath $project)) {
    Write-Error "tools/Xtask/Xtask.csproj not found under '$repo'"
    exit 2
}

& dotnet run --project $project -- @ArgumentList
exit $LASTEXITCODE
