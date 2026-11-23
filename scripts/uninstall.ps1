#Requires -Version 5.1
<#
.SYNOPSIS
    Removes a nib install made by install.ps1.

.DESCRIPTION
    Settings > Apps > nib runs this script. It removes the install folder (the
    folder this script is in), its entry in the user PATH, and the uninstall
    entry.

    It keeps your settings: %APPDATA%\nib\config.toml. Delete it by hand if you
    no longer want it.

.PARAMETER DryRun
    Print what would happen and change nothing.

.EXAMPLE
    .\uninstall.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [switch]$DryRun
)

# Started from a PowerShell 7 window, Windows PowerShell inherits 7's module
# paths and can load 7's modules. Drop those paths for this process.
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    $env:PSModulePath = (($env:PSModulePath -split ';') | Where-Object {
            $_ -and $_ -notmatch '\\PowerShell\\|\\Microsoft\.PowerShell_'
        }) -join ';'
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# KEEP THIS FILE PURE ASCII. See the note in install.ps1; ScriptEncodingTests
# enforces it.

$AppName      = 'nib'
$ExeName      = 'nib.exe'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\nib'

function Write-Step { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note { param([string]$Message) Write-Host "    $Message" -ForegroundColor DarkGray }
function Write-Warn { param([string]$Message) Write-Host "  ! $Message" -ForegroundColor Yellow }

function Invoke-Change {
    # Every write goes through here, so -DryRun cannot miss one.
    param([string]$Description, [scriptblock]$Action)
    if ($DryRun) {
        Write-Note "would $Description"
    }
    else {
        Write-Note $Description
        & $Action
    }
}

function Get-FullPath {
    param([string]$Path)
    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
}

# --- user PATH -------------------------------------------------------------
#
# Copied from install.ps1, which explains why it reads and writes the raw
# registry value instead of using [Environment]::Get/SetEnvironmentVariable:
# both of those corrupt %VAR% entries that belong to other tools. Fix both.

function Get-UserPathRaw {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $false)
    if ($null -eq $key) { return '' }
    try {
        $v = $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        return [string]$v
    } finally { $key.Dispose() }
}

# SendMessageTimeout, not SendMessage: a hung top-level window would otherwise
# block the uninstaller forever.
function Send-SettingChange {
    if (-not ('Nib.Env' -as [type])) {
        Add-Type -Namespace Nib -Name Env -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Auto)]
public static extern System.IntPtr SendMessageTimeout(
    System.IntPtr hWnd, uint Msg, System.IntPtr wParam, string lParam,
    uint fuFlags, uint uTimeout, out System.UIntPtr lpdwResult);
'@
    }
    $HWND_BROADCAST = [IntPtr] 0xffff
    $WM_SETTINGCHANGE = 0x1A
    $SMTO_ABORTIFHUNG = 0x0002
    $result = [UIntPtr]::Zero
    [void][Nib.Env]::SendMessageTimeout($HWND_BROADCAST, $WM_SETTINGCHANGE, [IntPtr]::Zero,
        'Environment', $SMTO_ABORTIFHUNG, 5000, [ref] $result)
}

function Set-UserPathRaw {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Value)

    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    if ($null -eq $key) { throw 'Could not open HKCU\Environment for writing.' }
    try {
        $kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
        try { $kind = $key.GetValueKind('Path') } catch { }
        if ($Value.Contains('%')) { $kind = [Microsoft.Win32.RegistryValueKind]::ExpandString }
        $key.SetValue('Path', $Value, $kind)
    } finally { $key.Dispose() }

    Send-SettingChange
}

# Empty entries are kept, so a trailing ';' survives the round trip. See
# install.ps1.
function Split-PathList {
    param([string] $Value)
    if ([string]::IsNullOrEmpty($Value)) { return @() }
    return @($Value -split ';')
}

function Test-SamePathEntry {
    param([string] $A, [string] $B)
    $a = $A.Trim().TrimEnd('\', '/')
    $b = $B.Trim().TrimEnd('\', '/')
    return $a.Equals($b, [StringComparison]::OrdinalIgnoreCase)
}

function Remove-FromUserPath {
    param([Parameter(Mandatory)][string] $Directory)

    $entries = Split-PathList (Get-UserPathRaw)
    $kept    = @($entries | Where-Object { -not (Test-SamePathEntry $_ $Directory) })

    if ($kept.Count -eq $entries.Count) {
        Write-Note "PATH does not contain $Directory, so there is nothing to remove."
        return
    }

    Invoke-Change "remove $Directory from your user PATH" {
        Set-UserPathRaw -Value ($kept -join ';')
    }
}

# --- checks ----------------------------------------------------------------

$installDir = Get-FullPath $PSScriptRoot
$exePath    = Join-Path $installDir $ExeName

# This script deletes the folder it is in, so make sure that folder is a nib
# install before anything else. Run from the repo's scripts/ folder, this is
# what stops it deleting scripts/.
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "$ExeName is not next to uninstall.ps1, so $installDir does not look like a nib install. Nothing was removed."
}

$modeText = ''
if ($DryRun) { $modeText = ' (dry run, nothing will change)' }
Write-Step "Uninstalling $AppName from $installDir$modeText"

# Refused rather than stopped, for the same reason as in install.ps1: a running
# editor may hold unsaved text. Checked before any change, so a refusal leaves
# the install whole and the Settings > Apps entry still there to retry from.
$running = @(Get-Process -Name $AppName -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and ((Get-FullPath $_.Path) -ieq $exePath)
    })
if ($running.Count -gt 0) {
    $msg = "nib is running from $installDir. Save your work, close it, and run the uninstall again."
    if ($DryRun) { Write-Warn "would fail: $msg" } else { throw $msg }
}

# --- PATH ------------------------------------------------------------------

Remove-FromUserPath -Directory $installDir

# --- uninstall entry -------------------------------------------------------

if (Test-Path -LiteralPath $UninstallKey) {
    # StrictMode throws on a missing property, and a hand-made key may lack it.
    $prop = (Get-ItemProperty -LiteralPath $UninstallKey).PSObject.Properties['InstallLocation']
    $location = $null
    if ($prop) { $location = $prop.Value }
    if ($location -and ((Get-FullPath $location) -ieq $installDir)) {
        Invoke-Change "remove $UninstallKey" { Remove-Item -LiteralPath $UninstallKey -Recurse -Force }
    }
    else {
        Write-Note "$UninstallKey belongs to an install at $location, so it is kept."
    }
}

# --- files -----------------------------------------------------------------

Invoke-Change "remove $installDir" {
    # Windows will not delete a folder that is the current directory.
    Set-Location -LiteralPath ([System.IO.Path]::GetTempPath())
    Remove-Item -LiteralPath $installDir -Recurse -Force
}

Write-Step 'Done'
if ($DryRun) {
    Write-Note 'Dry run: nothing was changed. Run again without -DryRun to uninstall.'
}
else {
    Write-Note "$AppName is uninstalled. Shells already open still have the old PATH."
}
$config = Join-Path $env:APPDATA 'nib\config.toml'
if (Test-Path -LiteralPath $config) {
    Write-Note "Kept: $config (your settings)"
}
