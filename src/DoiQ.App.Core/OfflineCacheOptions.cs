namespace DoiQ.App.Core;

public sealed class OfflineCacheOptions
{
    /// <summary>How long to wait for the server before falling back to the cache. Kept short so offline feels instant.</summary>
    public TimeSpan NetworkTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Entries kept per server; the oldest are pruned beyond this.</summary>
    public int MaxEntries { get; set; } = 2000;

    /// <summary>Prune after this many stores, so the directory is not listed on every response.</summary>
    public int PruneEvery { get; set; } = 50;
}
