#Requires -Version 5.1
<# nib highlight fixture #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Path,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Get-ChildItem -LiteralPath $Path | ForEach-Object {
    Write-Host "found $($_.Name)" -ForegroundColor Green
}
