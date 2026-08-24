using System.Diagnostics;
using System.Text.Json;

namespace Hedgerow.Publishing;

/// <summary>
/// Uploads a built site to object storage, skipping anything whose content
/// hash already matches what is up there.
/// </summary>
public sealed class Publisher : IAsyncDisposable
{
    private const int MaxParallelUploads = 8;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IObjectStore _store;
    private readonly SemaphoreSlim _gate = new(MaxParallelUploads);
    private readonly List<string> _uploaded = new();

    public Publisher(IObjectStore store, ILogger<Publisher>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Logger = logger;
    }

    public ILogger<Publisher>? Logger { get; }

    public int Skipped { get; private set; }

    public async Task<PublishResult> PublishAsync(
        DirectoryInfo root,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var files = root.EnumerateFiles("*", SearchOption.AllDirectories)
                        .Where(f => !f.Name.StartsWith('.'))
                        .ToArray();

        await Parallel.ForEachAsync(files, cancellationToken, async (file, token) =>
        {
            await _gate.WaitAsync(token);
            try
            {
                string key = Path.GetRelativePath(root.FullName, file.FullName)
                                 .Replace(Path.DirectorySeparatorChar, '/');
                string hash = await Hashing.OfFileAsync(file, token);

                if (await _store.MatchesAsync(key, hash, token))
                {
                    Skipped++;
                    return;
                }

                await _store.PutAsync(key, file, CacheControlFor(key), token);
                lock (_uploaded) _uploaded.Add(key);
                Logger?.LogInformation("uploaded {Key} ({Bytes} bytes)", key, file.Length);
            }
            finally
            {
                _gate.Release();
            }
        });

        return new PublishResult(_uploaded.Count, Skipped, stopwatch.Elapsed);
    }

    private static string CacheControlFor(string key) => key switch
    {
        var k when k.EndsWith(".html", StringComparison.Ordinal) => "public, max-age=300",
        var k when k.StartsWith("assets/", StringComparison.Ordinal)
            => "public, max-age=31536000, immutable",
        _ => "public, max-age=3600",
    };

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await _store.FlushAsync();
    }
}

public readonly record struct PublishResult(int Uploaded, int Skipped, TimeSpan Elapsed)
{
    public override string ToString() =>
        $"{Uploaded} uploaded, {Skipped} unchanged, in {Elapsed.TotalSeconds:F1}s";
}
