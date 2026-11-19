# The ladder to the future

The village starts prehistoric: the base (`catalog.json`) knows fire pits, huts, gathering, fishing from the shore, reed, loam, stones and driftwood. Every step forward is a release: a wish of the residents that Eddie builds. The live village loads the base and every release in this folder; a new release is simply a new file here.

A release adds lists to the catalog (`materials`, `roles`, `blueprints`, `things`) and changes nothing that already exists (one exception: `bakery.json`, ours, bakes from the flour of `mill.json` instead of grain), so the order in which the releases load does not matter. A release may use what another release adds (the sawmill saws the wood of the lumberyard). Every release serves a wish from `wishes.json` with `serves`; the base never does.

## The releases

Each release is named once in this list, with what it brings and the wish it serves:

1. `tools.json` — lumberyard (wood). Wish `tools`.
2. `sawmill.json` — sawmill (planks from wood). Wish `planks`.
3. `farm.json` — farm (grain). Wish `fields`.
4. `bakery.json` — bakery (bread from flour). Wish `bread`.
5. `quarry.json` — quarry (dressed stone). Wish `stone`.
6. `brickworks.json` — clay pit and brickworks (clay, bricks). Wish `bricks`.
7. `peat.json` — peat cutter (peat). Wish `peat`.
8. `wooden-house.json` — wooden house. Wish `house`.
9. `stone-house.json` — stone house. Wish `house`.
10. `jetty.json` — fishing jetty. Wish `jetty`.
11. `cafe.json` — café. Wish `cafe`.
12. `grapes.json` — wild vines (grapes, picked on fertile soil). Wish `fruit`.
13. `mill.json` — mill (flour from grain), between the farm and the bakery. Wish `flour`.

What an island can do with a release depends on its soil: Oost has no forest and no fertile soil, so no lumberyard, farm, mill, bakery or vines; West has no rock, clay or fishing ground.

The test `Every_release_serves_a_wish_and_loads` derives per release what it depends on, loads it on the base plus that, checks that it serves a wish, and checks that this list names every release once.

## Wishes waiting for a release

A wish with `after` only counts once the loaded data (the base plus the releases) knows every named id, a material or a structure. That way one release brings the next wish: with `grapes.json` the village knows grapes, and then residents wish for `wine`. No release serves wine, so that becomes a task for Eddie. An id in `after` that does not exist anywhere yet is not an error: a later release from Eddie can bring it. An empty `after` is an error.
