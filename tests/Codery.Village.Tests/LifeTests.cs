using Codery.Village.Core.Data;
using Codery.Village.Core.Map;
using Codery.Village.Modules.Contracts;
using Codery.Village.Modules.Day;
using Codery.Village.Modules.People;

namespace Codery.Village.Tests;

/// <summary>
/// The day as a visitor sees it, in the real village and its real data: free time outside, the evening at the fire,
/// a chat at the door by day, and an honest night.
/// </summary>
public class LifeTests
{
    private static readonly IEnumerable<Island> Ordinary = Builders.RealMap.Islands.Where(island => !island.Eddie);

    /// <summary>A Dutch time in 2026 on the given month and day; summer time from April to October.</summary>
    private static DateTimeOffset At(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(month is >= 4 and <= 10 ? 2 : 1));

    // Risk: someone without a job stands "working" in his own yard, a figure without a reason.
    [Fact]
    public void Without_a_job_there_is_no_work_at_home()
    {
        var checkedOnes = 0;
        foreach (var seed in new ulong[] { 1, 2, 3 })
        {
            // the village starts in the middle of a working day, before anyone has a job
            var village = Builders.Start(seed, Builders.June(2, 10));
            Check(village);
            for (var hour = 11; hour < 24; hour += 3)
            {
                village.JumpTo(Builders.June(2, hour));
                Check(village);
            }
        }
        Assert.True(checkedOnes > 0, "nobody was checked");

        // whoever works, works at his job: not at home, in the yard, for want of one
        void Check(Builders.Village village)
        {
            foreach (var resident in village.People.All.Where(resident => resident.Phase == LifePhase.Working))
            {
                var doing = village.Day.Of(resident.Id);
                checkedOnes++;
                if (doing.Activity != Activity.Work) continue;
                var job = village.Work.JobOf(resident.Id);
                // a builder helping on the land may stand on another tile of the area, until the quarter after a
                // gatherer took his
                var helping = job is { Building: < 0, Role: "builder" } && village.Building.All.Any(structure => structure.Island == resident.Island
                    && village.Work.WorkAreaOf(structure.Id).Contains(doing.Place));
                Assert.True(job is not null && (job.Place == doing.Place || helping),
                    $"{village.Time.GetUtcNow().ToOffset(TimeSpan.FromHours(2)):HH:mm} {resident.Name} works at {doing.Place}, his job is at {job?.Place.ToString() ?? "nowhere"}");
            }
        }
    }

    // Risk: the evening is empty, the fire burns for nobody.
    [Fact]
    public void After_dark_the_island_sits_at_the_fire()
    {
        foreach (var seed in new ulong[] { 1, 2, 3, 4, 5 })
        {
            // a Wednesday in September: the evening out begins at seven while it is light, and it gets dark at five to eight
            var village = Builders.Start(seed, At(9, 2, 18));
            for (var at = At(9, 2, 18, 15); at <= At(9, 2, 20, 30); at += TimeSpan.FromMinutes(15)) village.JumpTo(at);
            foreach (var island in Ordinary)
            {
                var fire = village.Building.GatheringOn(island.Id);
                Assert.NotNull(fire);
                var there = village.People.On(island.Id).Count(resident =>
                    village.Day.Of(resident.Id) is { Inside: false } doing && doing.Place.DistanceTo(fire.Value) <= 1);
                Assert.True(there >= 2, $"seed {seed}: {there} at the fire of {island.Name} at half past eight");
            }
        }
    }

    // Risk: the evenings look the same all year, the summer as empty as the winter.
    [Fact]
    public void A_summer_evening_is_busier_than_a_winter_evening()
    {
        double Outside(DateTimeOffset afternoon, DateTimeOffset evening)
        {
            var total = 0;
            for (ulong seed = 1; seed <= 30; seed++)
            {
                var village = Builders.Start(seed, afternoon);
                village.JumpTo(evening);
                total += Ordinary.Sum(island => village.People.On(island.Id).Count(resident => !village.Day.Of(resident.Id).Inside));
            }
            return total / 30.0;
        }

        // two Wednesdays at eight in the evening
        var july = Outside(At(7, 8, 17), At(7, 8, 20));
        var january = Outside(At(1, 7, 17), At(1, 7, 20));

        Assert.True(july > january, $"{july:0.0} outside in July, {january:0.0} in January");
    }

