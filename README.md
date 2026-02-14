# Rocket.RiverOfNews
This is a website that allows river of news style of RSS aggregration

## Development
- Build: `dotnet build Rocket.RiverOfNews.slnx`
- Test: `dotnet test --solution Rocket.RiverOfNews.slnx`
- Run: `dotnet run --project src/Rocket.RiverOfNews`
- River UI: `http://localhost:5000/river` (or configured URL)

## Operational Notes
- Retention cleanup runs hourly and deletes items older than 30 days by `published_at`.
- Performance check endpoint: `GET /api/perf/latest-200`
  - Target: `durationMilliseconds < 2000`
