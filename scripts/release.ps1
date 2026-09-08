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

    The csproj <Version> is the source of truth. Bump and commit it before
    releasing, which is what makes the clean-tree check worth having.

    Release publishes are self-contained even when the csproj is
    framework-dependent. That is deliberate: SelfContained in the csproj would
    make every `dotnet build` copy the whole runtime into bin/ and destroy the
    fast dev loop, so the release path sets it here instead. The cost is size -
    expect 70-90 MB against 5 MB framework-dependent. The benefit is that the
    zip runs on a machine with no .NET installed, which is the whole point of
    shipping it to anyone but yourself. Set $SelfContained to $false if the
    target machines are known to have the runtime.

.PARAMETER Version
    Set the release version. Rewrites the csproj <Version>, then builds. Omit
    to build at whatever the csproj already declares.

.PARAMETER Tag
    After a successful build, create the annotated git tag v<version>. Requires
    a clean working tree. Does not push.

.PARAMETER Publish
    Build, tag, push the tag, create the GitHub release, and upload the zip and
    its checksum as assets. Implies -Tag. Needs gh, authenticated.

.PARAMETER Apply
    Actually do the work. Without it the script writes, tags and publishes
    nothing.

.PARAMETER DryRun
    Accepted but redundant - a dry run is already the default.

.PARAMETER SkipTests
    Skip the test run. For iterating on this script, not for real releases.

.PARAMETER Force
    Replace an artifact that already exists for this version.

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
    .\scripts\release.ps1 -Version 1.2.0 -Publish
    Rehearse the full release and print every step it would take.

.EXAMPLE
    .\scripts\release.ps1 -Version 1.2.0 -Publish -Apply
    The one-command release.
#>

