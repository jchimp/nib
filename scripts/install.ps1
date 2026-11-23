#Requires -Version 5.1
<#
.SYNOPSIS
    Installs nib for the current user and puts it on PATH. No admin rights needed.

.DESCRIPTION
    Run from the unzipped release folder:

        powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1

    Copies the release to %LOCALAPPDATA%\Programs\nib, appends that folder to
    the *user* PATH, and registers an uninstall entry in Settings > Apps for the
    current user only (HKCU). No HKLM, nothing under Program Files - an editor
    this small does not deserve an elevation prompt.

    Running it again upgrades in place. An existing PATH entry is left alone
    rather than duplicated.

    It never touches %APPDATA%\nib\config.toml.

.PARAMETER InstallDir
    Where nib is installed. The folder must be empty, missing, or an earlier
    nib install: everything in it is replaced.

.PARAMETER NoPath
    Install, but leave PATH untouched.

.PARAMETER DryRun
    Print what would happen and change nothing.

.EXAMPLE
    .\install.ps1 -DryRun

.EXAMPLE
    .\install.ps1
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\nib'),
    [switch]$NoPath,
    [switch]$DryRun
)

# Started from a PowerShell 7 window, Windows PowerShell inherits 7's module
# paths and loads 7's Utility module, which is missing cmdlets 5.1 has. Drop
# those paths for this process before anything loads a module.
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    $env:PSModulePath = (($env:PSModulePath -split ';') | Where-Object {
            $_ -and $_ -notmatch '\\PowerShell\\|\\Microsoft\.PowerShell_'
        }) -join ';'
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# KEEP THIS FILE PURE ASCII. It has no BOM, so Windows PowerShell 5.1 decodes it as
# CP-1252: a UTF-8 em dash ends on an ASCII quote there, which silently terminates a
# string mid-line and makes one function swallow the next. No parse error, wrong
# code runs. ScriptEncodingTests enforces it.

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
# The same code lives in uninstall.ps1. Each script has to run on its own from
# wherever it sits, so there is no shared module to put it in. Fix both.
#
# Read PATH from the registry with DoNotExpandEnvironmentNames, NOT with
# [Environment]::GetEnvironmentVariable('Path','User').
#
# The convenience API expands entries like %USERPROFILE%\bin before handing the
# string back. Writing that expanded string back would bake in literal paths that
# were deliberately written as variables. Either way we would corrupt PATH entries
# belonging to other tools, which is a far worse outcome than failing to install
# an editor.
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

# Empty entries are kept, not filtered out. A PATH ending in ';' (common: many
# installers leave one) would otherwise lose it on install, and the
# install-then-uninstall round trip would not give back the bytes it started from.
function Split-PathList {
    param([string] $Value)
    if ([string]::IsNullOrEmpty($Value)) { return @() }
    return @($Value -split ';')
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
    param([Parameter(Mandatory)][string] $Directory)

    $entries = Split-PathList (Get-UserPathRaw)

    if ($entries | Where-Object { Test-SamePathEntry $_ $Directory }) {
        Write-Note "PATH already contains $Directory, so it is left alone."
        return
    }

    # Before a trailing ';', not after it: 'a;b;' becomes 'a;b;dir;', so
    # uninstall.ps1 removing the entry gives back 'a;b;' exactly.
    if ($entries.Count -gt 0 -and $entries[-1] -eq '') {
        $entries = @($entries[0..($entries.Count - 2)]) + $Directory + ''
    }
    else {
        $entries = @($entries) + $Directory
    }
    $updated = $entries -join ';'
    Invoke-Change "append $Directory to your user PATH" {
        Set-UserPathRaw -Value $updated
    }
}

# --- checks ----------------------------------------------------------------

# $PSScriptRoot in the body, not in a param default: it is not reliably set
# while param defaults are evaluated under `powershell -File`, which is how
# someone who right-click-runs this will invoke it.
$sourceDir  = Get-FullPath $PSScriptRoot
$InstallDir = Get-FullPath $InstallDir
$sourceExe  = Join-Path $sourceDir $ExeName
$targetExe  = Join-Path $InstallDir $ExeName

