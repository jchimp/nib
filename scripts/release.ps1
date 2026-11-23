#Requires -Version 5.1
<#
.SYNOPSIS
    Builds, tags and publishes a versioned release of this .NET project.

.DESCRIPTION
    One command, nine steps, in this order: version, preflight, tests, build,
    stage, package, tag, publish, done. Every release.ps1 in every one of my
    repos runs those same nine steps - only the build seam differs.

    Nothing happens without -Apply. Run any command without it to see exactly
    what a release would do; add -Apply once the report looks right.

    The csproj <Version> is the source of truth. -Version
    writes it and rolls the CHANGELOG. Commit that, then -Publish reads it.

    Release publishes are self-contained even when the csproj is
    framework-dependent. That is deliberate: SelfContained in the csproj would
    make every `dotnet build` copy the whole runtime into bin/ and destroy the
    fast dev loop, so the release path sets it here instead. The cost is size -
    expect 70-90 MB against 5 MB framework-dependent. The benefit is that the
    zip runs on a machine with no .NET installed, which is the whole point of
    shipping it to anyone but yourself. Set $SelfContained to $false if the
    target machines are known to have the runtime.

.PARAMETER Version
    Prepare a release. Writes the version to the csproj <Version>, rolls Unreleased in
    the CHANGELOG, prints the next commands and stops. Does not build. Cannot
    be combined with -Tag or -Publish.
    Prints the current version next to the new one. A value that is not
    x.y.z or x.y.z-prerelease always stops. A version that is not higher than
    the current one or the newest v* tag, or that is already tagged, stops
    unless -Force.

.PARAMETER Tag
    After a successful build, create the annotated git tag v<version>. Requires
    a clean working tree. Does not push.

.PARAMETER Publish
    Build, tag, push the tag, create the GitHub release, and upload the zip and
    its checksum as assets. Implies -Tag. Needs gh, authenticated.

.PARAMETER Ship
    Copy the release files and their .sha256 sidecars to
    <NasRoot>\<ProjectName>\<version>\ and check each copy's hash. Set
    NasRoot in release.settings.psd1 first. Without -Tag or -Publish, a
    build that already exists for this version is copied as it is, with no
    rebuild. -Force replaces a version folder already on the NAS.

.PARAMETER Apply
    Actually do the work. Without it the script writes, tags and publishes
    nothing.

.PARAMETER DryRun
    Accepted but redundant - a dry run is already the default.

.PARAMETER SkipTests
    Skip the test run. For iterating on this script, not for real releases.

.PARAMETER Force
    Replace an artifact that already exists for this version.
    With -Version, allows a version that is lower than or equal to the
    current one, or that git has already tagged.

.PARAMETER OutDir
    Where the zip lands. Defaults to release/ at the repo root.

.PARAMETER ReleaseBranch
    Branch releases are cut from. Defaults to the remote's default branch.

.EXAMPLE
    .\scripts\release.ps1
    Rehearse a build at the current csproj version.

.EXAMPLE
    .\scripts\release.ps1 -Apply
    Build and zip locally. Nothing is tagged or pushed.

.EXAMPLE
    .\scripts\release.ps1 -Version 1.2.0 -Apply
    Prepare 1.2.0: write the version and roll the CHANGELOG. Commit and push next.

.EXAMPLE
    .\scripts\release.ps1 -Publish
    Rehearse the full release and print every step it would take.

.EXAMPLE
    .\scripts\release.ps1 -Publish -Apply
    Release the version that is already committed.

.EXAMPLE
    .\scripts\release.ps1 -Ship -Apply
    Copy this version's build to the NAS. Builds it first only if needed.

.EXAMPLE
    .\scripts\release.ps1 -Publish -Ship -Apply
    Release on GitHub, then copy the same files to the NAS.
#>

[CmdletBinding()]
param(
    # Prepare a release: write this version to the project's version file, roll
    # the CHANGELOG, then stop so the result can be committed. It never builds,
    # tags or publishes, and it cannot be combined with -Tag or -Publish.
    # Checked in the script body, not with ValidatePattern, so a bad value gets
    # a readable message instead of a parameter binding error.
    [string] $Version,

    # After a successful build, create the annotated git tag v<version>.
    [switch] $Tag,

    # Build, tag, push the tag, create the GitHub release, upload the assets.
    # Implies -Tag. Needs the gh CLI, authenticated.
    [switch] $Publish,

    # Copy the release assets to <NasRoot>\<ProjectName>\<version>\. Combines
    # with -Publish. Alone, it ships the artifacts already built for this
    # version and only builds when there are none.
    [switch] $Ship,

    # Actually do the work. Without it this is a rehearsal that writes nothing.
    [switch] $Apply,

    # Redundant: a dry run is already the default. Accepted so typing it still works.
    [switch] $DryRun,

    # Skip the test run. For iterating on this script, not for real releases.
    [switch] $SkipTests,

    # Allow overwriting an artifact that already exists for this version.
    [switch] $Force,

    # Directory the release zip is written to. Defaults to release/ at the repo
    # root - deliberately not dist/, which is where build output lives in most
    # ecosystems: an artifact directory that the build also wipes would delete
    # the previous release, and one the build fills would fold last release's
    # zip into this one.
    [string] $OutDir,

    # Branch releases are cut from. Defaults to the remote's default branch,
    # falling back to main. Only consulted with -Publish.
    [string] $ReleaseBranch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Safe by default: this script only writes, tags or publishes with -Apply.
# $DryRun adds nothing, since its behaviour is already the default. Everything
# below branches on $isDryRun, never on the raw parameters.
$isDryRun = -not $Apply

# Resolved here rather than as a param default: $PSScriptRoot is not reliably
# populated while param defaults are being evaluated under `powershell -File`.
$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $here '..')).Path
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'release' }

# ---- SETTINGS SCHEMA ------------------------------------------------------
# A repo does not edit this. Its settings live in release.settings.psd1 next
# to this script, and this list says which keys that file may hold.
#
# A key with no Default is required. A key with a Default is optional and
# falls back to it when the settings file leaves it out. Keys added after
# settings files were already deployed must be optional, or every existing
# repo breaks on its next template update.
#
# ChangelogFile defaults to off here, while a new settings file sets it to
# CHANGELOG.md: a repo that never had the setting should not start failing
# -Publish because it has no CHANGELOG.
$SettingsSchema = @(
    @{ Name = 'ProjectName';      Type = 'string' }
    @{ Name = 'VersionFile';      Type = 'string' }
    @{ Name = 'ChangelogFile';    Type = 'string';   Default = '' }
    @{ Name = 'NasRoot';          Type = 'string';   Default = '' }
    @{ Name = 'TestProject';      Type = 'string' }
    @{ Name = 'BuildDir';         Type = 'string' }
    @{ Name = 'ExtraPayload';     Type = 'string[]' }
    @{ Name = 'Rid';              Type = 'string' }
    @{ Name = 'SelfContained';    Type = 'bool' }
    @{ Name = 'PublishProps';     Type = 'string[]' }
    @{ Name = 'PackageMode';      Type = 'string' }
)
# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# Project settings
#
# Everything a repo changes lives in release.settings.psd1 next to this
# script. This script stays byte-identical to its template, so updating a repo
# is a plain file copy and audit.ps1 is a plain hash compare.
#
# Import-PowerShellDataFile accepts literal values only (strings, $true,
# $false, @() lists) and runs no code, so a settings file cannot do anything a
# settings file should not.
# ---------------------------------------------------------------------------

$SettingsFile = Join-Path $here 'release.settings.psd1'

function Import-ReleaseSettings {
    <#
        Reads the settings file and checks it against $SettingsSchema. Every
        problem is collected and reported together, the same way a rehearsal
        reports everything at once.

        A misspelled key is an error, not a silently ignored extra: with inline
        variables, '$NassRoot = ...' made an unused variable and -Ship quietly
        had no share.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object[]]$Schema
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw ("No settings file at $Path. Copy release.settings.psd1 from the template " +
               "directory into the same folder as release.ps1 and fill it in.")
    }

    try { $data = Import-PowerShellDataFile -LiteralPath $Path }
    catch { throw "$Path is not a valid settings file: $($_.Exception.Message)" }

    $known = @{}
    foreach ($s in $Schema) { $known[$s.Name] = $s }

    $problems = New-Object System.Collections.Generic.List[string]
    foreach ($key in $data.Keys) {
        if (-not $known.ContainsKey($key)) {
            $problems.Add("Unknown setting '$key'. Check the spelling. This template accepts: " +
                          (($Schema | ForEach-Object { $_.Name }) -join ', ') + '.')
        }
    }

    $values = @{}
    foreach ($s in $Schema) {
        if (-not $data.ContainsKey($s.Name)) {
            # Optional keys are the ones added after settings files already
            # existed in repos. The default keeps those files working.
            if ($s.ContainsKey('Default')) { $values[$s.Name] = $s.Default }
            else { $problems.Add("Missing setting '$($s.Name)'. It is required.") }
            continue
        }

        $value = $data[$s.Name]
        switch ($s.Type) {
            'string' {
                if ($value -isnot [string]) {
                    $problems.Add("$($s.Name) must be a string in single quotes, such as '...'.")
                }
            }
            'bool' {
                if ($value -isnot [bool]) {
                    $problems.Add("$($s.Name) must be `$true or `$false.")
                }
            }
            'string[]' {
                # A bare string where a list was expected is unambiguous, so it
                # becomes a one-item list rather than an error.
                $items = @($value)
                if (@($items | Where-Object { $_ -isnot [string] }).Count -gt 0) {
                    $problems.Add("$($s.Name) must be a list of strings, such as @('a', 'b').")
                }
                else { $value = [string[]]$items }
            }
            default { throw "Schema error: $($s.Name) has unknown type '$($s.Type)'." }
        }
        $values[$s.Name] = $value
    }

    if ($problems.Count -gt 0) {
        throw ("Problems in ${Path}:`n  - " + ($problems -join "`n  - "))
    }
    return $values
}

