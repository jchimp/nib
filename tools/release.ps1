#Requires -Version 5.1
<#
.SYNOPSIS
    Builds a shippable nib release zip.

.DESCRIPTION
    Tests, publishes a self-contained single-file nib.exe, stages it with the
    install script and docs, smoke-tests the staged exe, zips it, and writes a
    SHA256 sidecar.

    Release publishes are self-contained (--self-contained true) even though the
    csproj is framework-dependent. That is deliberate: SelfContained in the csproj
    would make every `dotnet build` copy the whole runtime into bin/ and destroy
    the fast dev loop, so the release path sets it here instead. The cost is size -
    expect 70-90 MB, against 5 MB framework-dependent. The benefit is that the zip
    runs on a machine with no .NET installed, which is the whole point of shipping
    it to anyone but yourself.

    Not added, on purpose:
      PublishTrimmed              TextMateSharp resolves grammar rule types
                                  reflectively. The trimmer has no way to see that
                                  and would strip them; the failure surfaces much
                                  later as a null grammar, not as a build error.
      EnableCompressionInSingleFile
                                  Would roughly halve the zip, but it decompresses
                                  on every launch and startup is already 135 ms
                                  against a sub-100 ms goal.

.PARAMETER Version
    Release version, x.y.z. Stamped into the assembly, the zip name and VERSION.txt.

.PARAMETER OutputRoot
    Where dist artifacts land. Defaults to <repo>/dist.

.PARAMETER SkipTests
    Skip the test run. For iterating on this script, not for real releases.

.PARAMETER Force
    Allow a release from a dirty working tree.

.EXAMPLE
    ./tools/release.ps1 -Version 0.6.0 -WhatIf

.EXAMPLE
    ./tools/release.ps1 -Version 0.6.0
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $OutputRoot,
    [switch] $SkipTests,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolved here rather than as a param default: $PSScriptRoot is not reliably
# populated while param defaults are being evaluated under `powershell -File`.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here '..')).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'dist' }

$project = Join-Path $repo 'src\Nib\Nib.csproj'
$tests   = Join-Path $repo 'tests\Nib.Tests\Nib.Tests.csproj'
$rid     = 'win-x64'
$name    = "nib-$Version-$rid"

function Invoke-Step {
    param([Parameter(Mandatory)][string] $Label, [Parameter(Mandatory)][scriptblock] $Body)
    Write-Host ''
    Write-Host "==> $Label" -ForegroundColor Cyan
    & $Body
}

# dotnet writes plenty to stderr on a healthy run; the exit code is the signal.
function Invoke-Dotnet {
    param([Parameter(Mandatory)][string[]] $Arguments)
    Write-Verbose "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE." }
}

# --- 1. provenance ---------------------------------------------------------
# A binary you cannot map back to a commit is not a release, it is a mystery.

$commit = 'unknown'
Invoke-Step 'Checking the working tree' {
    $script:commit = (& git -C $repo rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) {
        if (-not $Force) { throw 'Not a git repository (or git is unavailable). Re-run with -Force to release anyway.' }
        Write-Warning 'No git metadata; VERSION.txt will record an unknown commit.'
        $script:commit = 'unknown'
        return
    }

    $dirty = & git -C $repo status --porcelain
    if ($dirty) {
        if (-not $Force) {
            Write-Host ($dirty | Out-String)
            throw "Working tree is dirty. Commit or stash first, or re-run with -Force."
        }
        Write-Warning "Releasing from a dirty tree; $commit does not describe what is in the zip."
        $script:commit = "$($script:commit)-dirty"
    }
    Write-Host "Commit: $($script:commit)"
}

# --- 2. tests --------------------------------------------------------------

if ($SkipTests) {
    Write-Warning 'Skipping tests (-SkipTests).'
} else {
    Invoke-Step 'Running tests' {
        Invoke-Dotnet @('test', $tests, '-c', 'Release', '--nologo', '-v', 'quiet')
    }
}

# --- 3. staging ------------------------------------------------------------
# The one destructive step. Everything removed here is under $OutputRoot, which
# this script owns and .gitignore excludes.

$staging = Join-Path $OutputRoot 'staging'
$payload = Join-Path $staging $name
$pubDir  = Join-Path $staging 'publish'

Invoke-Step 'Preparing the staging directory' {
    if (Test-Path -LiteralPath $staging) {
        if ($PSCmdlet.ShouldProcess($staging, 'remove previous staging directory')) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }
    if ($PSCmdlet.ShouldProcess($payload, 'create staging directory')) {
        New-Item -ItemType Directory -Path $payload -Force | Out-Null
    }
}

# --- 4. publish ------------------------------------------------------------

