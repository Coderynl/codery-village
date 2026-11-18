using Codery.Village.Core;
using Codery.Village.Core.Modules;
using Codery.Village.Core.Save;
using Codery.Village.Core.Time;
using Codery.Village.Server.Store;

namespace Codery.Village.Tests;

/// <summary>The village in Postgres: what goes in comes out, and nothing grows without end.</summary>
[Collection(PostgresCollection.Name)]
public class StoreTests(PostgresFixture postgres)
{
    private static Engine Started(int ticks, ulong seed = 1, IReadOnlyList<IModule>? modules = null)
    {
        var engine = new Engine(modules ?? TestVillage.Modules(), TestVillage.Data(), TestVillage.Map(), seed, new SimulatedTime(TestVillage.Start));
        engine.Start();
        Run(engine, ticks);
        return engine;
    }

    private static void Run(Engine engine, int ticks)
    {
        for (var i = 0; i < ticks; i++) engine.Step();
    }

    private async Task<PostgresVillageStore> Store(string? database = null)
    {
        var store = new PostgresVillageStore(database ?? await postgres.NewDatabase());
        await store.Prepare(CancellationToken.None);
        return store;
    }

    [Fact]
    public async Task The_tables_can_be_made_twice_in_a_row()
    {
        await using var store = new PostgresVillageStore(await postgres.NewDatabase());
        await store.Prepare(CancellationToken.None);
        await store.Prepare(CancellationToken.None);
        Assert.Null(await store.Latest(CancellationToken.None));
    }

    [Fact]
    public async Task What_was_saved_comes_back_as_it_was()
    {
        await using var store = await Store();
        var engine = Started(2000, seed: ulong.MaxValue);
        var saved = engine.Save();

        var village = await store.Begin(ulong.MaxValue, saved, CancellationToken.None);
        var latest = await store.Latest(CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(village, latest.Id);
        Assert.Equal(ulong.MaxValue, latest.Seed);
        Assert.NotNull(latest.Save);
        // jsonb writes the keys in an order of its own; loaded, it is the same village
        var loaded = Engine.Load(latest.Save, TestVillage.Modules(), TestVillage.Data(), TestVillage.Map(), new SimulatedTime(latest.Save.Calendar));
        Assert.Equal(SaveJson.ToJson(saved), SaveJson.ToJson(loaded.Save()));
    }

    [Fact]
    public async Task The_latest_save_of_the_village_begun_last_is_the_one_that_comes_back()
    {
        await using var store = await Store();
        var engine = Started(0);
        var first = await store.Begin(1, engine.Save(), CancellationToken.None);
        Run(engine, 100);
        await store.Save(first, engine.Save(), CancellationToken.None);
        Assert.Equal(100, (await store.Latest(CancellationToken.None))!.Save!.Tick);

        var second = await store.Begin(2, Started(0, seed: 2).Save(), CancellationToken.None);
        var latest = await store.Latest(CancellationToken.None);
        Assert.Equal(second, latest!.Id);
        Assert.Equal(2UL, latest.Seed);
        Assert.Equal(0, latest.Save!.Tick);
    }

    [Fact]
    public async Task Of_every_village_the_latest_ten_saves_are_kept()
    {
        var database = await postgres.NewDatabase();
        await using var store = await Store(database);
        var engine = Started(0);
        var first = await store.Begin(1, engine.Save(), CancellationToken.None);
        for (var save = 0; save < 14; save++)
        {
            engine.Step();
            await store.Save(first, engine.Save(), CancellationToken.None);
        }
        var other = Started(0, seed: 2);
        var second = await store.Begin(2, other.Save(), CancellationToken.None);
        await store.Save(second, other.Save(), CancellationToken.None);

        Assert.Equal(10, await PostgresFixture.Count(database, $"SELECT count(*) FROM village_save WHERE village = {first}"));
        Assert.Equal(5, await PostgresFixture.Count(database, $"SELECT min(tick) FROM village_save WHERE village = {first}"));
        Assert.Equal(14, await PostgresFixture.Count(database, $"SELECT max(tick) FROM village_save WHERE village = {first}"));
        Assert.Equal(2, await PostgresFixture.Count(database, $"SELECT count(*) FROM village_save WHERE village = {second}"));
    }

    [Fact]
    public async Task The_log_is_kept_line_by_line_and_a_line_saved_twice_is_kept_once()
    {
        var database = await postgres.NewDatabase();
        await using var store = await Store(database);
        var engine = Started(10, modules: [new LoggingModule("scribe")]);
        var village = await store.Begin(1, engine.Save(), CancellationToken.None);
        // the next save holds the same first ten lines again, and ten new ones
        Run(engine, 10);
        await store.Save(village, engine.Save(), CancellationToken.None);

        Assert.Equal(20, await PostgresFixture.Count(database, $"SELECT count(*) FROM village_log WHERE village = {village}"));
        Assert.Equal(15, await PostgresFixture.Count(database, "SELECT tick FROM village_log WHERE seq = 14"));
        Assert.Equal(["noted scribe 15"], await PostgresFixture.Strings(database, "SELECT kind || ' ' || subject || ' ' || detail FROM village_log WHERE seq = 14"));
    }

    [Fact]
    public async Task A_line_without_detail_is_kept_without_detail()
    {
        var database = await postgres.NewDatabase();
        await using var store = await Store(database);
        var saved = Started(0, modules: [new CountingModule("counter")]).Save() with { Log = [new LogEntry(0, "quiet", "counter")] };

        await store.Begin(1, saved, CancellationToken.None);

        Assert.Equal(1, await PostgresFixture.Count(database, "SELECT count(*) FROM village_log WHERE kind = 'quiet' AND detail IS NULL"));
    }

    [Fact]
    public async Task A_save_that_fails_leaves_nothing_behind()
    {
        var database = await postgres.NewDatabase();
        await using var store = await Store(database);
        var engine = Started(5, modules: [new LoggingModule("scribe")]);
        var village = await store.Begin(1, engine.Save() with { Log = [] }, CancellationToken.None);

        // a village that is not there: the save is refused, and its log lines with it
        await Assert.ThrowsAnyAsync<Exception>(() => store.Save(village + 1, engine.Save(), CancellationToken.None));

        Assert.Equal(0, await PostgresFixture.Count(database, "SELECT count(*) FROM village_log"));
        Assert.Equal(1, await PostgresFixture.Count(database, "SELECT count(*) FROM village_save"));
    }

    [Fact]
    public async Task What_the_conversations_cost_is_kept_per_day_and_never_goes_down()
    {
        await using var store = await Store();
        var monday = new DateOnly(2026, 6, 1);

        Assert.Equal(0m, await store.TalkSpent(monday, CancellationToken.None));
        await store.SaveTalkSpent(monday, 1.25m, CancellationToken.None);
        await store.SaveTalkSpent(monday.AddDays(1), 0.5m, CancellationToken.None);
        await store.SaveTalkSpent(monday, 1m, CancellationToken.None);

        Assert.Equal(1.25m, await store.TalkSpent(monday, CancellationToken.None));
        Assert.Equal(0.5m, await store.TalkSpent(monday.AddDays(1), CancellationToken.None));
    }
}
