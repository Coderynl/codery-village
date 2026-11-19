# Codery Village

Codery's village: a simulated village of residents, houses and islands that keeps running in the background. It runs as a server and tells every connected page what changes. Everything the village is (numbers, names, wages, sentences) lives as data in `data/`, not in code.

## Running

```
dotnet test
dotnet run --project src/Codery.Village.Server
```

On a developer's machine (`ASPNETCORE_ENVIRONMENT=Development`) `Village__ClockStart=2026-06-03T13:00:00+02:00` starts the clock at another moment, and `Village__Advance=3d` (or `12h`, `3.00:00:00`) begins the village that much older, its days simulated before it serves and ending at the start moment; a saved village in the database wins over it.

For a local database: `docker compose up -d` (Postgres on port 5433). The tests with a database start their own container, so Docker Desktop must be running then.

More about Codery: [codery.nl](https://codery.nl).
