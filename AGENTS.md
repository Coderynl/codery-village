# Codery Village: voor agents

Wat elke agent die in deze repo werkt moet weten, welke tool het ook is. Claude Code leest dit bestand via `@AGENTS.md` bovenaan `CLAUDE.md`.


## Testing
- Write a test only if it would fail when behaviour a consumer relies on breaks.
  State that risk in one line in the test plan before writing code.
- Test through the public boundary (handler, endpoint, message consumer),
  not through private helpers or internal call order.
- Do NOT test: DTOs/records, mappings without logic, DI registration,
  framework or library behaviour, logging calls.
- Mock only external boundaries (Service Bus, HTTP, clock).
  Never assert solely on "method X was called".
- One happy path plus the failure cases with business impact.
  No permutations of the same path.
- Every bug fix gets exactly one regression test that fails before the fix.
- No coverage target. List what you deliberately left untested in the PR.

### Draaien

- `dotnet test` in de root. Docker moet draaien: de tests tegen de database starten zelf een Postgres-container. Lokaal duurt het vijf à zes minuten, op CI een kwartier.
- De tests die een echte server of de echte klok gebruiken, staan in de collectie `RealTimeCollection`; de tests tegen de database in `PostgresCollection`. Beide draaien pas na de rest, en niet naast elkaar. Een nieuwe test van die soort hoort in een van de twee: naast de tests die weken dorp simuleren, halen ze op CI (vier kernen) hun wachttijden niet.