if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "$ExeName is not next to install.ps1. Run install.ps1 from the unzipped release folder."
}
if ($sourceDir -ieq $InstallDir) {
    throw "install.ps1 is running from the install folder itself ($InstallDir). Run it from the unzipped release folder."
}
# The install folder is wiped before the copy, so refuse anything that is not
# clearly ours. An empty or missing folder is fine. A 0.7.x install held only
# nib.exe, so it passes and is upgraded.
if ((Test-Path -LiteralPath $InstallDir) -and
    (@(Get-ChildItem -LiteralPath $InstallDir -Force).Count -gt 0) -and
    -not (Test-Path -LiteralPath $targetExe)) {
    throw "$InstallDir has files but no $ExeName, so it is not a nib install. Pick an empty folder with -InstallDir."
}

$version  = ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($sourceExe).ProductVersion -split '\+')[0]
$modeText = ''
if ($DryRun) { $modeText = ' (dry run, nothing will change)' }
Write-Step "Installing $AppName $version to $InstallDir$modeText"

# --- running copies --------------------------------------------------------

# A running nib.exe is locked, and the folder is wiped before the copy, so an
# install over a running copy would stop halfway with the old files gone.
# Refused rather than stopped: unlike a tray app, a running editor may hold
# unsaved text, and killing it would throw that away.
# @() because a pipeline that matches nothing is $null on 5.1.
$running = @(Get-Process -Name $AppName -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and ((Get-FullPath $_.Path) -ieq $targetExe)
    })
if ($running.Count -gt 0) {
    $msg = "nib is running from $InstallDir ($($running.Count) cop$(if ($running.Count -eq 1) { 'y' } else { 'ies' })). Save your work, close it, and run install.ps1 again."
    if ($DryRun) { Write-Warn "would fail: $msg" } else { throw $msg }
}

# --- copy ------------------------------------------------------------------

Write-Step 'Copying files'
$payload = @(Get-ChildItem -LiteralPath $sourceDir -Force | Where-Object { $_.Name -ne 'install.ps1' })
if (Test-Path -LiteralPath $InstallDir) {
    # Clear first so files dropped from a newer release do not linger.
    Invoke-Change "remove the previous files in $InstallDir" {
        Get-ChildItem -LiteralPath $InstallDir -Force | Remove-Item -Recurse -Force
    }
}
Invoke-Change "copy $($payload.Count) item(s) to $InstallDir" {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    $payload | Copy-Item -Destination $InstallDir -Recurse -Force
    # A zip downloaded from the internet marks its files as such, and SmartScreen
    # then blocks the unsigned exe on every launch. The user has chosen to
    # install it, so drop the mark on the installed copy only.
    Get-ChildItem -LiteralPath $InstallDir -Recurse -File | Unblock-File
}

# --- PATH ------------------------------------------------------------------

Write-Step 'User PATH'
if ($NoPath) {
    Write-Note 'skipped (-NoPath)'
}
else {
    Add-ToUserPath -Directory $InstallDir
}

# --- uninstall entry -------------------------------------------------------

Write-Step 'Uninstall entry (Settings > Apps)'
$uninstallScript = Join-Path $InstallDir 'uninstall.ps1'
$powershellExe   = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
Invoke-Change "write $UninstallKey" {
    $sizeKb = [int]((Get-ChildItem -LiteralPath $InstallDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1KB)
    New-Item -Path $UninstallKey -Force | Out-Null
    $values = @{
        DisplayName     = $AppName
        DisplayVersion  = $version
        Publisher       = 'Jeremy Champion'
        InstallLocation = $InstallDir
        DisplayIcon     = "$targetExe,0"
        UninstallString = "`"$powershellExe`" -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
    }
    foreach ($name in $values.Keys) {
        New-ItemProperty -Path $UninstallKey -Name $name -Value $values[$name] -PropertyType String -Force | Out-Null
    }
    foreach ($name in 'NoModify', 'NoRepair') {
        New-ItemProperty -Path $UninstallKey -Name $name -Value 1 -PropertyType DWord -Force | Out-Null
    }
    New-ItemProperty -Path $UninstallKey -Name 'EstimatedSize' -Value $sizeKb -PropertyType DWord -Force | Out-Null
}

# --- done ------------------------------------------------------------------

Write-Step 'Done'
if ($DryRun) {
    Write-Note 'Dry run: nothing was changed. Run again without -DryRun to install.'
}
else {
    Write-Note "$AppName $version is installed."
    if (-not $NoPath) {
        Write-Note 'This shell still has the old PATH - open a new terminal, or run:'
        Write-Note "    `$env:Path += ';$InstallDir'"
    }
    Write-Note 'Then: nib --version'
}
Write-Note 'To uninstall: Settings > Apps > nib, or run uninstall.ps1 in the install folder.'
