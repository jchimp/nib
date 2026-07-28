using System.Diagnostics;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace Nib.Highlight;

/// <summary>
/// <c>nib --soak [dir] [passes]</c>. Not part of the editor.
///
/// TextMateSharp reaches Oniguruma through the native Onigwrap DLL, and there is a
/// historical heap-corruption report for that combination under
/// <c>PublishSingleFile</c> (dotnet/runtime#65443), where the native library is
/// extracted to <c>%TEMP%\.net\nib\&lt;hash&gt;</c> on first launch. Old, probably
/// fixed — but "probably" is not a foundation to build a phase on, and a heap
/// corruption found later would be indistinguishable from a bug in our own
/// tokenizer cache.
///
/// So: load every grammar, tokenize a few hundred real files repeatedly, and see
/// whether the process survives. Run it against the *published* single-file exe,
/// not bin/Debug — the extraction is the thing under test.
/// </summary>
internal static class Soak
{
    public static int Run(string? directory, int passes)
    {
        string root = directory ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"nib: --soak: no such directory: {root}");
            return 1;
        }

        GrammarManifest manifest;
        try
        {
            manifest = GrammarManifest.Load();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"nib: --soak: manifest: {ex.Message}");
            return 1;
        }

        var store = new GrammarStore(manifest);
        var registry = new Registry(store);
        var detector = new LanguageDetector(manifest);
        var timeout = TimeSpan.FromMilliseconds(50);

        // Force every grammar through the loader once, including the ones that only
        // exist as dependencies. This is where a bad resource or an unresolved
        // include shows up as a hard failure instead of silently-no-colors.
        Console.WriteLine($"Loading grammars for {manifest.ThemeIds.Count} themes...");
        int loaded = 0, missing = 0;
        foreach (string themeId in manifest.ThemeIds)
        {
            if (store.GetTheme(themeId) is null) { Console.Error.WriteLine($"  MISSING theme {themeId}"); missing++; }
        }

        var scopes = new List<string>();
        foreach (string path in Files(root))
        {
            if (detector.Detect(path, null) is { } scope && !scopes.Contains(scope)) scopes.Add(scope);
        }
        // Every scope in the manifest, not just the ones the corpus happens to use:
        // a grammar that fails to compile must fail here, by name, and not as an
        // anonymous exception buried in a per-file loop.
        foreach (string scope in manifest.AllScopes)
        {
            IGrammar? g = registry.LoadGrammar(scope);
            if (g is null) { Console.Error.WriteLine($"  MISSING grammar {scope}"); missing++; continue; }

            // Compilation is lazy — the root rule is only built on first tokenize —
            // so loading alone proves nothing.
            try { g.TokenizeLine("x", null, TimeSpan.FromMilliseconds(50)); loaded++; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  BROKEN  {scope}: {ex.GetType().Name}: {ex.Message}");
                missing++;
            }
        }
        Console.WriteLine($"  {loaded} grammars loaded, {missing} missing");

        string[] files = Files(root).Where(f => detector.Detect(f, null) is not null).ToArray();
        if (files.Length == 0)
        {
            Console.Error.WriteLine($"nib: --soak: no highlightable files under {root}");
            return 1;
        }

        Console.WriteLine($"Soaking {files.Length} files x {passes} passes...");
        var sw = Stopwatch.StartNew();
        long lines = 0, tokens = 0, errors = 0, timeouts = 0;

        for (int pass = 0; pass < passes; pass++)
        {
            foreach (string path in files)
            {
                string scope = detector.Detect(path, null)!;
                IGrammar? grammar = registry.LoadGrammar(scope);
                if (grammar is null) { errors++; continue; }

                string[] text;
                try { text = File.ReadAllLines(path); }
                catch (IOException) { continue; }

                IStateStack? state = null;
                foreach (string line in text)
                {
                    try
                    {
                        ITokenizeLineResult result = grammar.TokenizeLine(line, state, timeout);
                        state = result.RuleStack;
                        tokens += result.Tokens.Length;
                        // A stopped tokenize returns the state it had; count it so a
                        // pathological file is visible rather than merely slow.
                        if (result.Tokens.Length == 0 && line.Length > 0) timeouts++;
                        lines++;
                    }
                    catch (Exception ex)
                    {
                        // Full stack for the first one only; after that the count is
                        // the signal and the trace is just noise.
                        if (errors == 0) Console.Error.WriteLine(ex.ToString());
                        else if (errors < 5) Console.Error.WriteLine($"  {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
                        errors++;
                        state = null;
                    }
                }
            }

            // Force collection between passes: a native heap corruption most often
            // surfaces when managed finalizers run over the wrapper objects.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Console.WriteLine($"  pass {pass + 1}/{passes}: {lines:N0} lines, {tokens:N0} tokens, {errors} errors");
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"{lines:N0} lines, {tokens:N0} tokens in {sw.Elapsed.TotalSeconds:N1}s");
        Console.WriteLine($"peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MB");
        Console.WriteLine($"errors {errors}, empty-token lines {timeouts}");
        return errors > 0 || missing > 0 ? 1 : 0;
    }

    private static IEnumerable<string> Files(string root)
    {
        try { return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
        catch (UnauthorizedAccessException) { return []; }
    }
}
