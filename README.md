# Codery Village

Codery's village: a simulated village of residents, houses and islands that keeps running in the background. It runs as a server and tells every connected page what changes. Everything the village is (numbers, names, wages, sentences) lives as data in `data/`, not in code.

## Running

```
dotnet test
dotnet run --project src/Codery.Village.Server
```

On a developer's machine (`ASPNETCORE_ENVIRONMENT=Development`) `Village__ClockStart=2026-06-03T13:00:00+02:00` starts the clock at another moment, and `Village__Advance=3d` (or `12h`, `3.00:00:00`) begins the village that much older, its days simulated before it serves and ending at the start moment; a saved village in the database wins over it.

For a local database: `docker compose up -d` (Postgres on port 5433). The tests with a database start their own container, so Docker Desktop must be running then.

## The desk

When the residents wish for something the data cannot give them, the village writes a task. The desk (`EddieDesk`) carries that task to Eddie on GitHub, as the village's GitHub App. Once a minute it:

- opens an issue with the label `wish` for the task and posts Eddie's `/eddie` command under it; an open issue the App opened already for the same wish is used again;
- reads Eddie's results under the issue (only those of his login count): after `done` it waits until the server runs on data that serves the wish and says so on the issue, then follows the issue until Michael approves or rejects the release;
- asks again 6 hours after a failure, up to 3 attempts; after the third, after a reject, or when Eddie may not do it, the village puts the wish on hold and stops asking for it;
- keeps one errand per wish (the table `errands` in the database, or in memory without one) and saves after every step on GitHub, so a restart never opens a second issue or posts a command twice.

A wish on hold is asked again, on its issue, once Michael released it with `Village__Tasks__Release__0=<wish>` and the village wishes it again.

The desk is off unless `Village__Tasks__Enabled=true`; off, it touches nothing, not even GitHub. On, it needs these settings:

| Setting | What |
|---|---|
| `Village__Desk__AppId` | the GitHub App's id |
| `Village__Desk__PrivateKey` | the App's private key, as PEM |
| `Village__Desk__AppLogin` | the App's bot login, such as `codery-village[bot]` |
| `Village__Desk__EddieLogin` | Eddie's bot login |
| `Village__Desk__Owner` | who is mentioned when Eddie waits or a wish is held, and whose own `/eddie` commands count; `Codery-nl` when not set |
| `Village__Desk__Repository` | where the issues go; `Coderynl/codery-village` when not set |

Switched on while one of the first four is missing, the desk names the missing settings once in the log, never their values, and stays idle; the village runs on.

More about Codery: [codery.nl](https://codery.nl).
