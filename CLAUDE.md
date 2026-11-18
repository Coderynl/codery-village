@AGENTS.md

# Codery Village

Het dorp van Codery: .NET-solution met `Core`, `Modules`, `Server` en `Tests`.

## Vaste eisen

- **De wereld is data:** getallen, namen, schema's, lonen, fases en zinnen staan in `data/`, niet in code. Data wordt bij het opstarten geladen en daarna niet meer veranderd.
- **Herhaalbaar:** dezelfde seed en dezelfde klok geven hetzelfde dorp, ook na bewaren en laden. Geen `System.Random`, geen `DateTime.Now`: alleen de kern-RNG (`Rng.For(seed, stream)`) en de klokken van de kern.
- **Geen netwerk, bestanden of processen** in `Core` en `Modules`. Dat mag alleen in `Codery.Village.Server`.
- **Fatsoen:** geen geweld, geen dood, geen seksuele inhoud, geen geloof, geen discriminatie. Geslacht bepaalt alleen naam en voornaamwoord; leeftijd alleen de levensfase.
- **Test-eerst:** elke regel heeft een test. Namen in code zijn Engels; teksten voor bezoekers staan in het Nederlands en het Engels in de data.
- **Commits:** in het Nederlands, een onderwerp en een korte body met wat er veranderde, zonder Co-Authored-By.