Invoke-Step "Publishing self-contained $rid, version $Version" {
    if ($PSCmdlet.ShouldProcess($project, 'dotnet publish')) {
        Invoke-Dotnet @(
            'publish', $project,
            '-c', 'Release',
            '-r', $rid,
            '--self-contained', 'true',
            "-p:Version=$Version",
            '-o', $pubDir,
            '--nologo'
        )
    }
}

# --- 5. stage the payload --------------------------------------------------

$stagedExe = Join-Path $payload 'nib.exe'

Invoke-Step 'Staging the payload' {
    if ($WhatIfPreference) { Write-Host 'skipped under -WhatIf'; return }

    $exe = Join-Path $pubDir 'nib.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "Publish produced no nib.exe at $exe." }
    Copy-Item -LiteralPath $exe -Destination $stagedExe -Force

    Copy-Item -LiteralPath (Join-Path $here 'install.ps1') -Destination $payload -Force

    # Docs are nice-to-have; a missing one is a warning, not a failed release.
    foreach ($doc in @('README.md', 'LICENSE')) {
        $src = Join-Path $repo $doc
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination $payload -Force
        } else {
            Write-Warning "$doc not found at the repo root; it will not be in the zip."
        }
    }

    # Build date is captured here rather than baked into the assembly so the build
    # itself stays reproducible.
    $stamp = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss') + ' UTC'
    @(
        "nib $Version"
        "commit:  $commit"
        "built:   $stamp"
        "runtime: $rid, self-contained (.NET runtime included; no prerequisites)"
    ) -join "`r`n" | Set-Content -LiteralPath (Join-Path $payload 'VERSION.txt') -Encoding UTF8

    Get-ChildItem -LiteralPath $payload | ForEach-Object {
        Write-Host ('  {0,-14} {1,12:N0} bytes' -f $_.Name, $_.Length)
    }
}

# --- 6. smoke test ---------------------------------------------------------
#
# Runs the artifact that is about to be sealed. Proves two things nothing else
# checks: that -p:Version actually reached the assembly, and that the bundled
# native Oniguruma payload self-extracts and loads on a real launch.

Invoke-Step 'Smoke-testing the staged exe' {
    if ($WhatIfPreference) { Write-Host 'skipped under -WhatIf'; return }

    $reported = (& $stagedExe --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "nib.exe --version exited $LASTEXITCODE`: $reported" }
    if ($reported -ne "nib $Version") { throw "Version mismatch: expected 'nib $Version', got '$reported'." }
    Write-Host "  --version -> $reported"

    # --soak tokenizes the repo's own files; it is the cheapest end-to-end proof
    # that the grammars unpacked and Oniguruma is alive in this build.
    & $stagedExe --soak (Join-Path $repo 'grammars') 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "nib.exe --soak exited $LASTEXITCODE; the highlighter is broken in this build." }
    Write-Host '  --soak    -> ok'
}

# --- 7. zip + hash ---------------------------------------------------------

$zip = Join-Path $OutputRoot "$name.zip"

Invoke-Step 'Packaging' {
    if ($WhatIfPreference) { Write-Host "would write $zip"; return }

    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }

    # ZipFile rather than Compress-Archive for two reasons, both of which cost a
    # release run to find. Compress-Archive opens each entry without FileShare.Read,
    # so anything holding the 84 MB exe open - Dropbox indexing dist/, an antivirus
    # scan, the smoke-tested process still tearing down - fails the archive. Worse,
    # it reports that as a *non-terminating* error, which sails straight past
    # $ErrorActionPreference = 'Stop' and leaves the script exiting 0 with no zip.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $level = [System.IO.Compression.CompressionLevel]::Optimal

    $attempt = 0
    while ($true) {
        $attempt++
        try {
            [System.IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, $level, $true)
            break
        } catch [System.IO.IOException] {
            if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
            if ($attempt -ge 5) { throw "Could not package after $attempt attempts: $($_.Exception.Message)" }
            Write-Warning "Packaging attempt $attempt hit a locked file; retrying in 3s."
            Start-Sleep -Seconds 3
        }
    }

    # Belt and braces: the step above must not be able to report success without
    # producing a file.
    if (-not (Test-Path -LiteralPath $zip)) { throw "Packaging reported success but $zip does not exist." }

    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name.zip" | Set-Content -LiteralPath "$zip.sha256" -Encoding ASCII

    $mb = (Get-Item -LiteralPath $zip).Length / 1MB
    Write-Host ''
    Write-Host "  $zip" -ForegroundColor Green
    Write-Host ('  {0:N1} MB' -f $mb)
    Write-Host "  sha256 $hash"
    Write-Host ''
    Write-Host '  Ship the zip. Extract it and run install.ps1 to put nib on PATH.'
}
