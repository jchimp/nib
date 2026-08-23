using Nib.Highlight;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// Extension, then filename, then shebang. The ordering matters: the filename table
/// exists to catch extensionless files and disambiguate, never to override a
/// perfectly good extension.
/// </summary>
public class LanguageDetectorTests
{
    private static readonly LanguageDetector Detector = new(GrammarManifest.Load());

    [Theory]
    [InlineData("config.yml", "source.yaml")]
    [InlineData("config.yaml", "source.yaml")]
    [InlineData("pyproject.toml", "source.toml")]
    [InlineData("Program.cs.json", "source.json")]
    [InlineData("script.PS1", "source.powershell")]      // extensions match case-insensitively
    [InlineData("Nib.csproj", "text.xml")]
    [InlineData("README.md", "text.html.markdown")]
    [InlineData("setup.py", "source.python")]
    [InlineData("run.sh", "source.shell")]
    [InlineData("build.bat", "source.batchfile")]
    [InlineData("app.ts", "source.ts")]
    [InlineData("loader.mts", "source.ts")]
    [InlineData("Picker.tsx", "source.tsx")]
    [InlineData("bundle.js", "source.js")]              // .ts arriving did not steal .js
    public void Detects_by_extension(string name, string expected) =>
        Assert.Equal(expected, Detector.Detect(name, null));

    [Fact]
    public void Detects_by_filename_when_there_is_no_useful_extension() =>
        Assert.Equal("source.ini", Detector.Detect(@"C:\src\app\.npmrc", null));

    [Fact]
    public void A_dotfile_is_matched_as_a_whole_name() =>
        Assert.Equal("source.shell", Detector.Detect("/home/j/.bashrc", null));

    [Theory]
    [InlineData("#!/usr/bin/env python3", "source.python")]
    [InlineData("#!/bin/bash", "source.shell")]
    [InlineData("#!/usr/bin/env -S node --flag", "source.js")]
    public void Falls_back_to_the_shebang(string firstLine, string expected) =>
        Assert.Equal(expected, Detector.Detect("deploy", firstLine));

    [Fact]
    public void An_extension_beats_a_shebang() =>
        Assert.Equal("source.python", Detector.Detect("thing.py", "#!/bin/bash"));

    [Fact]
    public void Unknown_types_return_null_rather_than_guessing() =>
        Assert.Null(Detector.Detect("notes.xyz", "just some text"));

    [Fact]
    public void No_path_and_no_shebang_is_null() =>
        Assert.Null(Detector.Detect(null, null));

    [Fact]
    public void An_override_wins_over_everything() =>
        Assert.Equal("source.rust", Detector.Detect("thing.py", "#!/bin/bash", "source.rust"));

    /// <summary>
    /// The five YAML dependency grammars carry no extensions and must never be
    /// selected directly — only source.yaml dispatches to them.
    /// </summary>
    [Fact]
    public void Dependency_only_grammars_are_never_detection_targets()
    {
        foreach (string name in new[] { "a.yaml", "b.yml", "docker-compose.yml" })
            Assert.Equal("source.yaml", Detector.Detect(name, null));
    }
}