[CmdletBinding()]
param(
    # Set the release version. Rewrites the project's version file, then builds.
    # Omit to build at whatever version the project already declares.
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    # After a successful build, create the annotated git tag v<version>.
    [switch] $Tag,

    # Build, tag, push the tag, create the GitHub release, upload the assets.
    # Implies -Tag. Needs the gh CLI, authenticated.
    [switch] $Publish,

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

# ---- PROJECT CONFIG -------------------------------------------------------
# The only region you edit after copying this file into a new repo.

# Zip is named <ProjectName>-<version>.zip.
$ProjectName = 'nib'

# Where the version lives, repo-relative. Shown in messages; the seam
# functions below read and write it.
$VersionFile = 'src\Nib\Nib.csproj'

# Test project, repo-relative. Set to '' to skip the test step entirely.
$TestProject = 'tests\Nib.Tests\Nib.Tests.csproj'

# Where the publish output lands, repo-relative. Wiped and rewritten on every
# release, so point it somewhere disposable - never at release/, and never at
# anything you edit by hand.
$BuildDir = 'publish'

# Extra files and directories folded into the zip alongside the build output.
# A trailing slash means "recurse this directory"; it lands in the zip under
# its own name. Every entry must exist or the release fails before it builds.
$ExtraPayload = @(
    'README.md'
    'LICENSE'
    'scripts/install.ps1'
    # 'config/'
)

# Runtime identifier and publish shape.
$Rid           = 'win-x64'
$SelfContained = $true

# Extra msbuild properties for the publish. Common ones:
#   -p:PublishSingleFile=true
#   -p:PublishTrimmed=true          (breaks anything using reflection - check first)
#   -p:EnableCompressionInSingleFile=true  (halves the zip, costs launch time)
$PublishProps = @(
    '-p:PublishSingleFile=true'
    # Symbols inside the assembly rather than beside it. Without this a loose
    # .pdb ships in the zip, and with DebugType=none a crash report comes back
    # with no line numbers at all.
    '-p:DebugType=embedded'
)

# How the release is packaged. 'zip' stages the build output plus
# $ExtraPayload into one archive. See the electron template for the
# 'artifacts' alternative, where the build already emits shippable files.
$PackageMode = 'zip'
# ---------------------------------------------------------------------------

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
        $rel = $path
        if ([System.IO.Path]::IsPathRooted($rel) -and
            $rel.StartsWith($RepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            $rel = $rel.Substring($RepoRoot.Length).TrimStart('\', '/')
        }
        if (-not $rel) { continue }

        # Exit 0 and echoes the path when ignored; exit 1 and silent when not,
        # which Get-GitOutput reports as $null.
        if (Get-GitOutput @('check-ignore', $rel)) { continue }

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
    param([Parameter(Mandatory)][bool]$Soft)

    Resolve-ReleaseBranch

    $problems = [System.Collections.Generic.List[string]]::new()

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

        [Parameter(Mandatory)][bool]$WhatIf
    )

    if ($WhatIf) {
        Write-Note "would push $TagName and create the GitHub release"
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
    Invoke-Native -What "git push origin $TagName" -Command {
        git -C $RepoRoot push origin $TagName
    } | Out-Null

    Write-Note "creating GitHub release"
    # gh resolves the repo from the git remote of its working directory, so run
    # it there. (--repo takes OWNER/REPO, never a filesystem path.)
    Push-Location $RepoRoot
    try {
        $exit = Invoke-Native -What 'gh release create' -AllowFailure -Command {
            gh release create $TagName @Assets --title $TagName --generate-notes
        }
    }
    finally { Pop-Location }

    if ($exit -ne 0) {
        throw ("gh release create failed with exit code $exit. The tag is pushed, so " +
               "fix the cause and finish with:`n" +
               "  gh release create $TagName " + ($Assets -join ' ') + " --generate-notes")
    }
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
        throw "No <Version> element in $VersionFile. Add one, or pass -Version explicitly."
    }

    $value = $node.InnerText.Trim()
    if ($value -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
        throw "<Version> in $VersionFile is '$value', which is not x.y.z. Fix it, or pass -Version explicitly."
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

    # -p:Version on the command line rather than relying on the csproj, so a
    # -Version override on a dry run still stamps the assembly it describes.
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

if ($isDryRun) {
    Write-Host ""
    Write-Warn "DRY RUN - nothing will be built, written, tagged or published."
    Write-Warn "Re-run the same command with -Apply to do it for real."
}

# --- 1. version ------------------------------------------------------------

Write-Step "Version"
if ($Version) {
    Set-ProjectVersion -NewVersion $Version -WhatIf:$isDryRun
    # On a dry run the file on disk is unchanged, so use the requested version
    # rather than reading back what is still the old one.
    $releaseVersion = $Version
}
else {
    $releaseVersion = Get-ProjectVersion
    Write-Note "$releaseVersion (from $VersionFile)"
}

$tagName = "v$releaseVersion"
$zipName = "$ProjectName-$releaseVersion.zip"
$zipPath = Join-Path $OutDir $zipName

if ($PackageMode -eq 'zip') {
    # A version already in the artifact directory has been built, and probably
    # shipped. Rebuilding it under the same number produces a second artifact
    # with the same name and a different hash, which is the one thing a release
    # number exists to prevent.
    if ((Test-Path -LiteralPath $zipPath) -and -not $Force) {
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

Assert-ExtraPayload
if ($Publish) {
    Assert-PublishReady -Soft:$isDryRun
}
else {
    Write-Note "remote checks skipped (no -Publish)"
}

# --- 3. tests --------------------------------------------------------------

Write-Step "Tests"
if ($SkipTests) {
    Write-Warn "skipped (-SkipTests). Do not do this for a real release."
}
else {
    Invoke-ProjectTests -WhatIf:$isDryRun
}

# --- 4. build --------------------------------------------------------------

Write-Step "Build"
$buildOutput = Invoke-ProjectBuild -ReleaseVersion $releaseVersion -WhatIf:$isDryRun

# --- 5/6. stage + package --------------------------------------------------

Write-Step "Packaging"

# Two shapes of release, chosen by $PackageMode in the config block.
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

if ($PackageMode -eq 'zip') {
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
    Publish-Release -TagName $tagName -Assets $assets -WhatIf:$isDryRun
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
    Write-Note "Tag created locally. Push and publish when ready:"
    Write-Note "  git push origin $tagName"
    Write-Note "  gh release create $tagName $quoted --generate-notes"
    Write-Note "Or re-run with -Publish next time to do both."
}
else {
    Write-Note "Artifact built. Add -Tag to tag it, or -Publish to release it."
}
Write-Host ""