$settings = Import-ReleaseSettings -Path $SettingsFile -Schema $SettingsSchema
foreach ($name in $settings.Keys) {
    # The rest of the script reads plain variables ($ProjectName, $BuildDir),
    # exactly as it did when the settings were assigned inline.
    Set-Variable -Name $name -Value $settings[$name]
}

# ---------------------------------------------------------------------------
# Shared helpers. Identical in every release.ps1 template - if you fix a bug
# here, fix it in templates/_src/shared/30-helpers.part and re-assemble.
# ---------------------------------------------------------------------------

function Write-Step { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Note { param([string]$Message) Write-Host "    $Message" -ForegroundColor DarkGray }
function Write-Warn { param([string]$Message) Write-Host "  ! $Message" -ForegroundColor Yellow }

function Get-RepoPath {
    <# Resolves a repo-relative path and fails loudly if it is missing. #>
    param([Parameter(Mandatory)][string]$RelativePath)

    $full = Join-Path $RepoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $full)) {
        throw "Required path is missing: $RelativePath"
    }
    return $full
}

function Invoke-Native {
    <#
        Runs a native command and checks its exit code.

        $ErrorActionPreference = 'Stop' turns anything a native command writes
        to stderr into a terminating error, and git, gh, npm and dotnet all
        write ordinary progress there on success. So drop to Continue for the
        call itself and judge the result by exit code, which is the only
        reliable signal.
    #>
    param(
        [Parameter(Mandatory)][scriptblock]$Command,
        [Parameter(Mandatory)][string]$What,
        [switch]$AllowFailure
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # Send whatever the command prints straight to the console. Letting it
        # fall into this function's output stream would concatenate it with the
        # exit code returned below, so a caller comparing the result to 0 would
        # be comparing an array - and a successful command would look failed.
        & $Command | Out-Host
    }
    finally { $ErrorActionPreference = $previous }

    $code = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }

    if (-not $AllowFailure -and $code -ne 0) {
        throw "$What failed with exit code $code."
    }
    return $code
}

function Get-GitOutput {
    <#
        Runs git and returns its trimmed stdout, or $null if the command
        failed. Never throws.

        Needed because plenty of legitimate git queries fail by design - a ref
        that does not exist, a repo with no origin/HEAD - and under
        $ErrorActionPreference = 'Stop' their stderr becomes a terminating
        error even with 2>$null. Asking a question should not be fatal.
    #>
    param([Parameter(Mandatory)][string[]]$Arguments)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        # Merge stderr into the pipeline and drop it. "2>$null" alone still
        # surfaces a NativeCommandError in Windows PowerShell; folding stderr
        # into objects and filtering them out is what actually silences it.
        $out = & git -C $RepoRoot @Arguments 2>&1 |
               Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }

        if ($LASTEXITCODE -ne 0 -or $null -eq $out) { return $null }
        return ($out | Out-String).Trim()
    }
    finally { $ErrorActionPreference = $previous }
}

function Resolve-ReleaseBranch {
    <#
        Ask the remote what its default branch is rather than assuming 'main'.
        Many clones have no origin/HEAD, so fall back rather than fail.
    #>
    if ($script:ReleaseBranch) { return }

    $originHead = Get-GitOutput @('symbolic-ref', '--short', 'refs/remotes/origin/HEAD')
    $script:ReleaseBranch = if ($originHead) { $originHead -replace '^origin/', '' } else { 'main' }
}

function ConvertTo-RepoRelativePath {
    <# Strips the repo root off an absolute path. Anything else is returned as-is. #>
    param([Parameter(Mandatory)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path) -and
        $Path.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $Path.Substring($RepoRoot.Length).TrimStart('\', '/')
    }
    return $Path
}

function Get-GhAssetArguments {
    <#
        Turns asset paths into arguments gh will actually accept.

        gh release create reads each asset as "path#label": everything after the
        first '#' is a display label for the upload, and there is no escape for
        a '#' that is part of the filename. So an artifact under a path like
        C:\Users\me\Source\C#\myapp is silently cut down to C:\Users\me\Source\C
        and gh reports it cannot find that file - after the tag is already
        pushed, which is the worst possible moment.

        gh runs with the repo root as its working directory, so passing
        repo-relative paths keeps any '#' in the parent directories out of the
        argument altogether.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Assets)

    $ghArgs = [System.Collections.Generic.List[string]]::new()

    foreach ($asset in $Assets) {
        $arg = ConvertTo-RepoRelativePath $asset

        if ($arg.Contains('#')) {
            throw ("Cannot upload $(Split-Path -Leaf $asset): the path gh would receive " +
                   "('$arg') contains a '#', which gh reads as the start of an asset " +
                   "display label rather than as part of the filename, and it has no " +
                   "escape for that. Put the artifact directory somewhere without a '#' " +
                   "in the name - -OutDir moves it.")
        }

        $ghArgs.Add($arg)
    }

    return $ghArgs.ToArray()
}

function Format-CommandArguments {
    <# Quotes arguments containing spaces, so a printed command is paste-able. #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Arguments)

    return (($Arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' ')
}

function Assert-ArtifactDirIgnored {
    <#
        The directory artifacts are written to has to be gitignored, or the
        release dirties its own working tree: the zip lands there, git reports
        it as untracked, and the clean-tree check in the tagging step then
        refuses to tag - after the entire build has already run.

        Enforced only when a tag is actually being created, since that is the
        step it breaks. Otherwise it is a warning: building into a visible
        directory is untidy, not wrong.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Paths,
        [Parameter(Mandatory)][bool]$Soft
    )

    # Not a repo, so there is no .gitignore for anything to be missing from.
    if (-not (Get-GitOutput @('rev-parse', '--git-dir'))) { return }

    foreach ($path in $Paths) {
        if (-not $path) { continue }

        # check-ignore wants a repo-relative path.
        $rel = ConvertTo-RepoRelativePath $path
        if (-not $rel) { continue }

        # Exit 0 and echoes the path when ignored; exit 1 and silent when not,
        # which Get-GitOutput reports as $null. The trailing slash matters: on
        # a first release the directory does not exist yet, git then treats a
        # bare 'dist' as a file, and the usual 'dist/' pattern does not match.
        if (Get-GitOutput @('check-ignore', "$rel/")) { continue }

        $msg = ("$rel is not gitignored. The release writes artifacts there, which makes " +
                "the working tree dirty, and -Tag then refuses to tag it - after the whole " +
                "build has run. Add it:`n" +
                "    Add-Content .gitignore '$rel/'")
        if ($Soft) { Write-Warn "would fail: $msg" } else { throw $msg }
    }
}

