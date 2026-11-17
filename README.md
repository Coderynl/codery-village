# Codery Village

Het dorp van Codery: een gesimuleerd dorp van bewoners, huizen en eilanden dat op de achtergrond doorloopt. Het draait als server en vertelt elke verbonden pagina wat er verandert. Alles wat het dorp is (getallen, namen, lonen, zinnen) staat als data in `data/`, niet in code.

## Draaien

```
dotnet test
dotnet run --project src/Codery.Village.Server
```

Voor een database lokaal: `docker compose up -d` (Postgres op poort 5433). De tests met een database starten zelf een container, dus Docker Desktop moet dan draaien.

Meer over Codery: [codery.nl](https://codery.nl).
