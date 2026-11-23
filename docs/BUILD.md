# Building Nib

Nib is a single C# console project targeting **.NET 10** (`src/Nib/Nib.csproj`).
There is no solution file — build the project directly.

## Prerequisites

- **.NET 10 SDK** (not just the runtime). Check with:

  ```powershell
  dotnet --list-sdks
  ```

  If that prints nothing — or `dotnet build` fails with *"No .NET SDKs were
  found"* — you have only the runtime installed. A runtime runs finished apps;
  building needs the SDK. Download it from <https://dotnet.microsoft.com/download>.

- The SDK's major version must cover the project's `TargetFramework`. Nib targets
  `net10.0`, so a 10.x SDK is required. **`net8.0` also works** if you don't have
  the .NET 10 SDK: change the one `<TargetFramework>` line in
  [src/Nib/Nib.csproj](src/Nib/Nib.csproj) to `net8.0`.

- Windows only. Nib is built on the Windows console host (`CONIN$`/`CONOUT$`,
  `ReadConsoleInputW`, VT sequences) and will not run on Linux/macOS.

## Common commands

Run these from the repo root.

```powershell
# Compile (Debug)
dotnet build src/Nib/Nib.csproj

# Compile and run the editor
dotnet run --project src/Nib/Nib.csproj -- [file]
```

`dotnet run` pays 2–4 s of build orchestration on every launch. For anything where
startup speed matters, build once and run the exe directly:

```powershell
dotnet build src/Nib/Nib.csproj
./src/Nib/bin/Debug/net10.0/win-x64/nib.exe [file]
```

`nib --probe` runs the phase-1 terminal probe instead of the editor: it puts the
terminal into raw mode and checks Ctrl+C interception, resize events, and clipboard
round-trip. `nib --soak [dir] [passes]` runs the tokenizer over a directory tree
without touching the console — useful after a TextMateSharp or .NET upgrade.

## Development build (single-file exe)

```powershell
dotnet publish src/Nib/Nib.csproj -c Release
# -> src/Nib/bin/Release/net10.0/win-x64/publish/nib.exe
```

Publish settings live in the csproj: **framework-dependent** (needs the .NET
runtime installed), single-file, ReadyToRun, `win-x64`.
`IncludeNativeLibrariesForSelfExtract` bundles TextMateSharp's native Oniguruma
wrapper into the exe; first launch extracts it to `%TEMP%\.net\nib\<hash>`.

This build is for you, on this machine. To hand nib to anyone else, use the
release script below — a framework-dependent exe fails at launch on a box without
the .NET runtime, with an error that explains nothing.

## Releasing

Full flow, refusal messages and recovery: [RELEASE.md](RELEASE.md). Short version:

```powershell
# As you work: add notes under "## Unreleased" in CHANGELOG.md.
./scripts/release.ps1 -Version 0.9.0 -Apply   # bump the csproj, roll the CHANGELOG
git commit -am "Release 0.9.0."
git push origin HEAD
./scripts/release.ps1 -Publish                # rehearse
./scripts/release.ps1 -Publish -Apply         # do it
```

Nothing is written, tagged or pushed without `-Apply`.

**The version comes from `<Version>` in [src/Nib/Nib.csproj](../src/Nib/Nib.csproj).**
`-Version` is the only thing that writes it, and it cannot be combined with `-Tag`
or `-Publish`: the bump has to be committed before the tag. `-p:Version=` is also
passed to the publish, so the assembly (and `nib --version`) carries the version the
release is named after.

**The CHANGELOG owns the notes.** `-Tag` and `-Publish` stop unless the newest
version section in [CHANGELOG.md](../CHANGELOG.md) matches the csproj, and that
section becomes the GitHub release notes.

The output is `release/nib-<version>.zip` plus a `.sha256` sidecar. The zip holds
`nib.exe`, `install.ps1`, `uninstall.ps1`, `README.md`, `LICENSE` and
`CHANGELOG.md`. `release/` and `publish/` are gitignored.

`scripts/release.ps1` is a shared template and stays byte-identical to it. Everything
nib-specific lives in [scripts/release.settings.psd1](../scripts/release.settings.psd1):
project paths, the extra files in the zip, publish properties. Edit that, not the
script.

**Run it from PowerShell 7 (`pwsh`).** Windows PowerShell 5.1 started from a
PowerShell 7 window inherits 7's module path and fails with *"The term
'Import-PowerShellDataFile' is not recognized"*. The template does not guard against
that yet; `install.ps1` and `uninstall.ps1` do.

**Release publishes are self-contained; ordinary builds are not.** `SelfContained`
stays `false` in the csproj because setting it there makes every `dotnet build`
copy the whole runtime into `bin/`, which wrecks the dev loop. `release.ps1` passes
it on the publish command line instead. The cost is size, about **70-90 MB** against
~5 MB framework-dependent. The benefit is that the zip runs on a machine with no
.NET installed.

Two settings are deliberately absent, and adding them will cost you an afternoon:

- **`PublishTrimmed`**: TextMateSharp resolves grammar rule types reflectively.
  The trimmer strips them and the failure surfaces much later as a null grammar,
  not as a build error.
- **`EnableCompressionInSingleFile`**: roughly halves the zip, but decompresses on
  *every* launch, and startup is already 135 ms against a sub-100 ms goal.

The template has no smoke test of the published exe. The old nib-only script ran
`nib --version` on the staged exe; it is kept in `scripts/old/` for reference.

## Installing a release

From the unzipped release folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -DryRun   # see what it will do
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

Installs to `%LOCALAPPDATA%\Programs\nib` (no admin rights), appends that folder to
the user PATH, and adds a Settings > Apps entry for the current user. Running it
again upgrades in place. It refuses to run while nib is running from the install
folder, because the copy would fail halfway and closing an editor for you could
lose unsaved text. `-NoPath` skips PATH.

To remove it: Settings > Apps > nib, or `uninstall.ps1` in the install folder. That
removes the folder, the PATH entry and the uninstall entry, and keeps
`%APPDATA%\nib\config.toml`.

Both scripts write PATH through the registry with the value kind preserved. See
"PowerShell landmines" in [CLAUDE.md](../CLAUDE.md) before you change that code.

## Tests

```powershell
dotnet test tests/Nib.Tests/Nib.Tests.csproj
```

## Grammars (not part of the build)

The vendored TextMate grammars under `src/Nib/Resources/` are committed as `.gz`
files and are **not** regenerated by a normal build. Refresh them only when
needed:

```powershell
./scripts/fetch-grammars.ps1 -Force
```

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `No .NET SDKs were found` | Only the runtime is installed | Install the .NET SDK |
| `NETSDK1045: The current .NET SDK does not support targeting net10.0` | SDK older than the target framework | Install the .NET 10 SDK, or retarget to `net8.0` |
| `NU1100: Unable to resolve 'Microsoft.NETCore.App.Runtime.win-x64 ...'` | No NuGet package source configured (`dotnet nuget list source` prints "No sources found") | Add nuget.org: `dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org` |
| Build fails on warnings | `TreatWarningsAsErrors` is on | Fix the warning; the build intends zero warnings |
