using Glyphite.Host.Data;

namespace Glyphite.Tests.Unit.Support;

/// <summary>
/// Shared temp SQLite database for tests. Creates a unique db file with
/// session + block repositories; deletes the file (and wal/shm sidecars) on dispose.
/// </summary>
public sealed class TestDb : IDisposable
{
    public string DbPath { get; }
    public string ConnStr { get; }
    public SessionRepository Sessions { get; }
    public BlockRepository Blocks { get; }

    public TestDb()
    {
        DbPath = Path.Combine(Path.GetTempPath(), $"glyphite_test_{Guid.NewGuid():N}.db");
        ConnStr = $"Data Source={DbPath}";
        Sessions = new SessionRepository(ConnStr);
        Blocks = new BlockRepository(ConnStr);
        // Init remaining tables (config, kv) so session deletion can cascade into them
        using var config = new ConfigRepository(ConnStr);
        using var kv = new KVStoreRepository(ConnStr);
    }

    public void Dispose()
    {
        Blocks.Dispose();
        Sessions.Dispose();
        foreach (var f in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" })
        {
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort cleanup */ }
        }
    }
}
