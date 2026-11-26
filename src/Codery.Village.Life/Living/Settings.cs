namespace Codery.Village.Life.Living;

/// <summary>
/// The section "living" of the world. <paramref name="Start"/>: units in stock at the start, by substance id;
/// <paramref name="Built"/>: the structures that stand at the start. <paramref name="Endless"/>: substances there is always
/// enough of (water), never stocked. Fuel that burns leaves <paramref name="AshShare"/> of itself as <paramref name="Ash"/>. <paramref name="Output"/>: units a
/// worker of a trade gathers or makes a working day (per recipe for a maker). A gatherer gets its full output while each
/// worker on a substance has <paramref name="TilesPerWorker"/> tiles of it. <paramref name="Seasons"/>: for what grows, a factor
/// per calendar month (January first); a drought cuts those. <paramref name="Cold"/>: per month, 0 to 100.
/// Each person eats <paramref name="DailyNutrition"/> points a day; each household burns <paramref name="FuelPerHousehold"/>
/// units at Cold 100. Stock loses (100 - durability)/100 times
/// <paramref name="SpoilRate"/> a day; a possession breaks with (100 - durability)/100 times <paramref name="BreakRate"/> a day.
/// A household's possession is made anew when the new one scores <paramref name="Upgrade"/> better. A maker makes a missing
/// input first, at most <paramref name="MaxDepth"/> steps deep. A kiln or furnace takes <paramref name="StructureCost"/> units.
/// </summary>
public sealed record LivingRules(
    IReadOnlyDictionary<string, double> Start,
    IReadOnlyList<string> Built,
    IReadOnlyList<string> Endless,
    string Ash,
    IReadOnlyDictionary<string, double> Output,
    double TilesPerWorker,
    IReadOnlyDictionary<string, IReadOnlyList<double>> Seasons,
    IReadOnlyList<int> Cold,
    DroughtRules Drought,
    double DailyNutrition,
    double FuelPerHousehold,
    double AshShare,
    double SpoilRate,
    double BreakRate,
    double Upgrade,
    int MaxDepth,
    double StructureCost,
    SchoolRules School,
    Roles Roles,
    HarmRules Harm,
    SicknessRules Sickness);

/// <summary>A drought begins with <paramref name="Chance"/> on a day of <paramref name="Months"/> (1 to 12), lasts
/// <paramref name="MinDays"/> to <paramref name="MaxDays"/> days, and multiplies what grows by <paramref name="Factor"/>.</summary>
public sealed record DroughtRules(IReadOnlyList<int> Months, double Chance, int MinDays, int MaxDays, double Factor);

/// <summary>A school is built once <paramref name="Pupils"/> pupils live and a teacher does, of the best substance for the use <paramref name="Of"/>.</summary>
public sealed record SchoolRules(int Pupils, string Of);

/// <summary>The trades that build, care and teach.</summary>
public sealed record Roles(string Builder, string Carer, string Teacher);

/// <summary>
/// Health lost a day: <paramref name="Hunger"/> when nothing was eaten, <paramref name="Cold"/> at Cold 100 without fuel (both
/// times the share that was short), times <paramref name="Old"/> for the old and <paramref name="Child"/> for children in the
/// cold. Whoever is fed, warm and well gets <paramref name="Heal"/> back.
/// </summary>
public sealed record HarmRules(double Hunger, double Cold, double Old, double Child, double Heal);

/// <summary>
/// A day's chance to fall ill: <paramref name="Chance"/>, times <paramref name="Hungry"/> or <paramref name="Cold"/> on a short
/// day; each ill housemate adds <paramref name="Household"/>, each other ill person <paramref name="Beyond"/>. The ill lose
/// <paramref name="Harm"/> a day (half while a carer is well) and recover with <paramref name="Recover"/> a day.
/// </summary>
public sealed record SicknessRules(double Chance, double Hungry, double Cold, double Household, double Beyond, double Harm, double Recover);
