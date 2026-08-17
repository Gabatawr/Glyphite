using Dapper;
using Glyphite.Host.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Glyphite.Tests.Unit.Data;

public class RepositoryBaseTests : IDisposable
{
    private readonly List<string> _dbPaths = [];

    public void Dispose()
    {
        foreach (var dbPath in _dbPaths)
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                try { if (File.Exists(f)) File.Delete(f); }
                catch { /* best-effort cleanup */ }
            }
    }

    private BusyFakeRepository NewRepo(int failAttempts, int failCode = 5)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"glyphite_test_{Guid.NewGuid():N}.db");
        _dbPaths.Add(dbPath);
        return new BusyFakeRepository(dbPath, failAttempts, failCode);
    }
// ── Busy retry ──

    [Fact]
    public async Task Write_RetriesOnBusy_ThenSucceeds()
    {
        using var repo = NewRepo(failAttempts: 2);
        var result = await repo.WriteOnceAsync();
        Assert.Equal("ok", result);
        Assert.Equal(3, repo.Attempts); // initial + 2 retries
    }

    [Fact]
    public async Task Write_ExhaustsRetries_ThenRethrowsBusy()
    {
        using var repo = NewRepo(failAttempts: int.MaxValue);
        var ex = await Assert.ThrowsAsync<SqliteException>(() => repo.WriteOnceAsync());
        Assert.Equal(5, ex.SqliteErrorCode);
        Assert.Equal(6, repo.Attempts); // initial + 5 retries
    }

    [Fact]
    public async Task Write_DoesNotRetry_NonBusyError()
    {
        using var repo = NewRepo(failAttempts: 1, failCode: 1); // SQLITE_ERROR
        var ex = await Assert.ThrowsAsync<SqliteException>(() => repo.WriteOnceAsync());
        Assert.Equal(1, ex.SqliteErrorCode);
        Assert.Equal(1, repo.Attempts); // non-busy errors propagate immediately
    }

    [Fact]
    public async Task TransactionalWrite_RetriesOnBusy_CommitsExactlyOnce()
    {
        using var repo = NewRepo(failAttempts: 1);
        await repo.TransactionalWriteOnceAsync();
        Assert.Equal(2, repo.Attempts);
        Assert.Equal(1, await repo.CountRowsAsync()); // no duplicate rows from the failed attempt
    }

    private sealed class BusyFakeRepository : RepositoryBase
    {
        private readonly int _failAttempts;
        private readonly int _failCode;

        public BusyFakeRepository(string dbPath, int failAttempts, int failCode = 5)
            : base($"Data Source={dbPath}")
        {
            _failAttempts = failAttempts;
            _failCode = failCode;
        }

        public int Attempts { get; private set; }

        public async Task<string> WriteOnceAsync() => await WithLockAsync(async () =>
        {
            Attempts++;
            if (Attempts <= _failAttempts)
                throw new SqliteException("database is locked", _failCode);
            await _conn.ExecuteAsync(
                "CREATE TABLE IF NOT EXISTS t (x INTEGER); INSERT INTO t (x) VALUES (1);");
            return "ok";
        });

        public async Task TransactionalWriteOnceAsync() => await WithLockAsync(async () =>
        {
            Attempts++;
            if (Attempts <= _failAttempts)
                throw new SqliteException("database is locked", _failCode);
            await using var tx = await _conn.BeginTransactionAsync();
            await _conn.ExecuteAsync(
                "CREATE TABLE IF NOT EXISTS t (x INTEGER); INSERT INTO t (x) VALUES (1);", transaction: tx);
            await tx.CommitAsync();
        });

        public async Task<int> CountRowsAsync()
        {
            using var conn = CreateReadConnection();
            return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM t");
        }
    }
}
