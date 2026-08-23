using System.Text;
using Xunit;

namespace Nib.Tests;

/// <summary>
/// The PowerShell tooling under <c>scripts/</c> stays pure ASCII.
///
/// Not a style rule. None of those files carry a BOM, so Windows PowerShell 5.1
/// decodes them as CP-1252 rather than UTF-8. A UTF-8 em dash is three bytes, and
/// the last of them lands on an ASCII double quote in that code page — which
/// terminates a string literal in the middle of a line and silently swallows
/// everything after it. That is not a parse error: the script still runs, one
/// function absorbs the next, and the caller quietly executes the wrong body.
/// It cost an afternoon in install.ps1, where an em dash inside a Write-Host string
/// made Add-ToUserPath run Remove-FromUserPath's code.
///
/// Enforced here rather than by adding BOMs because ASCII is the invariant that
/// holds no matter which shell, editor or git filter touches the file next.
/// release.ps1 runs the test suite, so a regression cannot reach a zip.
/// </summary>
public class ScriptEncodingTests
{
    private const string ScriptFolder = "scripts";

    private static string ScriptsDirectory()
    {
        // Anchor on the repo root, not on the folder being looked for. Walking up
        // for a bare folder name climbs out of the repo the moment it is missing:
        // when this folder was called tools/ and got renamed, the walk reached
        // C:\tools and started ASCII-linting unrelated personal scripts, reporting
        // them as failures of this project. Missing scripts/ is now a clear failure
        // at the repo root instead of a search that succeeds somewhere else.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !IsRepoRoot(dir))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string scripts = Path.Combine(dir!.FullName, ScriptFolder);
        Assert.True(Directory.Exists(scripts), $"no {ScriptFolder}/ under the repo root {dir.FullName}");
        return scripts;
    }

    private static bool IsRepoRoot(DirectoryInfo dir) =>
        File.Exists(Path.Combine(dir.FullName, "src", "Nib", "Nib.csproj"));

    [Fact]
    public void PowerShellScriptsAreAscii()
    {
        string dir = ScriptsDirectory();
        string[] scripts = Directory.GetFiles(dir, "*.ps1", SearchOption.AllDirectories);
        Assert.NotEmpty(scripts);

        var offenders = new List<string>();

        foreach (string script in scripts)
        {
            string[] lines = File.ReadAllLines(script, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (char c in lines[i])
                {
                    if (c > 127)
                    {
                        offenders.Add(
                            $"{Path.GetFileName(script)}:{i + 1} contains U+{(int)c:X4} '{c}'");
                        break;
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Non-ASCII characters in PowerShell tooling (see this test's summary for why "
            + "that is a correctness bug, not a style nit):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