function Assert-ExtraPayload {
    <#
        Every entry in $ExtraPayload has to exist, checked before anything is
        built. A typo here would otherwise produce a quietly thinner zip that
        still looks like a successful release.
    #>
    foreach ($entry in $ExtraPayload) {
        $rel = $entry.TrimEnd('/', '\')
        if (-not $rel) { throw "Empty entry in the ExtraPayload list." }
        Get-RepoPath $rel | Out-Null
    }
    if (@($ExtraPayload).Count -gt 0) {
        Write-Note "$(@($ExtraPayload).Count) extra payload entries resolved"
    }
}

function Assert-PublishReady {
    <#
        Checked before anything is built, so a missing prerequisite fails in
        two seconds rather than after a tag has already been created.

        On a dry run these are reported as warnings instead of throwing: the
        point of a rehearsal is to see the whole plan, including the parts you
        are not set up for yet. Problems accumulate rather than stopping at the
        first, so one rehearsal surfaces everything.
    #>
    param(
        [Parameter(Mandatory)][bool]$Soft,
        [Parameter(Mandatory)][string]$ArtifactDir
    )

    Resolve-ReleaseBranch

    $problems = [System.Collections.Generic.List[string]]::new()

    # Checked here, before the build, because the alternative is finding out
    # after the tag has been pushed - see Get-GhAssetArguments.
    $relArtifact = ConvertTo-RepoRelativePath $ArtifactDir
    if ($relArtifact.Contains('#')) {
        $problems.Add("Artifacts land in '$relArtifact', and gh reads a '#' in an asset path as " +
                      "the start of a display label, with no way to escape it. Move the artifact " +
                      "directory, or pass -OutDir to put it somewhere without a '#'.")
    }

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        $problems.Add("-Publish needs the GitHub CLI. Install it, then run: gh auth login")
    }
    else {
        $authed = Invoke-Native -What 'gh auth status' -AllowFailure -Command { gh auth status *> $null }
        if ($authed -ne 0) {
            $problems.Add("-Publish needs an authenticated gh. Run: gh auth login")
        }
    }

    # Refresh the remote-tracking refs first, or 'behind' is measured against
    # whatever this clone last happened to see.
    $fetched = Invoke-Native -What 'git fetch' -AllowFailure -Command {
        git -C $RepoRoot fetch --quiet origin $ReleaseBranch 2>$null
    }
    if ($fetched -ne 0) {
        Write-Warn "could not fetch origin - branch checks use possibly stale refs"
    }

    # The release is cut from whatever commit gets tagged, so pin down exactly
    # which commit that is: the right branch, and level with its remote.
    $branch = Get-GitOutput @('rev-parse', '--abbrev-ref', 'HEAD')

    if ($branch -eq 'HEAD') {
        $problems.Add("Detached HEAD. Check out $ReleaseBranch before releasing.")
    }
    elseif ($branch -ne $ReleaseBranch) {
        $problems.Add("On branch '$branch', but releases are cut from '$ReleaseBranch'.`n" +
                      "    git switch $ReleaseBranch    (or pass -ReleaseBranch $branch)")
    }

    # Compare HEAD against the remote tip rather than asking whether HEAD is
    # merely an ancestor of it - being behind would pass that weaker test and
    # quietly ship stale code.
    $remoteRef = "origin/$ReleaseBranch"
    $localSha  = Get-GitOutput @('rev-parse', 'HEAD')
    $remoteSha = Get-GitOutput @('rev-parse', $remoteRef)

    if (-not $remoteSha) {
        $problems.Add("No $remoteRef. Push the branch first: git push -u origin $ReleaseBranch")
    }
    elseif ($localSha -ne $remoteSha) {
        # Which way are we out of step? The fix differs.
        $ahead  = [int](Get-GitOutput @('rev-list', '--count', "$remoteRef..HEAD"))
        $behind = [int](Get-GitOutput @('rev-list', '--count', "HEAD..$remoteRef"))

        if ($ahead -gt 0 -and $behind -gt 0) {
            $problems.Add("HEAD and $remoteRef have diverged ($ahead ahead, $behind behind). Reconcile before releasing.")
        }
        elseif ($ahead -gt 0) {
            $problems.Add("$ahead commit(s) not pushed. The release is cut from the tagged commit:`n" +
                          "    git push origin $ReleaseBranch")
        }
        else {
            $problems.Add("$behind commit(s) behind $remoteRef. You would release stale code:`n" +
                          "    git pull")
        }
    }

    if ($problems.Count -eq 0) {
        Write-Note "gh authenticated, HEAD is on the remote"
        return
    }

    if (-not $Soft) { throw ($problems -join "`n") }

    foreach ($p in $problems) { Write-Warn "would fail: $p" }
}

function New-StagingTree {
    <#
        Builds the exact tree that becomes the zip: the build output at the
        root, plus every $ExtraPayload entry. Staging rather than zipping the
        build directory in place means the archive holds what was declared and
        nothing else - no stray .pdb, no leftover from a previous build.

        A trailing slash on an $ExtraPayload entry means "recurse this
        directory", and it lands in the zip under its own name.

        The caller is responsible for removing the returned directory.
    #>
    param([Parameter(Mandatory)][string]$BuildOutput)

    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ("release-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    try {
        if (-not (Test-Path -LiteralPath $BuildOutput)) {
            throw "The build reported success but produced nothing at $BuildOutput."
        }
        Copy-Item -Path (Join-Path $BuildOutput '*') -Destination $staging -Recurse -Force

        foreach ($entry in $ExtraPayload) {
            $isDir  = $entry -match '[/\\]$'
            $rel    = $entry.TrimEnd('/', '\')
            $source = Get-RepoPath $rel
            $dest   = Join-Path $staging (Split-Path -Leaf $rel)

            if ($isDir) {
                Copy-Item -LiteralPath $source -Destination $dest -Recurse -Force
            }
            else {
                Copy-Item -LiteralPath $source -Destination $dest -Force
            }
        }
    }
    catch {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }

    return $staging
}

function New-ReleaseZip {
    <#
        Not Compress-Archive, for two reasons that each cost a release run to
        find. It opens each entry without FileShare.Read, so anything holding a
        file open - Dropbox indexing the folder, an antivirus scan, a
        just-tested process still tearing down - fails the archive. Worse, it
        reports that as a *non-terminating* error, which sails straight past
        $ErrorActionPreference = 'Stop' and leaves the script exiting 0 with no
        zip.

        Not ZipFile::CreateFromDirectory either, for a third reason: on .NET
        Framework it writes entry names with the platform separator, so a
        nested path is stored as "config\app.conf". Windows tools cope; unzip
        on macOS and Linux treats the backslash as part of the filename and
        drops the whole tree into one oddly named file. The zip spec says
        forward slashes, so the entries are added one at a time and named
        explicitly here.
    #>
    param(
        [Parameter(Mandatory)][string]$SourceDir,
        [Parameter(Mandatory)][string]$ZipPath
    )

    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }

    # Both assemblies: ZipFile and ZipFileExtensions live in the FileSystem
    # one, ZipArchive and ZipArchiveMode in the other. Loading only the first
    # fails at the ZipArchiveMode reference with a TypeNotFound.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $level  = [System.IO.Compression.CompressionLevel]::Optimal
    $prefix = (Resolve-Path -LiteralPath $SourceDir).Path.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $files  = @(Get-ChildItem -LiteralPath $SourceDir -Recurse -File)

    if ($files.Count -eq 0) { throw "Nothing to package: $SourceDir is empty." }

    $attempt = 0
    while ($true) {
        $attempt++
        try {
            $archive = [System.IO.Compression.ZipFile]::Open(
                $ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
            try {
                foreach ($file in $files) {
                    $entryName = $file.FullName.Substring($prefix.Length).Replace('\', '/')
                    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                        $archive, $file.FullName, $entryName, $level) | Out-Null
                }
            }
            finally { $archive.Dispose() }
            break
        }
        catch [System.IO.IOException] {
            if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
            if ($attempt -ge 5) { throw "Could not package after $attempt attempts: $($_.Exception.Message)" }
            Write-Warn "packaging attempt $attempt hit a locked file; retrying in 3s"
            Start-Sleep -Seconds 3
        }
    }

    # Belt and braces: the step above must not be able to report success
    # without producing a file.
    if (-not (Test-Path -LiteralPath $ZipPath)) {
        throw "Packaging reported success but $ZipPath does not exist."
    }
}

function Set-ManifestText {
    <#
        Writes a version-bumped manifest back to disk, preserving whether the
        file had a byte order mark.

        Not Set-Content -Encoding UTF8, which in Windows PowerShell 5.1 means
        UTF-8 *with* a BOM. That is not cosmetic: electron-builder's Go-based
        app-builder rejects a package.json starting with one outright -
        "readObjectStart: expect { or n" - and it upsets enough other non-.NET
        tooling that a version bump should never introduce one.

        Preserving rather than always stripping, because re-encoding a file the
        project deliberately saved with a BOM is not this script's decision to
        make.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Text
    )

    $existing = [System.IO.File]::ReadAllBytes($Path)
    $hadBom   = $existing.Length -ge 3 -and
                $existing[0] -eq 0xEF -and $existing[1] -eq 0xBB -and $existing[2] -eq 0xBF

    if ($hadBom) {
        Write-Warn ("$(Split-Path -Leaf $Path) already has a byte order mark, which is kept. " +
                    "Some non-.NET tooling refuses to parse it - strip it if a build complains.")
    }

    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($hadBom)))
}

function Read-ChangelogLines {
    <#
        Returns the changelog split into lines, or throws if it is missing.

        Read as UTF-8 explicitly: Get-Content in PS 5.1 decodes a BOM-less file
        as CP-1252, which would mangle any non-ASCII text in the release notes.
    #>
    $path = Join-Path $RepoRoot $ChangelogFile
    if (-not (Test-Path -LiteralPath $path)) {
        throw ("$ChangelogFile is missing. Create it with a '## Unreleased' section, or set " +
               "ChangelogFile = '' in release.settings.psd1 to turn this check off.")
    }
    return ,([System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8) -split '\r?\n')
}

function Get-ChangelogHeadings {
    <#
        Finds the '## ' headings in the changelog, newest first. Each has Line
        (0-based), Version ($null if none) and Unreleased.

        Accepts '## 1.2.0', '## [1.2.0]', '## v1.2.0', with anything after them,
        such as a date. Any heading with the word 'unreleased' is the pending
        section, including '## 1.2.0 (unreleased)'. Lines inside a code fence
        are skipped, because a shell comment there looks exactly like a heading.
    #>
    param([Parameter(Mandatory)][AllowEmptyString()][string[]]$Lines)

    $headings = [System.Collections.Generic.List[object]]::new()
    $inFence  = $false

    for ($i = 0; $i -lt $Lines.Count; $i++) {
        if ($Lines[$i] -match '^\s*(```|~~~)') { $inFence = -not $inFence; continue }
        if ($inFence -or $Lines[$i] -notmatch '^##\s') { continue }

        $unreleased = $Lines[$i] -match '\bunreleased\b'
        $found      = $null
        if (-not $unreleased -and
            $Lines[$i] -match '^##\s+\[?v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\]?(?:\s|$)') {
            $found = $Matches[1]
        }

        $headings.Add([pscustomobject]@{ Line = $i; Version = $found; Unreleased = $unreleased })
    }

    return ,$headings.ToArray()
}

