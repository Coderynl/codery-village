namespace Codery.Village.Life.Knowing;

/// <summary>
/// The section "knowing" of the world. A person knows an item once its level passes <paramref name="Knows"/>. At the start
/// every adult knows the start processes, the start uses and their trade's items at a level from <paramref name="StartMin"/>
/// to <paramref name="StartMax"/>. A day's learning moves a level towards the teacher's by rate × (teacher's level − level) / 100
/// points: <paramref name="ParentRate"/> for a child or pupil from a parent, <paramref name="MasterRate"/> for an adult from the
/// best master of their trade, <paramref name="SchoolRate"/> for a pupil from a teacher who knows the item at
/// <paramref name="TeacherMin"/> or more, <paramref name="WatchRate"/> for anyone who sees a worker of their household at work.
/// Each item used at work gains <paramref name="Practice"/> points a day.
/// </summary>
public sealed record KnowingRules(
    double Knows,
    int StartMin,
    int StartMax,
    double ParentRate,
    double MasterRate,
    double SchoolRate,
    double WatchRate,
    double TeacherMin,
    double Practice,
    string Teacher,
    string School);
