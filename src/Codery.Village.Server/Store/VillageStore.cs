using Codery.Village.Core.Save;
using Npgsql;
using NpgsqlTypes;

namespace Codery.Village.Server.Store;

/// <summary>The village that was begun last, with its latest save; the save is null when it has none.</summary>
public sealed record StoredVillage(long Id, ulong Seed, SavedVillage? Save);

/// <summary>
/// Where the village is kept between two runs. A new village is a new one next to the old ones: nothing is
/// overwritten, and only saves older than the latest few of a village are let go.
/// </summary>
public interface IVillageStore
{
    /// <summary>Makes what the store needs when it is not there yet.</summary>
    Task Prepare(CancellationToken cancellation);

    /// <summary>The village begun last. Throws <see cref="SaveException"/> when its latest save cannot be read.</summary>
    Task<StoredVillage?> Latest(CancellationToken cancellation);

    /// <summary>Begins a new village with its first save, and gives its id.</summary>
    Task<long> Begin(ulong seed, SavedVillage first, CancellationToken cancellation);

    /// <summary>Writes a save and the lines of its log together: both or neither. A line that is there already stays as it is.</summary>
    Task Save(long village, SavedVillage save, CancellationToken cancellation);

    /// <summary>What the conversations cost on a Dutch calendar day, in dollars; 0 for a day without any.</summary>
    Task<decimal> TalkSpent(DateOnly day, CancellationToken cancellation);

    /// <summary>Keeps what the conversations of a day cost; what is kept never goes down.</summary>
    Task SaveTalkSpent(DateOnly day, decimal spent, CancellationToken cancellation);
}

/// <summary>The village in Postgres: four tables, and the desk's <c>errands</c>, made by the store itself when they are not there.</summary>
public sealed class PostgresVillageStore(string connectionString) : IVillageStore, IAsyncDisposable, IDisposable
{
    /// <summary>How many saves of a village are kept: the latest.</summary>
    public const int KeptSaves = 10;

    private readonly NpgsqlDataSource _source = NpgsqlDataSource.Create(connectionString);

    public async Task Prepare(CancellationToken cancellation)
    {
        await using var command = _source.CreateCommand("""
            CREATE TABLE IF NOT EXISTS village (
                id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                seed    numeric(20) NOT NULL,
                started timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS village_save (
                id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                village bigint NOT NULL REFERENCES village (id),
                tick    bigint NOT NULL,
                format  integer NOT NULL,
                saved   timestamptz NOT NULL DEFAULT now(),
                state   jsonb NOT NULL
            );
            CREATE INDEX IF NOT EXISTS village_save_village ON village_save (village, id);
            CREATE TABLE IF NOT EXISTS village_log (
                village bigint NOT NULL REFERENCES village (id),
                seq     bigint NOT NULL,
                tick    bigint NOT NULL,
                kind    text NOT NULL,
                subject text NOT NULL,
                detail  text,
                PRIMARY KEY (village, seq)
            );
            CREATE TABLE IF NOT EXISTS talk_budget (
                day   date PRIMARY KEY,
                spent numeric NOT NULL
            );
            CREATE TABLE IF NOT EXISTS errands (
                wish            text PRIMARY KEY,
                issue           integer NOT NULL,
                command         bigint NOT NULL,
                state           text NOT NULL,
                since           timestamptz NOT NULL,
                failures        integer NOT NULL,
                history         jsonb NOT NULL,
                owner_mentioned boolean NOT NULL,
                reappeared      timestamptz,
                baseline        bigint NOT NULL DEFAULT 0
            );
            """);
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public async Task<StoredVillage?> Latest(CancellationToken cancellation)
    {
        await using var command = _source.CreateCommand("""
            SELECT v.id, v.seed, s.state::text
            FROM village v
            LEFT JOIN LATERAL (SELECT state FROM village_save WHERE village = v.id ORDER BY id DESC LIMIT 1) s ON true
            ORDER BY v.id DESC
            LIMIT 1
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        if (!await reader.ReadAsync(cancellation)) return null;
        return new StoredVillage(
            reader.GetInt64(0),
            (ulong)reader.GetDecimal(1),
            reader.IsDBNull(2) ? null : SaveJson.FromJson(reader.GetString(2)));
    }

    public async Task<long> Begin(ulong seed, SavedVillage first, CancellationToken cancellation)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        await using var command = new NpgsqlCommand("INSERT INTO village (seed) VALUES ($1) RETURNING id", connection, transaction)
        {
            Parameters = { new() { Value = (decimal)seed } },
        };
        var village = (long)(await command.ExecuteScalarAsync(cancellation))!;
        await Write(connection, transaction, village, first, cancellation);
        await transaction.CommitAsync(cancellation);
        return village;
    }

    public async Task Save(long village, SavedVillage save, CancellationToken cancellation)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);
        await Write(connection, transaction, village, save, cancellation);
        await transaction.CommitAsync(cancellation);
    }

    private static async Task Write(NpgsqlConnection connection, NpgsqlTransaction transaction, long village, SavedVillage save, CancellationToken cancellation)
    {
        var lines = save.Log;
        await using var batch = new NpgsqlBatch(connection, transaction)
        {
            BatchCommands =
            {
                new("INSERT INTO village_save (village, tick, format, state) VALUES ($1, $2, $3, $4)")
                {
                    Parameters =
                    {
                        new() { Value = village },
                        new() { Value = save.Tick },
                        new() { Value = save.Format },
                        new() { Value = SaveJson.ToJson(save), NpgsqlDbType = NpgsqlDbType.Jsonb },
                    },
                },
                // every save holds the recent log; a line that an earlier save wrote is not written twice
                new("""
                    INSERT INTO village_log (village, seq, tick, kind, subject, detail)
                    SELECT $1, * FROM unnest($2::bigint[], $3::bigint[], $4::text[], $5::text[], $6::text[])
                    ON CONFLICT (village, seq) DO NOTHING
                    """)
                {
                    Parameters =
                    {
                        new() { Value = village },
                        new() { Value = lines.Select((_, i) => save.LogStart + i).ToArray() },
                        new() { Value = lines.Select(line => line.Tick).ToArray() },
                        new() { Value = lines.Select(line => line.Kind).ToArray() },
                        new() { Value = lines.Select(line => line.Subject).ToArray() },
                        new() { Value = lines.Select(line => line.Detail).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
                    },
                },
                new("DELETE FROM village_save WHERE village = $1 AND id NOT IN (SELECT id FROM village_save WHERE village = $1 ORDER BY id DESC LIMIT $2)")
                {
                    Parameters = { new() { Value = village }, new() { Value = KeptSaves } },
                },
            },
        };
        await batch.ExecuteNonQueryAsync(cancellation);
    }

    public async Task<decimal> TalkSpent(DateOnly day, CancellationToken cancellation)
    {
        await using var command = _source.CreateCommand("SELECT spent FROM talk_budget WHERE day = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = day });
        return await command.ExecuteScalarAsync(cancellation) is decimal spent ? spent : 0m;
    }

    public async Task SaveTalkSpent(DateOnly day, decimal spent, CancellationToken cancellation)
    {
        await using var command = _source.CreateCommand("""
            INSERT INTO talk_budget (day, spent) VALUES ($1, $2)
            ON CONFLICT (day) DO UPDATE SET spent = GREATEST(talk_budget.spent, EXCLUDED.spent)
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = day });
        command.Parameters.Add(new NpgsqlParameter { Value = spent });
        await command.ExecuteNonQueryAsync(cancellation);
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();

    public void Dispose() => _source.Dispose();
}