function Get-ChangelogSectionText {
    <# The trimmed text under one heading, up to the next heading or the end. #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string[]]$Lines,
        [Parameter(Mandatory)][object[]]$Headings,
        [Parameter(Mandatory)][object]$Heading
    )

    $next = @($Headings | Where-Object { $_.Line -gt $Heading.Line } | Select-Object -First 1)
    $last = if ($next.Count -gt 0) { $next[0].Line - 1 } else { $Lines.Count - 1 }
    if ($last -le $Heading.Line) { return '' }

    return ($Lines[($Heading.Line + 1)..$last] -join "`n").Trim()
}

function Get-ChangelogNotes {
    <#
        Resolves the manifest against the changelog, and returns the release
        notes. The manifest owns the number and the changelog owns the notes,
        so neither wins a disagreement: the script stops and names both files.

        Compared with the newest heading, not just any heading, which also
        catches a changelog that has moved on ahead of the manifest.

        Returns $null when the changelog is off, or with -Soft when a problem
        was reported as a warning.
    #>
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][bool]$Soft
    )

    if (-not $ChangelogFile) {
        Write-Note "changelog check off - GitHub will generate the release notes"
        return $null
    }

    try {
        $lines    = Read-ChangelogLines
        $headings = Get-ChangelogHeadings -Lines $lines
        $versions = @($headings | Where-Object { $_.Version })
        $newest   = if ($versions.Count -gt 0) { $versions[0] } else { $null }

        if (-not $newest) {
            throw ("$ChangelogFile has no version section, but $VersionFile says $ReleaseVersion. " +
                   "Run: .\scripts\release.ps1 -Version $ReleaseVersion -Apply")
        }
        if ($newest.Version -ne $ReleaseVersion) {
            throw ("$ChangelogFile newest version is $($newest.Version), but $VersionFile says " +
                   "$ReleaseVersion. Update one so they match.")
        }

        $notes = Get-ChangelogSectionText -Lines $lines -Headings $headings -Heading $newest
        if (-not $notes) {
            throw "The $ReleaseVersion section in $ChangelogFile is empty. Add the release notes."
        }
    }
    catch {
        if (-not $Soft) { throw }
        Write-Warn "would fail: $($_.Exception.Message)"
        return $null
    }

    Write-Note "$ChangelogFile section for $ReleaseVersion found"
    return $notes
}

function Get-RolledChangelog {
    <#
        Works out the changelog for a new version and writes nothing, so that a
        problem stops -Version before any file has changed.

        Returns the new file text, or $null if there is nothing to write: the
        changelog is off, the version section already exists, or -Soft reported
        a problem as a warning.

        A fresh empty '## Unreleased' goes above the new section, so the next
        release has somewhere to collect notes without a hand edit.
    #>
    param(
        [Parameter(Mandatory)][string]$NewVersion,
        [Parameter(Mandatory)][bool]$Soft
    )

    if (-not $ChangelogFile) { return $null }

    try {
        $lines    = Read-ChangelogLines
        $headings = Get-ChangelogHeadings -Lines $lines
        $pending  = @($headings | Where-Object { $_.Unreleased } | Select-Object -First 1)
        $section  = @($headings | Where-Object { $_.Version -eq $NewVersion })

        if ($section.Count -gt 0) {
            Write-Note "$ChangelogFile already has a $NewVersion section - left as it is"
            if ($pending.Count -gt 0 -and
                (Get-ChangelogSectionText -Lines $lines -Headings $headings -Heading $pending[0])) {
                Write-Warn "$ChangelogFile has entries under Unreleased that will not ship in $NewVersion"
            }
            return $null
        }

        if ($pending.Count -eq 0) {
            throw ("$ChangelogFile has no '## Unreleased' section and no $NewVersion section. " +
                   "Add one of them with the release notes.")
        }
        if (-not (Get-ChangelogSectionText -Lines $lines -Headings $headings -Heading $pending[0])) {
            throw "The Unreleased section in $ChangelogFile is empty. Add the release notes."
        }
    }
    catch {
        if (-not $Soft) { throw }
        Write-Warn "would fail: $($_.Exception.Message)"
        return $null
    }

    $today = Get-Date -Format 'yyyy-MM-dd'
    $lines[$pending[0].Line] = "## Unreleased`n`n## $NewVersion ($today)"

    # Keep the file's own line endings, so the diff is only the changed lines.
    $raw = [System.IO.File]::ReadAllText((Join-Path $RepoRoot $ChangelogFile), [System.Text.Encoding]::UTF8)
    $eol = if ($raw.Contains("`r`n")) { "`r`n" } else { "`n" }
    return (($lines -join "`n") -replace "`n", $eol)
}

function Write-ReleaseNotesFile {
    <#
        Saves the notes for gh --notes-file and returns the path. Outside the
        repo so it cannot dirty the tree, and as UTF-8 with no BOM so the first
        line of the notes cannot pick up a stray character on GitHub.
    #>
    param(
        [Parameter(Mandatory)][string]$TagName,
        [Parameter(Mandatory)][string]$Notes
    )

    $path = Join-Path ([System.IO.Path]::GetTempPath()) "$ProjectName-$TagName-notes.md"
    [System.IO.File]::WriteAllText($path, $Notes, (New-Object System.Text.UTF8Encoding($false)))
    return $path
}

function New-ChecksumSidecar {
    <#
        Writes "<hash>  <filename>" beside the file, in the format sha256sum -c
        and CertUtil-adjacent tooling expect - two spaces, and the bare
        filename rather than a path, so verification works from whatever
        directory the file was downloaded into.
    #>
    param([Parameter(Mandatory)][string]$FilePath)

    $name = Split-Path -Leaf $FilePath
    $hash = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$FilePath.sha256" -Value "$hash  $name" -Encoding ASCII

    $sizeMb = [math]::Round((Get-Item -LiteralPath $FilePath).Length / 1MB, 2)
    Write-Note "$name  ($sizeMb MB)"
    Write-Note "  sha256 $hash"

    return "$FilePath.sha256"
}

# x.y.z with an optional -prerelease. The same pattern every seam's
# Get-ProjectVersion accepts, so -Version can never write a version the next
# run refuses to read.
$SemVerPattern = '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$'

function Split-SemVer {
    <# The numeric core (three bigints) and the prerelease identifiers (empty when none). #>
    param([Parameter(Mandatory)][string]$Value)

    # Assigned, never emitted from an if-statement: PowerShell unrolls a
    # statement's output, which turns @() into $null and @('beta') into 'beta'.
    $pre  = @()
    $core = $Value
    $dash = $Value.IndexOf('-')
    if ($dash -ge 0) {
        $core = $Value.Substring(0, $dash)
        $pre  = $Value.Substring($dash + 1).Split('.')
    }

    # bigint, not int: '1.0.99999999999' passes the pattern and would
    # overflow an int with an unhelpful error.
    return [pscustomobject]@{
        Core = @($core.Split('.') | ForEach-Object { [bigint]::Parse($_) })
        Pre  = $pre
    }
}

function Compare-SemVer {
    <#
        -1, 0 or 1 as Left is lower than, equal to or higher than Right, by
        SemVer precedence: the numeric core first, then a prerelease sorts
        below its release (1.3.0-beta < 1.3.0), then prerelease identifiers
        left to right, numbers numerically and below any text. A plain string
        compare gets both 1.10.0 vs 1.9.0 and 1.3.0-beta vs 1.3.0 wrong.
    #>
    param([Parameter(Mandatory)][string]$Left, [Parameter(Mandatory)][string]$Right)

    $a = Split-SemVer $Left
    $b = Split-SemVer $Right

    for ($i = 0; $i -lt 3; $i++) {
        if ($a.Core[$i] -lt $b.Core[$i]) { return -1 }
        if ($a.Core[$i] -gt $b.Core[$i]) { return 1 }
    }

    if ($a.Pre.Count -eq 0 -and $b.Pre.Count -eq 0) { return 0 }
    if ($a.Pre.Count -eq 0) { return 1 }
    if ($b.Pre.Count -eq 0) { return -1 }

    $shared = [math]::Min($a.Pre.Count, $b.Pre.Count)
    for ($i = 0; $i -lt $shared; $i++) {
        $x = $a.Pre[$i]
        $y = $b.Pre[$i]
        $xNumeric = $x -match '^\d+$'
        $yNumeric = $y -match '^\d+$'

        if ($xNumeric -and $yNumeric) {
            $xv = [bigint]::Parse($x)
            $yv = [bigint]::Parse($y)
            if ($xv -lt $yv) { return -1 }
            if ($xv -gt $yv) { return 1 }
        }
        elseif ($xNumeric) { return -1 }
        elseif ($yNumeric) { return 1 }
        else {
            $c = [string]::CompareOrdinal($x, $y)
            if ($c -ne 0) { return [math]::Sign($c) }
        }
    }
    return [math]::Sign($a.Pre.Count - $b.Pre.Count)
}

