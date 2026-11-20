# EDDIE.md: team knowledge for the Eddie agent

Repository: the village of Codery (`codery-village`, .NET 10). An issue here is a wish of the residents that no release serves yet. Your job is a release: one JSON file.

## What a release is

A file `data/releases/<id>.json` (id in lower case, digits and dashes). It only adds to the lists of the catalog (`materials`, `roles`, `blueprints`, `things`) and changes nothing that already exists, so the order of loading does not matter. It may use what another release adds. It serves one wish from `data/wishes.json` with `serves` on its blueprint.

The vocabulary of a blueprint: `look` (how it is drawn), `on` (the terrain it may stand on), `near` (what must be next to it), `takes` (what the recipe takes from nature, such as `game`), `serves` (the wish). Examples: `data/releases/mill.json` (flour from grain, `on: ["land"]`, `serves: "flour"`) and `data/releases/hunting.json` (`near: "forest"`, `takes: "game"`, `serves: "meat"`).

```json
{
  "materials": [{ "id": "flour", "nl": "meel", "en": "flour" }],
  "roles": [{ "id": "miller", "nl": "molenaar", "en": "miller" }],
  "blueprints": [{
    "id": "mill", "nl": "molen", "en": "mill", "on": ["land"], "serves": "flour",
    "jobs": [{ "role": "miller", "count": 1, "shift": "day", "outdoor": false }],
    "recipe": { "in": { "grain": 2 }, "out": { "flour": 2 }, "ticks": 240 },
    "phases": [{ "id": "foundation", "nl": "fundering", "en": "foundation", "ticks": 300, "cost": { "branches": 2 } }],
    "look": { "form": "kit", "models": ["building_windmill"] }
  }]
}
```

Read `data/releases/README.md` first: it is the ladder, with the rules about what a release may bring. Every text for visitors is in Dutch and English. Decency: wine is allowed, drunkenness and drugs are not.

## What you may change

Only these paths. CI (job `paths`) fails your pull request on anything else:

- add `data/releases/<id>.json`;
- change `data/releases/README.md`: one new numbered line for your release, nothing else;
- delete a release that your own bot added (see Revert).

Never change `.github/`, `src/`, `tests/`, `EDDIE.md`, `data/wishes.json` or any other data file, and no existing release.

## Build & test

- Quick (T1, T2): `dotnet test --filter Category!=Scenario`
- Full (T3): `dotnet test` (needs Docker running; about 12 minutes; the scenarios are in it)
- The test `Every_release_serves_a_wish_and_loads` loads your release and checks that it serves a wish and that the README names it once.

## Run locally (T4)

Not applicable. The village runs on its own server; there is no test account.

## Delivery

- Default branch `main`: pull request required, squash merge, auto-merge allowed, no required approval (mode `merge-on-green`). Your pull request touches only release paths, which have no code owner.
- CI on pull requests: GitHub Actions workflow `ci.yml` (jobs `paths`, `part`, required check `test`).
- Deployment: Coolify deploys `main` by itself after a merge, usually within a few minutes.
- Integration URL: https://village-engine.codery.nl

## Verification after the deploy (T5 to T8)

- T5: `GET https://village-engine.codery.nl/version` returns `{ "commit": "<sha>", "releases": ["<id>", ...] }`. `commit` is the merge commit and the id of your new release is in `releases`.
- T6: the wish of the issue is now answered: the same `/version` lists your release.
- Health: `GET https://village-engine.codery.nl/health` returns `ok`.
- No login, no test account: not applicable.

## Revert

If a check after the deploy fails: open a pull request on branch `eddie/<n>-revert` that deletes your own release file and its line in `data/releases/README.md`. Nothing else.

## House rules

- Branch `eddie/<issue>-<slug>`; the pull request says `Refs #<issue>`.
- Commits in English: a subject and a short body with what changed, without Co-Authored-By.
- A new package, a change in code or a wish that needs more than a release: stop with `needs_input`.