    // Risk: just as it gets dark, those already at the fire get up and walk away from it.
    [Fact]
    public void Who_sits_at_the_fire_at_dusk_stays_there()
    {
        var sitters = 0;
        foreach (var seed in Enumerable.Range(1, 10).Select(seed => (ulong)seed))
        {
            // a Wednesday in September: out from seven while it is light, dark at five to eight
            var village = Builders.Start(seed, At(9, 2, 18));
            for (var at = At(9, 2, 18, 15); at <= At(9, 2, 19, 45); at += TimeSpan.FromMinutes(15)) village.JumpTo(at);
            var atTheFire = village.People.All.Where(resident => village.Building.GatheringOn(resident.Island) is { } fire
                    && village.Day.Of(resident.Id) is { Activity: Activity.Leisure } doing && doing.Place.DistanceTo(fire) <= 1)
                .ToDictionary(resident => resident, resident => village.Day.Of(resident.Id).Place);

            village.JumpTo(At(9, 2, 20));
            foreach (var (resident, place) in atTheFire)
            {
                var doing = village.Day.Of(resident.Id);
                Assert.True(doing.Activity == Activity.Leisure && doing.Place == place,
                    $"seed {seed}: {resident.Name} left the fire at {place} (fire {village.Building.GatheringOn(resident.Island)}) at dusk for {doing.Activity} at {doing.Place}");
                sitters++;
            }
        }
        Assert.True(sitters > 0, "nobody sat at the fire before dusk");
    }

    // Risk: visits by day happen out of sight, inside; or after dark on the doorstep.
    [Fact]
    public void A_daytime_visit_is_a_chat_at_the_door()
    {
        int byDay = 0, byNight = 0;
        foreach (var seed in new ulong[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            // a Wednesday in September: light until five to eight
            var village = Builders.Start(seed, At(9, 2, 12));
            village.JumpTo(At(9, 2, 14));
            foreach (var resident in Visiting(village))
            {
                var doing = village.Day.Of(resident.Id);
                Assert.False(doing.Inside, $"seed {seed}: {resident.Name} visits inside at two");
                Assert.True(IsDoorOfAnother(village, resident, doing.Place), $"seed {seed}: {resident.Name} visits at {doing.Place}, no door");
                byDay++;
            }

            // the evening out begins while it is light; after dark the visit goes on inside
            village.JumpTo(At(9, 2, 18, 45));
            village.JumpTo(At(9, 2, 19));
            village.JumpTo(At(9, 2, 21));
            foreach (var resident in Visiting(village))
            {
                var doing = village.Day.Of(resident.Id);
                Assert.True(doing.Inside, $"seed {seed}: {resident.Name} visits outside at nine, at {doing.Place}");
                byNight++;
            }
        }
        Assert.True(byDay > 0 && byNight > 0, $"{byDay} visits by day, {byNight} by night");
    }

    // Risk: after school the children sit indoors and the island looks empty.
    [Fact]
    public void Children_play_outside_after_school()
    {
        int pupils = 0, outside = 0;
        foreach (var seed in new ulong[] { 1, 2, 3, 4, 5 })
        {
            var village = Builders.Start(seed, Builders.June(2, 12));
            village.JumpTo(Builders.June(2, 16));
            foreach (var child in village.People.All.Where(resident => resident.Phase == LifePhase.Child && village.People.AgeOf(resident) >= 6))
            {
                pupils++;
                if (!village.Day.Of(child.Id).Inside) outside++;
            }
        }
        Assert.True(pupils > 0, "no pupils");
        // they play outside, not at home: all but a few on errands or visits
        Assert.True(outside * 4 >= pupils * 3, $"{outside} of {pupils} pupils outside at four");
    }

    // Risk: the night is filled with figures without a reason, and reads as a village that does not sleep.
    [Fact]
    public void At_night_only_who_has_a_reason_is_outside()
    {
        foreach (var seed in new ulong[] { 1, 2, 3 })
        {
            var village = Builders.Start(seed, At(9, 6, 12));
            // every night of a week, Sunday night and the weekend nights among them
            for (var day = 7; day <= 13; day++)
            {
                var now = At(9, day, 2);
                village.JumpTo(now);
                foreach (var resident in village.People.All)
                {
                    var doing = village.Day.Of(resident.Id);
                    if (doing.Inside) continue;
                    var reason = village.Work.JobOf(resident.Id) is { Shift: Shift.Night }
                        || village.Day.TripOf(resident.Id) is not null
                        || doing is { Walk: not null, Activity: Activity.Home or Activity.Sleep };
                    Assert.True(reason, $"seed {seed}, {now:ddd HH:mm}: {resident.Name} ({resident.Phase}) is outside, {doing.Activity} at {doing.Place}");
                }
            }
        }
    }

    // the residents on a visit now
    private static IEnumerable<Resident> Visiting(Builders.Village village) =>
        village.People.All.Where(resident => village.Day.Of(resident.Id).Activity == Activity.Visit);

    // a tile of land beside the home of someone of another household of his island
    private static bool IsDoorOfAnother(Builders.Village village, Resident visitor, Cell cell) =>
        Builders.RealMap.IsWalkable(cell) && Builders.RealMap.At(cell) != Ground.Lot
        && village.People.On(visitor.Island).Any(other => other.Household != visitor.Household
            && village.Building.HomeOf(other.Id) is { } home && home.DistanceTo(cell) == 1);
}