function Get-VersionBumpLabel {
    <# Names the kind of bump from From to To. Only meaningful when To is higher. #>
    param([Parameter(Mandatory)][string]$From, [Parameter(Mandatory)][string]$To)

    $a     = Split-SemVer $From
    $b     = Split-SemVer $To
    $parts = @('major', 'minor', 'patch')

    for ($i = 0; $i -lt 3; $i++) {
        if ($a.Core[$i] -ne $b.Core[$i]) {
            if ($b.Pre.Count -gt 0) { return "$($parts[$i]) bump, prerelease" }
            return "$($parts[$i]) bump"
        }
    }
    # Same core and To is higher, so From was a prerelease.
    if ($b.Pre.Count -eq 0) { return 'prerelease to release' }
    return 'prerelease bump'
}

function Get-VersionTagProblems {
    <#
        Checks git tags for -Version, so a version that has already shipped,
        or that sits below one that has, is caught when it is prepared - not
        after the commit, when -Tag refuses it. Local and remote tags are read
        separately, because a tag deleted on one side often survives on the
        other. Returns the problems; the caller decides whether they stop.
    #>
    param([Parameter(Mandatory)][string]$NewVersion)

    $problems = [System.Collections.Generic.List[string]]::new()
    $tagName  = "v$NewVersion"

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Warn "git is not on PATH, so tags were not checked"
        return
    }
    if ($null -eq (Get-GitOutput @('rev-parse', '--git-dir'))) {
        Write-Warn "not a git repository, so tags were not checked"
        return
    }

    $local = @()
    $out = Get-GitOutput @('tag', '--list', 'v*')
    if ($out) { $local = @($out -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }

    # $null means the remote was not checked, @() that it has no v* tags.
    $remote = $null
    if (Get-GitOutput @('remote', 'get-url', 'origin')) {
        $out = Get-GitOutput @('ls-remote', '--tags', '--refs', 'origin')
        if ($out) {
            $remote = @($out -split "`r?`n" |
                        ForEach-Object { ($_ -split "`t")[-1] -replace '^refs/tags/', '' } |
                        Where-Object { $_ -like 'v*' })
        }
        elseif (Get-GitOutput @('ls-remote', '--heads', 'origin')) {
            # Reachable, just no tags. Empty output alone cannot tell these
            # apart from a remote that could not be reached.
            $remote = @()
        }
        else {
            Write-Warn "could not reach origin, so only local tags were checked"
        }
    }

    $inLocal  = $local -contains $tagName
    $inRemote = ($null -ne $remote) -and ($remote -contains $tagName)

    if ($inLocal -and $inRemote) {
        $problems.Add("Tag $tagName already exists locally and on origin, so $NewVersion has " +
                      "already been released. Pick a higher version.")
    }
    elseif ($inRemote) {
        $problems.Add("Tag $tagName already exists on origin, so $NewVersion has already been " +
                      "released. Pick a higher version.")
    }
    elseif ($inLocal) {
        $problems.Add("Tag $tagName exists locally but not on origin. If that release was " +
                      "abandoned, remove it:`n    git tag -d $tagName")
    }

    $all = @($local)
    if ($remote) { $all += $remote }

    $newest = $null
    foreach ($t in ($all | Select-Object -Unique)) {
        $v = $t.Substring(1)
        if ($v -notmatch $SemVerPattern) { continue }
        if ($null -eq $newest -or (Compare-SemVer -Left $v -Right $newest) -gt 0) { $newest = $v }
    }

    $free = if ($inLocal -or $inRemote) { 'taken' } else { 'free' }
    if ($newest) {
        Write-Note "tags:    newest is v$newest, $tagName is $free"
        if ((Compare-SemVer -Left $NewVersion -Right $newest) -lt 0) {
            $problems.Add("$NewVersion is lower than the newest tag, v$newest. Pick a version above it.")
        }
    }
    else {
        Write-Note "tags:    none yet, $tagName is $free"
    }

    return $problems.ToArray()
}

function Assert-VersionChange {
    <#
        Shows the current and the new version, and checks the new one before
        -Version writes anything. A malformed version always stops. A version
        that is not higher than the current one, is below the newest tag, or
        is already tagged stops unless -Force. A dry run reports those
        instead, so one rehearsal shows everything, as the other checks do.
    #>
    param(
        [Parameter(Mandatory)][string]$NewVersion,
        [switch]$Soft
    )

    if ($NewVersion -notmatch $SemVerPattern) {
        $hint = ''
        if ($NewVersion -match '^[vV]\d') { $hint = " Drop the leading 'v': the tag adds it." }
        throw ("'$NewVersion' is not a valid version. Use x.y.z or x.y.z-prerelease, for " +
               "example 1.3.0 or 1.3.0-beta.1.$hint")
    }

    $problems = [System.Collections.Generic.List[string]]::new()

    # An unreadable current version does not stop -Version, because -Version
    # is how that file gets fixed. Set-ProjectVersion still fails if the
    # field is missing altogether.
    $current = $null
    try { $current = Get-ProjectVersion }
    catch { Write-Note "current: unreadable - $($_.Exception.Message)" }

    if ($current) {
        Write-Note "current: $current (from $VersionFile)"
        $order = Compare-SemVer -Left $NewVersion -Right $current
        if ($order -gt 0) {
            Write-Note "new:     $NewVersion ($(Get-VersionBumpLabel -From $current -To $NewVersion))"
        }
        elseif ($order -eq 0) {
            Write-Note "new:     $NewVersion (no change)"
            $problems.Add("$VersionFile is already at $NewVersion. Pick a higher version.")
        }
        else {
            Write-Note "new:     $NewVersion (LOWER than current)"
            $problems.Add("$NewVersion is lower than the current $current in $VersionFile. " +
                          "Pick a higher version.")
        }
    }
    else {
        Write-Note "new:     $NewVersion"
    }

    foreach ($p in @(Get-VersionTagProblems -NewVersion $NewVersion)) { $problems.Add($p) }

    if ($problems.Count -eq 0) { return }

    if ($Force) {
        foreach ($p in $problems) { Write-Warn "-Force, going ahead anyway: $p" }
        return
    }
    if ($Soft) {
        foreach ($p in $problems) { Write-Warn "would fail: $p" }
        Write-Warn "Add -Force to go ahead anyway."
        return
    }
    throw (($problems -join "`n") + "`nRe-run with -Force to go ahead anyway.")
}

function New-ReleaseTag {
    param(
        [Parameter(Mandatory)][string]$TagName,
        [Parameter(Mandatory)][bool]$WhatIf
    )

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw "-Tag was requested but git is not on PATH."
    }

    # As with the publish checks: a dry run reports these rather than stopping,
    # so one rehearsal surfaces everything standing between you and a release.
    $problems = [System.Collections.Generic.List[string]]::new()

    $status = Get-GitOutput @('status', '--porcelain')
    if ($status) {
        $problems.Add("-Tag requires a clean working tree. Commit or stash first:`n$status")
    }

    # Local and remote tags are separate things, and deleting a release on
    # GitHub leaves the local tag behind. Report which copy is in the way, so
    # the fix is not a guess.
    $localTag = [bool](Get-GitOutput @('tag', '--list', $TagName))

    $remoteTag = $false
    if (Get-GitOutput @('remote')) {
        # Distinguish "no such tag" from "could not ask": both come back empty,
        # but only one of them means the tag is free to use.
        $reachable = Get-GitOutput @('ls-remote', '--exit-code', '--tags', 'origin', $TagName)
        if ($reachable) {
            $remoteTag = $true
        }
        elseif ($null -eq (Get-GitOutput @('ls-remote', '--heads', 'origin'))) {
            $problems.Add("Could not reach the remote to check whether tag $TagName is free.")
        }
    }

    if ($localTag -and $remoteTag) {
        $problems.Add("Tag $TagName already exists locally and on the remote. Pick another version, or:`n" +
                      "    gh release delete $TagName --cleanup-tag --yes`n" +
                      "    git tag -d $TagName")
    }
    elseif ($localTag) {
        $problems.Add("Tag $TagName exists locally but not on the remote. If you deleted it on GitHub, " +
                      "remove the local copy too:`n    git tag -d $TagName")
    }
    elseif ($remoteTag) {
        $problems.Add("Tag $TagName already exists on the remote. Pick another version, or:`n" +
                      "    gh release delete $TagName --cleanup-tag --yes")
    }

    if ($problems.Count -gt 0) {
        if (-not $WhatIf) { throw ($problems -join "`n") }
        foreach ($p in $problems) { Write-Warn "would fail: $p" }
    }

    if ($WhatIf) {
        Write-Note "would create annotated tag $TagName"
        return
    }

    Invoke-Native -What "git tag $TagName" -Command {
        git -C $RepoRoot tag -a $TagName -m "Release $TagName"
    } | Out-Null
    Write-Note "created annotated tag $TagName"
}

