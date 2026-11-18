# Codery Village: for agents

What every agent working in this repo must know, whatever the tool. Claude Code reads this file via `@AGENTS.md` at the top of `CLAUDE.md`.


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

### Running

- `dotnet test` in the root. Docker must be running: the tests against the database start their own Postgres container. Locally it takes five to six minutes, on CI fifteen minutes.
- The tests that use a real server or the real clock are in the collection `RealTimeCollection`; the tests against the database in `PostgresCollection`. Both run only after the rest, and not side by side. A new test of that kind belongs in one of the two: next to the tests that simulate weeks of village, they miss their timeouts on CI (four cores).


