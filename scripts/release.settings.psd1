# Settings for release.ps1 in this folder. This is the only release file a
# repo edits: release.ps1 is replaced whole when the template changes.
#
# Literal values only: strings in single quotes, $true or $false, and @()
# lists. No variables and no commands. Paths are repo-relative.
@{
    # Zip is named <ProjectName>-<version>.zip.
    ProjectName = 'nib'

    # Where the version lives. release.ps1 reads and writes it, and publishes
    # this project.
    VersionFile = 'src\Nib\Nib.csproj'

    # Where the release notes live. -Tag and -Publish stop unless its newest
    # version section matches VersionFile, and that section becomes the release
    # notes. Set to '' to turn this off and let GitHub generate the notes.
    ChangelogFile = 'CHANGELOG.md'

    # The NAS share -Ship copies releases to. Each version lands in its own
    # folder, <NasRoot>\<ProjectName>\<version>\. Use a UNC path: a mapped
    # drive letter is not visible from an elevated shell. '' turns -Ship off.
    NasRoot = ''

    # Test project. Set to '' to skip the test step entirely.
    TestProject = 'tests\Nib.Tests\Nib.Tests.csproj'

    # Where the publish output lands. Wiped and rewritten on every release, so
    # point it somewhere disposable - never at release/, and never at anything
    # you edit by hand.
    BuildDir = 'publish'

    # Extra files and directories folded into the zip alongside the build
    # output. A trailing slash means "recurse this directory"; it lands in the
    # zip under its own name. Every entry must exist or the release fails
    # before it builds.
    ExtraPayload = @(
        'README.md'
        'LICENSE'
        'CHANGELOG.md'
        # install.ps1 copies everything beside it except itself, so
        # uninstall.ps1 lands in the install folder, where the Settings > Apps
        # entry runs it from.
        'scripts/install.ps1'
        'scripts/uninstall.ps1'
    )

    # Runtime identifier and publish shape.
    Rid           = 'win-x64'
    SelfContained = $true

    # Extra msbuild properties for the publish. Common ones:
    #   -p:PublishSingleFile=true
    #   -p:PublishTrimmed=true          (breaks anything using reflection - check first)
    #   -p:EnableCompressionInSingleFile=true  (halves the zip, costs launch time)
    #
    # Not added, on purpose: PublishTrimmed (TextMateSharp resolves grammar
    # rule types reflectively; the trimmer strips them and it surfaces as a
    # null grammar at runtime, not a build error) and
    # EnableCompressionInSingleFile (decompresses on every launch; startup is
    # already 135 ms against a 100 ms goal).
    PublishProps = @(
        '-p:PublishSingleFile=true'
        # Symbols inside the assembly rather than beside it. Without this a
        # loose .pdb ships in the zip, and with DebugType=none a crash report
        # comes back with no line numbers at all.
        '-p:DebugType=embedded'
    )

    # How the release is packaged. 'zip' stages the build output plus
    # ExtraPayload into one archive.
    PackageMode = 'zip'
}