function Publish-Release {
    param(
        [Parameter(Mandatory)][string]$TagName,

        # AllowEmptyCollection because a rehearsal in 'artifacts' mode has
        # nothing to list: the installers do not exist until something is
        # actually built. A mandatory [string[]] rejects an empty array before
        # the function body ever runs, which turned an ordinary dry run into a
        # parameter binding error.
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Assets,

        # The release notes. Empty means GitHub generates them.
        [string]$Notes,

        [Parameter(Mandatory)][bool]$WhatIf
    )

    if ($WhatIf) {
        Write-Note "would push $TagName and create the GitHub release"
        if ($Notes) { Write-Note "  notes: the $ChangelogFile section" }
        if ($Assets.Count -eq 0) {
            Write-Note "  assets: none yet - nothing is built, so this rehearsal cannot name them"
            Write-Note "  run with -Apply, or build first, to see the real list"
        }
        foreach ($a in $Assets) { Write-Note "  asset: $(Split-Path -Leaf $a)" }
        return
    }

    # A release with no downloads is worse than no release: it looks published.
    if ($Assets.Count -eq 0) {
        throw "Refusing to publish $TagName with no assets. The build produced nothing to upload."
    }

    Write-Note "pushing $TagName"
    $pushExit = Invoke-Native -What "git push origin $TagName" -AllowFailure -Command {
        git -C $RepoRoot push origin $TagName
    }
    if ($pushExit -ne 0) {
        throw ("Pushing the tag $TagName failed with exit code $pushExit. The tag exists " +
               "locally but not on the remote, and no release was created - so nothing is " +
               "half-published.`n" +
               "Common causes: the tag is already on the remote (a previous release was " +
               "deleted without --cleanup-tag), no push access, or a tag protection rule.`n" +
               "  git ls-remote --tags origin $TagName   # is it already there?`n" +
               "  git tag -d $TagName                    # drop the local tag and start over")
    }

    Write-Note "creating GitHub release"
    $releaseExists = $false

    # Repo-relative, so a '#' anywhere above the repo root cannot be mistaken
    # for gh's asset-label separator.
    $ghAssets = Get-GhAssetArguments -Assets $Assets

    $notesFile = $null
    $notesArgs = @('--generate-notes')
    if ($Notes) {
        $notesFile = Write-ReleaseNotesFile -TagName $TagName -Notes $Notes
        $notesArgs = @('--notes-file', $notesFile)
    }

    # gh resolves the repo from the git remote of its working directory, so run
    # it there. (--repo takes OWNER/REPO, never a filesystem path.) That working
    # directory is also what the relative asset paths above are resolved against.
    Push-Location $RepoRoot
    try {
        $exit = Invoke-Native -What 'gh release create' -AllowFailure -Command {
            gh release create $TagName @ghAssets --title $TagName @notesArgs
        }

        if ($exit -ne 0) {
            # Which recovery command to use depends on whether the release got
            # created before the failure, so find out rather than guess: a
            # release that exists cannot be created again, and telling someone
            # to retry a command that is guaranteed to fail wastes their time.
            $releaseExists = 0 -eq (Invoke-Native -What 'gh release view' -AllowFailure -Command {
                gh release view $TagName *> $null
            })
        }
    }
    finally { Pop-Location }

    # Kept on failure: the recovery command printed below still needs the file.
    if ($exit -eq 0 -and $notesFile) {
        Remove-Item -LiteralPath $notesFile -Force -ErrorAction SilentlyContinue
    }

    if ($exit -ne 0) {
        $assetList = Format-CommandArguments -Arguments $ghAssets
        $notesList = Format-CommandArguments -Arguments $notesArgs

        $lines = [System.Collections.Generic.List[string]]::new()
        $lines.Add("gh release create failed with exit code $exit.")
        $lines.Add("The tag $TagName is already pushed and the artifacts are built, so")
        $lines.Add('nothing needs redoing - only the upload has to be finished.')
        $lines.Add('')
        $lines.Add("Run from $RepoRoot")
        $lines.Add('')

        if ($releaseExists) {
            $lines.Add("The release $TagName EXISTS - it was created before the failure, so")
            $lines.Add('creating it again will fail. Finish the upload instead:')
            $lines.Add("    gh release upload $TagName $assetList --clobber")
        }
        else {
            $lines.Add('The release was not created. Retry it:')
            $lines.Add("    gh release create $TagName $assetList --title $TagName $notesList")
        }

        $lines.Add('')
        $lines.Add('Common causes: the gh token lost its workflow/repo scope (gh auth refresh),')
        $lines.Add('an asset larger than 2 GB, or a release someone created on the web already.')
        $lines.Add('')
        $lines.Add('To back the whole release out instead:')
        $lines.Add("    gh release delete $TagName --cleanup-tag --yes")
        $lines.Add("    git tag -d $TagName")

        throw ($lines -join "`n")
    }
}

function Get-NasReleaseDir {
    param([Parameter(Mandatory)][string]$ReleaseVersion)

    # A rehearsal still reaches the ship step with $NasRoot unset; preflight
    # has already reported that, so name the gap rather than crash on it.
    $base = if ($NasRoot) { $NasRoot } else { '<$NasRoot not set>' }
    return Join-Path (Join-Path $base $ProjectName) $ReleaseVersion
}

function Assert-NasReady {
    <#
        The -Ship half of preflight. Checked before the build, so a share that
        is offline, or a version that is already on it, fails in seconds and
        not after a full build.
    #>
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][bool]$Soft
    )

    $problems = [System.Collections.Generic.List[string]]::new()

    if (-not $NasRoot) {
        $problems.Add("-Ship needs a NAS share. Set NasRoot in release.settings.psd1, " +
                      "for example: NasRoot = '\\nas\releases'")
    }
    elseif (-not [System.IO.Path]::IsPathRooted($NasRoot)) {
        $problems.Add("`$NasRoot is '$NasRoot'. It must be an absolute path, such as \\nas\releases.")
    }
    # Not Test-Path: on PS 5.1 it throws "network path was not found" for an
    # unreachable server instead of returning false, which ends a rehearsal
    # that is meant to report every problem and carry on.
    elseif (-not [System.IO.Directory]::Exists($NasRoot)) {
        $problems.Add("Cannot reach $NasRoot. Check that the NAS is on and that this account " +
                      "can open the share. A mapped drive letter is not visible from an " +
                      "elevated shell; use the UNC path.")
    }
    else {
        $dest = Get-NasReleaseDir -ReleaseVersion $ReleaseVersion
        if ((Test-Path -LiteralPath $dest) -and -not $Force) {
            $problems.Add("$dest already exists, so $ReleaseVersion is already on the NAS. " +
                          "Bump the version, or re-run with -Force to replace it.")
        }
    }

    if ($problems.Count -eq 0) {
        Write-Note "NAS reachable: $NasRoot"
        return
    }

    if (-not $Soft) { throw ($problems -join "`n") }

    foreach ($p in $problems) { Write-Warn "would fail: $p" }
}

