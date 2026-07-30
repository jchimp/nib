#Requires -Version 5.1
<#
.SYNOPSIS
    Installs nib.exe to a per-user location and puts it on PATH.

.DESCRIPTION
    Copies nib.exe from beside this script into %LOCALAPPDATA%\Programs\nib and
    appends that directory to the *user* PATH. No admin rights, no HKLM, nothing
    under Program Files - an editor this small does not deserve an elevation
    prompt.

    Safe to re-run: an existing PATH entry is left alone rather than duplicated,
    and the exe is simply overwritten.

    This script ships inside the release zip and is also usable straight from the
    repo after a publish, if you point -Source at the publish directory.

.PARAMETER Source
    Directory containing nib.exe. Defaults to this script's own directory, which
    is how it is laid out in the release zip.

.PARAMETER InstallDir
    Where to put nib.exe. Defaults to %LOCALAPPDATA%\Programs\nib.

.PARAMETER Uninstall
    Remove the install directory and strip its PATH entry.

.PARAMETER NoPath
    Copy the exe but leave PATH untouched.

.EXAMPLE
    ./install.ps1 -WhatIf

.EXAMPLE
    ./install.ps1

.EXAMPLE
    ./install.ps1 -Uninstall
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Source,
    [string] $InstallDir,
    [switch] $Uninstall,
    [switch] $NoPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# KEEP THIS FILE PURE ASCII. It has no BOM, so Windows PowerShell 5.1 decodes it as
# CP-1252: a UTF-8 em dash ends on an ASCII quote there, which silently terminates a
# string mid-line and makes one function swallow the next. No parse error, wrong
# code runs. ToolScriptEncodingTests enforces it.

# Resolved here rather than as param defaults: $PSScriptRoot is not reliably
# populated while param defaults are being evaluated under `powershell -File`,
# which is exactly how someone who double-clicks or right-click-runs this will
# invoke it.
if (-not $Source)     { $Source     = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\nib' }

# --- user PATH -------------------------------------------------------------
#
# Read PATH from the registry with DoNotExpandEnvironmentNames, NOT with
# [Environment]::GetEnvironmentVariable('Path','User').
#
# The convenience API expands entries like %USERPROFILE%\bin before handing the
# string back. Writing that expanded string back would bake in literal paths that
# were deliberately written as variables, and would rewrite the value as REG_SZ
# instead of REG_EXPAND_SZ so the surviving %VARS% stop expanding at all. Either
# way we would corrupt PATH entries belonging to other tools, which is a far worse
# outcome than failing to install an editor.
#
# Writing has the mirror-image trap. [Environment]::SetEnvironmentVariable stores a
# plain REG_SZ, so a PATH that was REG_EXPAND_SZ comes back downgraded and every
# %VAR% entry in it stops expanding - the same corruption, arriving from the other
# side. So we write through the registry with the value kind preserved, and
# broadcast WM_SETTINGCHANGE ourselves, which is the only thing SetEnvironmentVariable
# was buying us.

function Get-UserPathRaw {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $false)
    if ($null -eq $key) { return '' }
    try {
        $v = $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        return [string]$v
    } finally { $key.Dispose() }
}

# Tells already-running Explorer and any new shells that the environment moved.
# Without this the change is invisible until logoff, even though the registry is
# already correct. SendMessageTimeout, not SendMessage: a hung top-level window
# would otherwise block the installer forever.
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
        # Preserve the existing kind, and force REG_EXPAND_SZ whenever the value
        # contains a %VAR%: storing one of those as REG_SZ means Windows hands the
        # literal text to every process that resolves PATH, so the entry silently
        # stops working.
        $kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
        try { $kind = $key.GetValueKind('Path') } catch { }
        if ($Value.Contains('%')) { $kind = [Microsoft.Win32.RegistryValueKind]::ExpandString }
        $key.SetValue('Path', $Value, $kind)
    } finally { $key.Dispose() }

    Send-SettingChange
}

function Split-PathList {
    param([string] $Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return @() }
    return @($Value -split ';' | Where-Object { $_ -ne '' })
}

# Compares PATH entries the way Windows resolves them: case-insensitively, and
# ignoring a trailing separator. Not a full canonicalisation - an entry written
# via a substituted drive or an 8.3 name will not match, which only costs a
# duplicate entry, never a wrong deletion.
function Test-SamePathEntry {
    param([string] $A, [string] $B)
    $a = $A.Trim().TrimEnd('\', '/')
    $b = $B.Trim().TrimEnd('\', '/')
    return $a.Equals($b, [StringComparison]::OrdinalIgnoreCase)
}

function Add-ToUserPath {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string] $Directory)

    $raw     = Get-UserPathRaw
    $entries = Split-PathList $raw

    if ($entries | Where-Object { Test-SamePathEntry $_ $Directory }) {
        Write-Host "PATH already contains $Directory - leaving it alone."
        return
    }

    $updated = (@($entries) + $Directory) -join ';'
    if ($PSCmdlet.ShouldProcess('user PATH', "append $Directory")) {
        Set-UserPathRaw -Value $updated
        Write-Host "Added $Directory to your user PATH."
    }
}

function Remove-FromUserPath {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string] $Directory)

    $entries = Split-PathList (Get-UserPathRaw)
    $kept    = @($entries | Where-Object { -not (Test-SamePathEntry $_ $Directory) })

    if ($kept.Count -eq $entries.Count) {
        Write-Host "PATH does not contain $Directory - nothing to remove."
        return
    }

    if ($PSCmdlet.ShouldProcess('user PATH', "remove $Directory")) {
        Set-UserPathRaw -Value ($kept -join ';')
        Write-Host "Removed $Directory from your user PATH."
    }
}

# --- install / uninstall ---------------------------------------------------

if ($Uninstall) {
    if (Test-Path -LiteralPath $InstallDir) {
        if ($PSCmdlet.ShouldProcess($InstallDir, 'remove directory')) {
            Remove-Item -LiteralPath $InstallDir -Recurse -Force
            Write-Host "Removed $InstallDir."
        }
    } else {
        Write-Host "$InstallDir does not exist - nothing to remove."
    }

    Remove-FromUserPath -Directory $InstallDir
    Write-Host ''
    Write-Host 'Uninstalled. Shells already open still have the old PATH; open a new one.'
    return
}

$exe = Join-Path $Source 'nib.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "nib.exe not found at $exe. Run this from the extracted release folder, or pass -Source with the directory containing nib.exe."
}

if (-not (Test-Path -LiteralPath $InstallDir)) {
    if ($PSCmdlet.ShouldProcess($InstallDir, 'create directory')) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }
}

$target = Join-Path $InstallDir 'nib.exe'
if ($PSCmdlet.ShouldProcess($target, 'copy nib.exe')) {
    # A running nib.exe holds a lock on its own image; say so plainly rather than
    # letting the raw "being used by another process" surface.
    try {
        Copy-Item -LiteralPath $exe -Destination $target -Force
    } catch [System.IO.IOException] {
        throw "Could not write $target - close any running nib and try again. ($($_.Exception.Message))"
    }
    Write-Host "Installed nib.exe to $InstallDir."
}

if (-not $NoPath) { Add-ToUserPath -Directory $InstallDir }

Write-Host ''
Write-Host 'Done. This shell still has the old PATH - open a new terminal, or run:'
Write-Host "    `$env:Path += ';$InstallDir'"
Write-Host ''
Write-Host 'Then: nib --version'
