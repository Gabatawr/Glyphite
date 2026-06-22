using Dapper;
using Microsoft.Data.Sqlite;

namespace Glyphite.Host.Data;

/// <summary>Base class for SQLite-backed repositories, providing shared connection, write-lock, and disposal.</summary>
public abstract class RepositoryBase : IDisposable
{
    protected readonly SqliteConnection _conn;
    protected readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

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
        try { await action(); }
        finally { _writeLock.Release(); }
    }

    protected async Task<T> WithLockAsync<T>(Func<Task<T>> func)
    {
        await _writeLock.WaitAsync();
        try { return await func(); }
        finally { _writeLock.Release(); }
    }

    protected SqliteConnection CreateReadConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        SetPragmas(conn);
        return conn;
    }
}