function Find-ExistingAssets {
    <#
        The finished artifacts for this version, each followed by its .sha256,
        or nothing when there are none. -Ship without -Tag uses this to copy
        what is already built instead of building it again.

        The sidecar is the proof that a build finished: packaging writes it
        last, so an artifact without one is from a build that stopped halfway.
        An artifact without the version in its name is from another release.
    #>
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][string]$ArtifactDir
    )

    if ($PackageMode -eq 'zip') {
        $artifacts = @($ZipPath)
    }
    else {
        if (-not (Test-Path -LiteralPath $ArtifactDir)) { return }

        # Get-ReleaseArtifacts throws when nothing matches, and warns about
        # files from other versions. Here both only mean "nothing to reuse",
        # which the caller reports, so its own messages are muted (stream 6
        # is Write-Host).
        try {
            $artifacts = @(Get-ReleaseArtifacts -BuildOutput $ArtifactDir `
                                                -ReleaseVersion $ReleaseVersion -WhatIf:$false 6>$null)
        }
        catch {
            return
        }
    }

    foreach ($a in $artifacts) {
        if (-not (Test-Path -LiteralPath $a))          { return }
        if (-not (Test-Path -LiteralPath "$a.sha256")) { return }
        if ((Split-Path -Leaf $a) -notmatch [regex]::Escape($ReleaseVersion)) { return }
    }

    foreach ($a in $artifacts) { $a; "$a.sha256" }
}

function Copy-ReleaseToNas {
    <#
        Copies the assets into <NasRoot>\<ProjectName>\<version>\ and checks
        each copy against its .sha256.

        The files go to a temporary folder beside the target first, and that
        folder gets the version name only when every copy has checked out. A
        copy that dies halfway (the NAS drops off the network, its disk fills)
        then leaves a folder whose name says it is unfinished, never a version
        folder that looks complete and is not.
    #>
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,

        # AllowEmptyCollection for the same reason as Publish-Release: an
        # artifacts-mode rehearsal cannot name files that are not built yet.
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Assets,

        [Parameter(Mandatory)][bool]$WhatIf
    )

    $dest = Get-NasReleaseDir -ReleaseVersion $ReleaseVersion

    if ($WhatIf) {
        Write-Note "would copy to $dest"
        if ($Assets.Count -eq 0) {
            Write-Note "  files: none yet - nothing is built, so this rehearsal cannot name them"
        }
        foreach ($a in $Assets) { Write-Note "  $(Split-Path -Leaf $a)" }
        return
    }

    if ($Assets.Count -eq 0) {
        throw "Refusing to ship $ReleaseVersion with no files. The build produced nothing to copy."
    }

    $projectDir = Split-Path -Parent $dest
    if (-not (Test-Path -LiteralPath $projectDir)) {
        New-Item -ItemType Directory -Path $projectDir -Force | Out-Null
    }

    $staging = "$dest.partial-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    New-Item -ItemType Directory -Path $staging | Out-Null

    try {
        foreach ($a in $Assets) {
            Copy-Item -LiteralPath $a -Destination $staging
        }

        # Hash the copy, not the source: this is the check that the bytes on
        # the NAS are the bytes that were built.
        foreach ($a in $Assets) {
            if ($a -like '*.sha256') { continue }
            $name    = Split-Path -Leaf $a
            $sidecar = Join-Path $staging "$name.sha256"
            if (-not (Test-Path -LiteralPath $sidecar)) {
                throw "$name has no .sha256 to check its copy against."
            }
            $expected = ((Get-Content -LiteralPath $sidecar -TotalCount 1) -split '\s+')[0].ToLowerInvariant()
            $actual   = (Get-FileHash -LiteralPath (Join-Path $staging $name) -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $expected) {
                throw ("The copy of $name on the NAS does not match its .sha256 " +
                       "(got $actual, expected $expected). Re-run to copy it again.")
            }
            Write-Note "$name copied, sha256 matches"
        }
    }
    catch {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }

    # Outside the try on purpose: past this point the staging folder is a
    # complete, checked copy, and a failure must not delete it.
    try {
        if (Test-Path -LiteralPath $dest) {
            # Assert-NasReady refuses an existing folder without -Force, but the
            # build ran in between and someone else may have shipped meanwhile.
            if (-not $Force) { throw "$dest appeared during the build. Re-run with -Force to replace it." }
            Remove-Item -LiteralPath $dest -Recurse -Force
            Write-Note "replaced the previous $dest (-Force)"
        }
        Rename-Item -LiteralPath $staging -NewName (Split-Path -Leaf $dest)
    }
    catch {
        throw ("A complete, checked copy is in $staging, but it could not be renamed to " +
               "$dest`: $($_.Exception.Message)`nRename it by hand, or delete it and re-run.")
    }

    Write-Note "shipped to $dest"
}

# ---------------------------------------------------------------------------
# Ecosystem seam: C# / .NET.
#
# These four functions are the only difference between this script and the
# node, python, powershell and electron templates. Everything above and
# below is identical across all five.
# ---------------------------------------------------------------------------

function Get-ProjectVersion {
    $path = Get-RepoPath $VersionFile

    $xml  = [xml](Get-Content -LiteralPath $path -Raw)
    $node = $xml.SelectSingleNode('/Project/PropertyGroup/Version')
    if (-not $node) {
        throw "No <Version> element in $VersionFile. Add one."
    }

    $value = $node.InnerText.Trim()
    if ($value -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw "<Version> in $VersionFile is '$value', which is not x.y.z. Fix it."
    }
    return $value
}

function Set-ProjectVersion {
    <#
        Patches the <Version> element's text in place. Loading and re-saving
        the XML would reformat the whole csproj - self-closing tags, attribute
        order, the XML declaration - and turn a one-line version bump into an
        unreviewable diff.
    #>
    param(
        [Parameter(Mandatory)][string]$NewVersion,
        [Parameter(Mandatory)][bool]$WhatIf
    )

    $path    = Get-RepoPath $VersionFile
    $text    = Get-Content -LiteralPath $path -Raw
    $pattern = '(?m)^(\s*<Version>)[^<]*(</Version>)'

    if ($text -notmatch $pattern) {
        throw "Could not find a <Version> element in $VersionFile to update."
    }

    if ($WhatIf) {
        Write-Note "would set $VersionFile <Version> to $NewVersion"
        return
    }

    $updated = [regex]::Replace($text, $pattern, "`${1}$NewVersion`${2}", 1)
    Set-ManifestText -Path $path -Text $updated
    Write-Note "$VersionFile <Version> set to $NewVersion"
}

function Invoke-ProjectTests {
    param([Parameter(Mandatory)][bool]$WhatIf)

    if (-not $TestProject) {
        Write-Note "no test project configured - skipping"
        return
    }
    $testPath = Join-Path $RepoRoot $TestProject
    if (-not (Test-Path -LiteralPath $testPath)) {
        Write-Note "$TestProject does not exist - skipping"
        return
    }

    if ($WhatIf) { Write-Note "would run dotnet test $TestProject -c Release"; return }

    Invoke-Native -What 'dotnet test' -Command {
        dotnet test $testPath -c Release --nologo
    } | Out-Null
}

function Invoke-ProjectBuild {
    <#
        Returns the absolute path to the directory whose contents become the
        zip. Always returns it, dry run or not, so the packaging step can
        report where the artifact would have come from.
    #>
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][bool]$WhatIf
    )

    $outPath = Join-Path $RepoRoot $BuildDir
    $proj    = Join-Path $RepoRoot $VersionFile

    # -p:Version on the command line rather than relying on the csproj, so the
    # assembly always carries the version this release is named after.
    $publishArgs = @(
        'publish', $proj,
        '-c', 'Release',
        '-r', $Rid,
        '--self-contained', $SelfContained.ToString().ToLowerInvariant(),
        '-o', $outPath,
        "-p:Version=$ReleaseVersion",
        '--nologo'
    ) + $PublishProps

    if ($WhatIf) {
        Write-Note "would run dotnet $($publishArgs -join ' ')"
        Write-Note "would produce $outPath"
        return $outPath
    }

    # Wipe first: dotnet publish merges into an existing output directory, so
    # a renamed or deleted file from a previous build would otherwise survive
    # into the zip.
    if (Test-Path -LiteralPath $outPath) {
        Remove-Item -LiteralPath $outPath -Recurse -Force
    }

    Invoke-Native -What 'dotnet publish' -Command { dotnet @publishArgs } | Out-Null

    # A publish that emits no executable has failed in a way dotnet reports as
    # success often enough to be worth checking.
    $exe = Get-ChildItem -LiteralPath $outPath -Filter '*.exe' -ErrorAction SilentlyContinue |
           Select-Object -First 1
    if (-not $exe) {
        throw "dotnet publish produced no .exe in $BuildDir."
    }

    $mb = [math]::Round((Get-ChildItem -LiteralPath $outPath -Recurse -File |
          Measure-Object -Property Length -Sum).Sum / 1MB, 1)
    Write-Note "published $($exe.Name) ($mb MB before compression)"

    # A cheap end-to-end proof that the published binary actually starts.
    # Adapt or delete: not every app has a --version flag.
    # Invoke-Native -What 'smoke test' -Command { & $exe.FullName --version } | Out-Null

    return $outPath
}

# ---------------------------------------------------------------------------
# Main. Identical in every release.ps1 template. The nine steps below are the
# whole release, and they run in this order in every repo regardless of
# language - only the four seam functions above differ.
# ---------------------------------------------------------------------------

# -Publish is -Tag plus the remote half.
if ($Publish) { $Tag = $true }

# -Ship without -Tag may copy a build that already exists rather than make a
# new one, so build-first-ship-later needs no -Force rebuild. With -Tag the
# build always runs: a tag has to point at what was just built and tested.
$mayReuse = $Ship -and -not $Tag

# -Version writes and -Publish reads, and never in the same run. The bump has to
# be committed before the tag, and a script that wrote it and then tagged would
# dirty its own tree and only find out after the whole build.
if ($Version -and ($Tag -or $Ship)) {
    throw ("-Version cannot be combined with -Tag, -Publish or -Ship. It writes files that have " +
           "to be committed first:`n" +
           "    .\scripts\release.ps1 -Version $Version -Apply`n" +
           "    git commit -am `"Release $Version.`"`n" +
           "    git push origin HEAD`n" +
           "    .\scripts\release.ps1 -Publish -Apply")
}

if ($isDryRun) {
    Write-Host ""
    Write-Warn "DRY RUN - nothing will be built, written, tagged or published."
    Write-Warn "Re-run the same command with -Apply to do it for real."
}

# --- prepare (-Version only) -----------------------------------------------

if ($Version) {
    Write-Step "Prepare $Version"

    # Before anything else, so a bad, lower or already-tagged version stops
    # here with the manifest and the CHANGELOG untouched.
    Assert-VersionChange -NewVersion $Version -Soft:$isDryRun

    # Worked out before anything is written, so a problem with the changelog
    # stops here and leaves the manifest as it was.
    $rolledChangelog = Get-RolledChangelog -NewVersion $Version -Soft:$isDryRun

    Set-ProjectVersion -NewVersion $Version -WhatIf:$isDryRun

    if ($rolledChangelog) {
        if ($isDryRun) {
            Write-Note "would roll Unreleased in $ChangelogFile into a $Version section"
        }
        else {
            Set-ManifestText -Path (Join-Path $RepoRoot $ChangelogFile) -Text $rolledChangelog
            Write-Note "$ChangelogFile rolled into a $Version section"
        }
    }

    Write-Host ""
    if ($isDryRun) {
        Write-Warn "Dry run complete. Nothing was changed."
        Write-Warn "Add -Apply to the same command to run it for real."
    }
    else {
        Write-Step "Next"
        Write-Note "git commit -am `"Release $Version.`""
        Write-Note "git push origin HEAD"
        Write-Note ".\scripts\release.ps1 -Publish          # rehearse"
        Write-Note ".\scripts\release.ps1 -Publish -Apply   # do it"
    }
    Write-Host ""
    return
}

# --- 1. version ------------------------------------------------------------

Write-Step "Version"
$releaseVersion = Get-ProjectVersion
Write-Note "$releaseVersion (from $VersionFile)"

$tagName = "v$releaseVersion"
$zipName = "$ProjectName-$releaseVersion.zip"
$zipPath = Join-Path $OutDir $zipName

if ($PackageMode -eq 'zip') {
    # A version already in the artifact directory has been built, and probably
    # shipped. Rebuilding it under the same number produces a second artifact
    # with the same name and a different hash, which is the one thing a release
    # number exists to prevent.
    # -Ship alone defers this to preflight, where an existing zip is reused.
    if ((Test-Path -LiteralPath $zipPath) -and -not $Force -and -not $mayReuse) {
        $msg = "$zipName already exists in $OutDir. Bump the version in $VersionFile, or re-run with -Force to replace it."
        if ($isDryRun) { Write-Warn "would fail: $msg" } else { throw $msg }
    }
}
else {
    # In artifacts mode the build output directory is wiped and rebuilt every
    # run, so there is no stale-artifact check to make here. The tag check in
    # step 7 is what stops a version being released twice.
}

# --- 2. preflight ----------------------------------------------------------

Write-Step "Preflight"

# Checked here rather than at the packaging step, which is eight minutes and a
# full build later. A typo ('artifacts ', 'zips') would otherwise fall through
# to the else branch and fail on a function that does not exist, and setting
# 'artifacts' in a template that has no Get-ReleaseArtifacts fails the same
# cryptic way.
if ($PackageMode -notin @('zip', 'artifacts')) {
    throw "`$PackageMode is '$PackageMode'. It must be 'zip' or 'artifacts'."
}
if ($PackageMode -eq 'artifacts' -and -not (Get-Command Get-ReleaseArtifacts -ErrorAction SilentlyContinue)) {
    throw ("`$PackageMode is 'artifacts', but this template defines no Get-ReleaseArtifacts. " +
           "Either set it to 'zip', or copy that function from the electron template - " +
           "artifacts mode needs it to decide which of the build's files are release assets.")
}

# Where artifacts land differs by mode, but either way it must be ignored.
$artifactDir = if ($PackageMode -eq 'zip') { $OutDir } else { Join-Path $RepoRoot (Resolve-BuilderOutput) }
Assert-ArtifactDirIgnored -Paths @($artifactDir) -Soft:($isDryRun -or -not $Tag)

# Found now, before tests and build, so they can be skipped. Empty means build.
$reuseAssets = @()
if ($mayReuse) {
    $reuseAssets = @(Find-ExistingAssets -ReleaseVersion $releaseVersion -ZipPath $zipPath `
                                         -ArtifactDir $artifactDir)
    if ($reuseAssets.Count -gt 0) {
        Write-Note "$releaseVersion is already built; -Ship copies that build without a rebuild"
    }
    else {
        Write-Note "no finished $releaseVersion build found; -Ship builds it first"
    }
}
$reusing = $reuseAssets.Count -gt 0

# Only a zip carries $ExtraPayload. In artifacts mode nothing in it ships, so
# requiring it to exist would only demand files the release never uses.
if ($PackageMode -eq 'zip') {
    # A reused zip already holds its payload; the files need not exist now.
    if (-not $reusing) { Assert-ExtraPayload }
}
elseif (@($ExtraPayload).Count -gt 0) {
    Write-Note "`$ExtraPayload ignored in artifacts mode ($(@($ExtraPayload).Count) entries)"
}

# Fatal only when a tag is being created. A plain local build names no release,
# so a changelog that is behind is worth a warning there and nothing more.
$releaseNotes = Get-ChangelogNotes -ReleaseVersion $releaseVersion -Soft:($isDryRun -or -not $Tag)

if ($Publish) {
    Assert-PublishReady -Soft:$isDryRun -ArtifactDir $artifactDir
}
else {
    Write-Note "remote checks skipped (no -Publish)"
}

if ($Ship) {
    Assert-NasReady -ReleaseVersion $releaseVersion -Soft:$isDryRun
}

# --- 3. tests --------------------------------------------------------------

Write-Step "Tests"
if ($reusing) {
    Write-Note "skipped: shipping the existing $releaseVersion build"
}
elseif ($SkipTests) {
    Write-Warn "skipped (-SkipTests). Do not do this for a real release."
}
else {
    Invoke-ProjectTests -WhatIf:$isDryRun
}

# --- 4. build --------------------------------------------------------------

Write-Step "Build"
if ($reusing) {
    Write-Note "skipped: shipping the existing $releaseVersion build"
}
else {
    $buildOutput = Invoke-ProjectBuild -ReleaseVersion $releaseVersion -WhatIf:$isDryRun
}

# --- 5/6. stage + package --------------------------------------------------

Write-Step "Packaging"

# Two shapes of release, chosen by PackageMode in release.settings.psd1.
#
#   'zip'       The build produced a directory of files. Stage it with
#               $ExtraPayload and compress the result into one archive.
#
#   'artifacts' The build already produced finished, individually shippable
#               files - an installer, say. Re-zipping those would only force
#               the user to unpack an installer before running it, so they are
#               uploaded as they are.
#
# Either way the step ends with $assets holding everything to attach to the
# release, each file paired with a .sha256 sidecar.

if ($reusing) {
    $assets = $reuseAssets
    foreach ($a in $assets) { Write-Note "existing: $(Split-Path -Leaf $a)" }
}
elseif ($PackageMode -eq 'zip') {
    if ($isDryRun) {
        Write-Note "would package $buildOutput"
        foreach ($e in $ExtraPayload) { Write-Note "  + $e" }
        Write-Note "would write $zipPath and $zipName.sha256"
    }
    else {
        if (-not (Test-Path -LiteralPath $OutDir)) {
            New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
        }

        $staging = New-StagingTree -BuildOutput $buildOutput
        try {
            New-ReleaseZip -SourceDir $staging -ZipPath $zipPath
        }
        finally {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        }

        New-ChecksumSidecar -FilePath $zipPath | Out-Null
    }

    $assets = @($zipPath, "$zipPath.sha256")
}
else {
    $artifacts = Get-ReleaseArtifacts -BuildOutput $buildOutput `
                                      -ReleaseVersion $releaseVersion -WhatIf:$isDryRun

    if ($isDryRun) {
        foreach ($a in $artifacts) { Write-Note "would upload $(Split-Path -Leaf $a) and its .sha256" }
        $assets = @($artifacts | ForEach-Object { $_; "$_.sha256" })
    }
    else {
        $assets = [System.Collections.Generic.List[string]]::new()
        foreach ($artifact in $artifacts) {
            New-ChecksumSidecar -FilePath $artifact | Out-Null
            $assets.Add($artifact)
            $assets.Add("$artifact.sha256")
        }
        $assets = $assets.ToArray()
    }
}

# --- 7. tag ----------------------------------------------------------------

if ($Tag) {
    Write-Step "Tagging"
    New-ReleaseTag -TagName $tagName -WhatIf:$isDryRun
}

# --- 8. publish ------------------------------------------------------------

if ($Publish) {
    Write-Step "Publishing"
    Publish-Release -TagName $tagName -Assets $assets -Notes $releaseNotes -WhatIf:$isDryRun
}

# --- ship (-Ship only) -----------------------------------------------------

# After publish, not before. If the copy fails here, the GitHub release is
# already complete, and `release.ps1 -Ship -Apply` copies the same build later
# without rebuilding it. Shipping first would leave a NAS folder that blocks
# the retry of a publish that failed.
if ($Ship) {
    Write-Step "Shipping to NAS"
    Copy-ReleaseToNas -ReleaseVersion $releaseVersion -Assets $assets -WhatIf:$isDryRun
}

# --- 9. done ---------------------------------------------------------------

Write-Host ""
Write-Step "Done"

if ($isDryRun) {
    Write-Warn "Dry run complete. Nothing was changed."
    Write-Warn "Add -Apply to the same command to run it for real."
}
elseif ($Publish) {
    Write-Note "Release $tagName is live with the zip attached."
    Write-Note "  gh release view $tagName --web"
}
elseif ($Tag) {
    $quoted = ($assets | ForEach-Object { "`"$_`"" }) -join ' '
    $notesFlag = '--generate-notes'
    if ($releaseNotes) {
        $notesPath = Write-ReleaseNotesFile -TagName $tagName -Notes $releaseNotes
        $notesFlag = "--notes-file `"$notesPath`""
    }
    Write-Note "Tag created locally. Push and publish when ready:"
    Write-Note "  git push origin $tagName"
    Write-Note "  gh release create $tagName $quoted $notesFlag"
    Write-Note "Or re-run with -Publish next time to do both."
}
elseif (-not $Ship) {
    Write-Note "Artifact built. Add -Tag to tag it, or -Publish to release it."
}

if ($Ship -and -not $isDryRun) {
    Write-Note "On the NAS at $(Get-NasReleaseDir -ReleaseVersion $releaseVersion)"
}
Write-Host ""
