# De ladder naar de toekomst

Het dorp begint prehistorisch: de basis (`catalog.json`) kent vuurplaatsen, hutten, verzamelen, vissen vanaf de kant, riet, leem, stenen en drijfhout. Elke stap vooruit is een release: een wens van de bewoners die Eddie bouwt. De stappen die nu al bekend zijn, staan hier klaar, zodat de vooruitgang met de hand te tonen is (`Village:Extras`).

Een release voegt lijsten toe aan de catalogus (`materials`, `roles`, `blueprints`, `things`) en verandert niets wat er al is. Elke release bedient met `serves` een wens uit `wishes.json`; de basis doet dat nooit.

## Volgorde

Een release mag gebruiken wat een eerdere toevoegt. Laad hem dus op de basis plus de releases waar hij van afhangt. Deze volgorde laadt in één keer:

1. `tools.json` — houthakkerij (hout). Wens `tools`. Hangt nergens van af.
2. `sawmill.json` — zagerij (planken uit hout). Wens `planks`. Na `tools.json`.
3. `farm.json` — boerderij (graan). Wens `fields`. Hangt nergens van af.
4. `bakery.json` — bakkerij (brood uit graan). Wens `bread`. Na `farm.json`.
5. `quarry.json` — steengroeve (behouwen steen). Wens `stone`. Hangt nergens van af.
6. `brickworks.json` — kleiput en steenbakkerij (klei, bakstenen). Wens `bricks`. Na `quarry.json`.
7. `peat.json` — turfsteker (turf). Wens `peat`. Hangt nergens van af.
8. `wooden-house.json` — houten huis. Wens `house`. Na `sawmill.json` (en dus `tools.json`).
9. `stone-house.json` — stenen huis. Wens `house`. Na `quarry.json` en `brickworks.json`.
10. `jetty.json` — vissteiger. Wens `jetty`. Na `quarry.json`.
11. `cafe.json` — café. Wens `cafe`. Hangt nergens van af.

Wat een eiland met een release kan, hangt af van zijn grond: Oost heeft geen bos en geen vruchtbare grond, dus geen houthakkerij, boerderij of bakkerij; West heeft geen rots, klei of visgrond.

De test `Every_release_serves_a_wish_and_loads` leidt per release af waar hij van afhangt, laadt hem daarop en controleert dat deze lijst elke release één keer noemt, na wat hij nodig heeft.
