using Dapper;
using Microsoft.Data.Sqlite;

namespace Glyphite.Host.Data;

/// <summary>Base class for SQLite-backed repositories, providing shared connection, write-lock, and disposal.</summary>
public abstract class RepositoryBase : IDisposable
{
    protected readonly SqliteConnection _conn;
    protected readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Cross-process collisions (SQLITE_BUSY/LOCKED) can outlive PRAGMA busy_timeout when another
    // process holds the write lock. Retry the whole action with exponential backoff — safe because
    // BUSY means nothing was written, and transactional callers start a fresh transaction on retry.
    private const int BusyRetryLimit = 5;
    private static readonly int[] BusyBackoffMs = [50, 100, 200, 400, 800];

    static RepositoryBase()
    {
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    protected RepositoryBase(string connectionString)
    {
        _connectionString = connectionString;
        _conn = new SqliteConnection(connectionString);
        _conn.Open();
        SetPragmas(_conn);
    }

    private static void SetPragmas(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            PRAGMA foreign_keys=ON;
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _conn?.Close();
        _conn?.Dispose();
    }

    protected async Task WithLockAsync(Func<Task> action)
    {
        await _writeLock.WaitAsync();
        try { await RetryOnBusyAsync(action); }
        finally { _writeLock.Release(); }
    }

    protected async Task<T> WithLockAsync<T>(Func<Task<T>> func)
    {
        await _writeLock.WaitAsync();
        try { return await RetryOnBusyAsync(func); }
        finally { _writeLock.Release(); }
    }

    private static async Task RetryOnBusyAsync(Func<Task> action)
        => await RetryOnBusyAsync(async () => { await action(); return true; });

    private static async Task<T> RetryOnBusyAsync<T>(Func<Task<T>> func)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await func(); }
            catch (SqliteException ex) when (IsBusy(ex) && attempt < BusyRetryLimit)
            {
                await Task.Delay(BusyBackoffMs[attempt]);
            }
        }
    }

    private static bool IsBusy(SqliteException ex) => ex.SqliteErrorCode is 5 or 6; // SQLITE_BUSY / SQLITE_LOCKED

    protected SqliteConnection CreateReadConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SetPragmas(conn);
        return conn;
    }
}
