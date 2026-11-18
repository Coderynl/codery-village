# Codery Village: voor agents

Wat elke agent die in deze repo werkt moet weten, welke tool het ook is. Claude Code leest dit bestand via `@AGENTS.md` bovenaan `CLAUDE.md`.

## Testen

### Draaien

- `dotnet test` in de root. Docker moet draaien: de tests tegen de database starten zelf een Postgres-container. Lokaal duurt het vijf à zes minuten, op CI een kwartier.
- De tests die een echte server of de echte klok gebruiken, staan in de collectie `RealTimeCollection`; de tests tegen de database in `PostgresCollection`. Beide draaien pas na de rest, en niet naast elkaar. Een nieuwe test van die soort hoort in een van de twee: naast de tests die weken dorp simuleren, halen ze op CI (vier kernen) hun wachttijden niet.

### Wat je wel en niet test

_Nog in te vullen door Michael._
