# The ladder to the future

The village starts prehistoric: the base (`catalog.json`) knows fire pits, huts, gathering, fishing from the shore, reed, loam, stones and driftwood. Every step forward is a release: a wish of the residents that Eddie builds. The steps already known are ready here, so progress can be shown by hand (`Village:Extras`).

A release adds lists to the catalog (`materials`, `roles`, `blueprints`, `things`) and changes nothing that already exists. Every release serves a wish from `wishes.json` with `serves`; the base never does.

## Order

A release may use what an earlier one adds. So load it on the base plus the releases it depends on. This order loads in one go:

1. `tools.json` — lumberyard (wood). Wish `tools`. No dependencies.
2. `sawmill.json` — sawmill (planks from wood). Wish `planks`. After `tools.json`.
3. `farm.json` — farm (grain). Wish `fields`. No dependencies.
4. `bakery.json` — bakery (bread from grain). Wish `bread`. After `farm.json`.
5. `quarry.json` — quarry (dressed stone). Wish `stone`. No dependencies.
6. `brickworks.json` — clay pit and brickworks (clay, bricks). Wish `bricks`. After `quarry.json`.
7. `peat.json` — peat cutter (peat). Wish `peat`. No dependencies.
8. `wooden-house.json` — wooden house. Wish `house`. After `sawmill.json` (and so `tools.json`).
9. `stone-house.json` — stone house. Wish `house`. After `quarry.json` and `brickworks.json`.
10. `jetty.json` — fishing jetty. Wish `jetty`. After `quarry.json`.
11. `cafe.json` — café. Wish `cafe`. No dependencies.
12. `grapes.json` — wild vines (grapes, picked on fertile soil). Wish `fruit`. No dependencies.

What an island can do with a release depends on its soil: Oost has no forest and no fertile soil, so no lumberyard, farm, bakery or vines; West has no rock, clay or fishing ground.

The test `Every_release_serves_a_wish_and_loads` derives per release what it depends on, loads it on that and checks that this list names every release once, after what it needs.

## Wishes waiting for a release

A wish with `after` only counts once the loaded data (the base plus the releases) knows every named id, a material or a structure. That way one release brings the next wish: with `grapes.json` the village knows grapes, and then residents wish for `wine`. No release serves wine, so that becomes a task for Eddie. An id in `after` that does not exist anywhere yet is not an error: a later release from Eddie can bring it. An empty `after` is an error.
