using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Codery.Village.Server.Desk;

/// <summary>Where the desk keeps its errands between two runs: one per wish.</summary>
public interface IErrandStore
{
    /// <summary>Every errand, one per wish, in no particular order.</summary>
    Task<IReadOnlyList<ErrandRecord>> All(CancellationToken cancellation);

    /// <summary>Keeps <paramref name="errand"/> as the errand of its wish, in place of the one there was.</summary>
    Task Save(ErrandRecord errand, CancellationToken cancellation);

    /// <summary>
    /// The desk's round, for one server at a time: during a deploy the old and the new server run side by side. Null
    /// while another holds it; disposing the lock lets go of it.
    /// </summary>
    Task<IAsyncDisposable?> TryLockRound(CancellationToken cancellation);
}

/// <summary>The errands in memory: for a village without a database, and for the tests.</summary>
public sealed class MemoryErrandStore : IErrandStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ErrandRecord> _errands = [];

    public Task<IReadOnlyList<ErrandRecord>> All(CancellationToken cancellation)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<ErrandRecord>>([.. _errands.Values]);
    }

    public Task Save(ErrandRecord errand, CancellationToken cancellation)
    {
        lock (_gate) _errands[errand.Wish] = errand;
        return Task.CompletedTask;
    }

    /// <summary>Always granted: without a database there is no other server that shares the errands.</summary>
    public Task<IAsyncDisposable?> TryLockRound(CancellationToken cancellation) => Task.FromResult<IAsyncDisposable?>(new Granted());

    private sealed class Granted : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// The errands in Postgres, in the table <c>errands</c> that <see cref="Store.PostgresVillageStore.Prepare"/> makes;
/// the history is a JSON column.
/// </summary>
public sealed class PostgresErrandStore(string connectionString) : IErrandStore, IAsyncDisposable, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // the advisory lock of the desk's round: one fixed number for the whole database ("errand" in ASCII)
    private const long RoundLock = 0x65_72_72_61_6E_64;

    private readonly NpgsqlDataSource _source = NpgsqlDataSource.Create(connectionString);

    public async Task<IReadOnlyList<ErrandRecord>> All(CancellationToken cancellation)
    {
        await using var command = _source.CreateCommand(
            "SELECT wish, issue, command, state, since, failures, history::text, owner_mentioned, reappeared, baseline FROM errands");
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        var errands = new List<ErrandRecord>();
        while (await reader.ReadAsync(cancellation))
            errands.Add(new ErrandRecord(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2),
                Enum.Parse<ErrandState>(reader.GetString(3)),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetInt32(5),
                JsonSerializer.Deserialize<List<Failure>>(reader.GetString(6), Json) ?? [],
                reader.GetBoolean(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetInt64(9)));
        return errands;
    }

    public async Task Save(ErrandRecord errand, CancellationToken cancellation)
    {
        await using var command = _source.CreateCommand("""
            INSERT INTO errands (wish, issue, command, state, since, failures, history, owner_mentioned, reappeared, baseline)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            ON CONFLICT (wish) DO UPDATE SET issue = EXCLUDED.issue, command = EXCLUDED.command, state = EXCLUDED.state,
                since = EXCLUDED.since, failures = EXCLUDED.failures, history = EXCLUDED.history,
                owner_mentioned = EXCLUDED.owner_mentioned, reappeared = EXCLUDED.reappeared, baseline = EXCLUDED.baseline
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = errand.Wish });
        command.Parameters.Add(new NpgsqlParameter { Value = errand.Issue });
        command.Parameters.Add(new NpgsqlParameter { Value = errand.Command });
        command.Parameters.Add(new NpgsqlParameter { Value = errand.State.ToString() });
        // Postgres takes a moment in UTC only
        command.Parameters.Add(new NpgsqlParameter { Value = errand.Since.ToUniversalTime() });
        command.Parameters.Add(new NpgsqlParameter { Value = errand.Failures });
        command.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(errand.History, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
        command.Parameters.Add(new NpgsqlParameter { Value = errand.OwnerMentioned });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)errand.Reappeared?.ToUniversalTime() ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.TimestampTz,
        });
        command.Parameters.Add(new NpgsqlParameter { Value = errand.Baseline });
        await command.ExecuteNonQueryAsync(cancellation);
    }

    /// <summary>
    /// A session advisory lock on a connection of its own, held until disposed. When the server dies, Postgres closes the
    /// connection and the lock goes with it.
    /// </summary>
    public async Task<IAsyncDisposable?> TryLockRound(CancellationToken cancellation)
    {
        var connection = await _source.OpenConnectionAsync(cancellation);
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection) { Parameters = { new() { Value = RoundLock } } };
            if (await command.ExecuteScalarAsync(cancellation) is true) return new HeldRound(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        await connection.DisposeAsync();
        return null;
    }

    private sealed class HeldRound(NpgsqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection) { Parameters = { new() { Value = RoundLock } } };
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            catch
            {
                // a session that may still hold the lock never goes back to the pool
                NpgsqlConnection.ClearPool(connection);
                throw;
            }
            finally
            {
                // closing the session lets go of the lock as well, should the unlock fail
                await connection.DisposeAsync();
            }
        }
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();

    public void Dispose() => _source.Dispose();
}
