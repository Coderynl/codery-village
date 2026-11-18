# Codery Village

Codery's village: a simulated village of residents, houses and islands that keeps running in the background. It runs as a server and tells every connected page what changes. Everything the village is (numbers, names, wages, sentences) lives as data in `data/`, not in code.

## Running

```
dotnet test
dotnet run --project src/Codery.Village.Server
```

For a local database: `docker compose up -d` (Postgres on port 5433). The tests with a database start their own container, so Docker Desktop must be running then.

More about Codery: [codery.nl](https://codery.nl).
