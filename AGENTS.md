# AGENTS.md - Rocket.RiverOfNews

Guidance for AI coding agents working in this repository.

## Repository Overview

A web-based "River of News" RSS aggregator built with ASP.NET Core 10 and C# 14. Presents posts from multiple feeds in a single chronological stream with deduplication, filtering, and a performant river view.

**Tech Stack:** ASP.NET Core Minimal APIs, Dapper + SQLite, TailwindCSS, TUnit

## Build / Test / Lint Commands

```bash
# Build solution
dotnet build Rocket.RiverOfNews.slnx

# Run all tests
dotnet test Rocket.RiverOfNews.slnx

# Run a single test by name
dotnet test Rocket.RiverOfNews.slnx --filter "FullyQualifiedName~TestName"

# Run all tests in a specific class
dotnet test Rocket.RiverOfNews.slnx --filter "FullyQualifiedName~AddFeedApiTests"

# Run the application
dotnet run --project src/Rocket.RiverOfNews

# Type check / lint (warnings as errors enabled)
dotnet build Rocket.RiverOfNews.slnx
```

**Note:** `TreatWarningsAsErrors=true` is enabled, so `dotnet build` serves as the lint/typecheck command.

## Project Structure

```
src/Rocket.RiverOfNews/
├── Api/MvpApi.cs           # Minimal API endpoints
├── Data/                   # SQLite connection & migrations
├── Services/               # Feed ingestion, polling, cleanup
└── Program.cs              # App bootstrap

tests/Rocket.RiverOfNews.Tests/   # TUnit tests

db/
├── river.db                # SQLite database
└── migrations/             # SQL migration files
```

## Code Style Guidelines

### Naming & Formatting
- **PascalCase** for all identifiers (types, members, parameters, properties)
- **camelCase** for local variables (exception: loop counters `i`, `j`, `k`, `x`, `y`, `z`)
- **Tabs** for indentation
- **Opening brace on its own line**
- **File-scoped namespaces** (block-scoped only when multiple namespaces per file)

### Typing
- **Explicit typing** - avoid `var` unless type is obvious
- **Nullable enabled** - treat warnings as errors
- Prefer `IReadOnlyList<T>` for return types; accept `IEnumerable<T>` for parameters

### Modern C# (12-14)
- Use **collection expressions**: `[]` for empty, `[.. collection]` for spread
- Use **primary constructors** for services and DTOs
- Use **`record class`** with `required` members for DTOs
- Use **`sealed`** classes unless inheritance is intended
- Mark methods/properties as `static` when they don't access instance members

### Guard Helpers (parameter validation)
```csharp
ArgumentNullException.ThrowIfNull(parameter);
ArgumentException.ThrowIfNullOrWhiteSpace(text);
ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, nameof(value));
```

### Dates & Times
- Prefer **UTC everywhere**: `DateTimeOffset.UtcNow` or `DateTime.UtcNow` with `Kind.Utc`
- Persist timestamps as ISO-8601 UTC format (`"O"`): `DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)`

### Async Patterns
- Public async methods accept `CancellationToken cancellationToken`
- In API handlers, use `HttpContext.RequestAborted`
- Use `await using` for `IAsyncDisposable`
- Use `await using` for transactions

## Data Access (Dapper + SQLite)

### Connection Factory Pattern
```csharp
await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
```

### PRAGMA Settings (auto-applied)
- `PRAGMA foreign_keys=ON;`
- `PRAGMA journal_mode=WAL;`
- `PRAGMA synchronous=NORMAL;`

### SQL Conventions
- Use **raw string literals** (`"""sql"""`) for multi-line SQL
- Use **Dapper** with `CommandDefinition` for parameterized queries
- Map to **immutable DTOs** (`sealed record class` with `required` members)

## API Patterns

### Minimal API Endpoints
```csharp
public static async Task<IResult> GetItemAsync(
    string itemId,
    SqliteConnectionFactory connectionFactory,
    CancellationToken cancellationToken)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
    // ...
    return item is null
        ? Results.NotFound(new ErrorResponse("Item not found."))
        : Results.Ok(item);
}
```

### DTOs
```csharp
public sealed record ItemResponse
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? ImageUrl { get; init; }
}
```

## Testing (TUnit)

### Test Structure
```csharp
public class AddFeedApiTests
{
    [Test]
    public async Task AddFeedAsync_WithValidUrl_ReturnsCreated()
    {
        await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
        // arrange, act, assert
        await Assert.That(statusCode).IsEqualTo(StatusCodes.Status201Created);
    }
}
```

### Key Patterns
- Use `await using TestDatabaseContext` for isolated test databases
- Assert with `await Assert.That(actual).IsEqualTo(expected)`
- Tests clean up their temp database directories

### Test Database Context
Creates isolated SQLite databases in temp directories, applies migrations, and cleans up on disposal.

## Key Dependencies

| Package | Purpose |
|---------|---------|
| `Rocket.Syndication` | RSS/Atom feed parsing |
| `Rocket.OPML` | OPML import/export |
| `Dapper` | Micro-ORM for SQL |
| `Microsoft.Data.Sqlite` | SQLite connectivity |
| `TUnit` | Testing framework |

## Environment Notes

- Develop on **Windows**, deploy to **Ubuntu**
- Use forward-slash paths in config/web files
- Database path is relative to repository root: `db/river.db`

## Common Tasks

### Adding a New API Endpoint
1. Add method to `MvpApi.cs` with signature: `static async Task<IResult> MethodName(...)`
2. Register in `Program.cs`: `app.MapGet("/path", MvpApi.MethodName);`
3. Add corresponding DTOs in `MvpApi.cs`
4. Add TUnit tests in `tests/Rocket.RiverOfNews.Tests/`

### Adding a Database Migration
1. Create `NNN_descriptive_name.sql` in `db/migrations/`
2. Migrations run automatically on app startup via `SqliteDatabaseBootstrapper`

### Adding a Background Service
1. Create class inheriting `BackgroundService` in `Services/`
2. Register in `Program.cs`: `builder.Services.AddHostedService<MyService>();`
```
