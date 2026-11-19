@AGENTS.md

# Codery Village

Codery's village: a .NET solution with `Core`, `Modules`, `Server` and `Tests`.

## Fixed requirements

- **The world is data:** numbers, names, schedules, wages, phases and sentences live in `data/`, not in code. Data is loaded at startup and not changed after that.
- **Repeatable:** the same seed and the same clock give the same village, also after saving and loading. No `System.Random`, no `DateTime.Now`: only the core RNG (`Rng.For(seed, stream)`) and the core's clocks.
- **No network, files or processes** in `Core` and `Modules`. That is allowed only in `Codery.Village.Server`.
- **Decency:** violence and death belong to life and may be named plainly, without blood, gore or cruelty. No sexual content, no religion, no discrimination. Gender determines only name and pronoun; age only the life stage.
- **Test-first,** following the rules in `AGENTS.md`. Names in code are English; texts for visitors are in Dutch and English in the data.
- **Commits:** in English, a subject and a short body with what changed, without Co-Authored-By.
