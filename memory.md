# Codebase Map: Rocket.RiverOfNews

## Architecture Overview
A **River of News** RSS aggregator built with ASP.NET Core 10 Minimal APIs, Dapper + SQLite, TailwindCSS, and Datastar for reactive UI.

## Project Structure
```
src/Rocket.RiverOfNews/
├── Program.cs                          # App bootstrap, DI, endpoint routing
├── appsettings.json                    # Configuration (polling, timeout, retention)
├── Configuration/
│   └── RiverOfNewsSettings.cs          # Settings classes for configuration
├── Api/
│   ├── MvpApi.cs                       # JSON API endpoints + DTOs (kept for backward compatibility)
│   └── DatastarApi.cs                  # Datastar SSE endpoints + HTML pages
├── Data/
│   ├── SqliteConnectionFactory.cs      # Connection factory with PRAGMA setup
│   └── SqliteDatabaseBootstrapper.cs   # Migration runner
├── Datastar/
│   └── SseHelper.cs                    # Datastar SSE helper for ASP.NET Core
└── Services/
    ├── FeedIngestionService.cs         # RSS parsing, deduplication, ingestion
    ├── FeedPollingBackgroundService.cs # 1-min polling timer
    └── RetentionCleanupBackgroundService.cs # 1-hour cleanup (configurable retention)

db/
├── river.db                            # SQLite database
└── migrations/
    ├── 001_initial_schema.sql          # feeds, items, item_sources tables
    └── 002_add_items_image_url.sql     # image_url column

tests/Rocket.RiverOfNews.Tests/
├── AddFeedApiTests.cs
├── FeedIngestionServiceTests.cs
├── ItemDetailsApiTests.cs
└── RiverPageHtmlTests.cs
```

## Database Schema
- **feeds**: `id`, `url`, `normalized_url` (unique), `title`, `status` (healthy/unhealthy), `consecutive_failures`, `last_error`, `last_polled_at`, `last_success_at`
- **items**: `id`, `canonical_key` (unique, for dedup), `guid`, `url`, `canonical_url`, `image_url`, `title`, `snippet`, `published_at`, `ingested_at`
- **item_sources**: Many-to-many linking items to feeds (for dedup across feeds)

## API Endpoints

### Page Endpoints (Datastar HTML)
| Method | Path | Handler |
|--------|------|---------|
| GET | `/` | Redirect to `/river` |
| GET | `/river` | `DatastarApi.GetRiverPage` (HTML UI with Datastar) |
| GET | `/river/items/{itemId}` | `DatastarApi.GetRiverItemPage` (HTML detail) |

### Datastar SSE Endpoints
| Method | Path | Handler |
|--------|------|---------|
| GET | `/river/feeds` | `DatastarApi.GetFeedsAsync` (SSE HTML fragment) |
| GET | `/river/toggle-feed/{feedId}` | `DatastarApi.ToggleFeedAsync` (SSE signal patch) |
| GET | `/river/items` | `DatastarApi.GetItemsAsync` (SSE HTML fragment) |
| GET | `/river/clear-filters` | `DatastarApi.ClearFiltersAsync` (SSE signal patch) |
| POST | `/river/feeds` | `DatastarApi.AddFeedAsync` (SSE) |
| DELETE | `/river/feeds/{feedId}` | `DatastarApi.DeleteFeedAsync` (SSE) |
| POST | `/river/refresh` | `DatastarApi.RefreshAsync` (SSE) |
| GET | `/river/items/{itemId}/detail` | `DatastarApi.GetItemDetailAsync` (SSE signal patch) |

### JSON API Endpoints (Backward Compatible)
| Method | Path | Handler |
|--------|------|---------|
| GET | `/health` | Health check |
| GET | `/api/feeds` | `MvpApi.GetFeedsAsync` |
| POST | `/api/feeds` | `MvpApi.AddFeedAsync` |
| DELETE | `/api/feeds/{feedId}` | `MvpApi.DeleteFeedAsync` |
| POST | `/api/refresh` | `MvpApi.RefreshAsync` |
| GET | `/api/items` | `MvpApi.GetItemsAsync` (cursor pagination) |
| GET | `/api/items/{itemId}` | `MvpApi.GetItemByIdAsync` |
| GET | `/api/perf/latest-200` | `MvpApi.GetLatest200PerformanceAsync` |

## Key Patterns
- **Cursor pagination**: Base64-encoded `published_at|ingested_at|id` for stable pagination
- **URL canonicalization**: Removes fragments, utm_*, fbclid, gclid params
- **Deduplication**: Uses `canonical_key` (guid, canonical URL, or SHA256 hash of content)
- **Feed status tracking**: Healthy/unhealthy with configurable exponential backoff
- **Image extraction priority**: Media content → Enclosures → HTML img tags
- **Snippet truncation**: 1000 char limit with "..." suffix
- **Configuration**: All timing/threshold values configurable via `appsettings.json`

## Configuration (appsettings.json)
```json
{
  "RiverOfNews": {
    "Feed": {
      "PollingIntervalMinutes": 15,
      "RequestTimeoutSeconds": 10,
      "RetentionDays": 30,
      "UnhealthyThreshold": 3,
      "BackoffLevel1Minutes": 5,
      "BackoffLevel2Minutes": 15,
      "BackoffLevel3Minutes": 60
    }
  }
}
```

## Dependencies
- `Rocket.Syndication` (0.1.0) - RSS/Atom parsing
- `Rocket.OPML` (0.1.0-alpha) - OPML import/export (unused yet)
- `Dapper` (2.1.66) - Micro-ORM
- `Microsoft.Data.Sqlite` (10.0.0) - SQLite
- `TUnit` - Testing framework
- **Datastar** (1.0.0-RC.7) - Reactive hypermedia framework (loaded via CDN)

## File Details

### Program.cs
- Bootstrap: Creates WebApplication, configures DI
- Binds `RiverOfNewsSettings` from configuration section
- Database path: `db/river.db` relative to repository root
- Services registered:
  - `RiverOfNewsSettings` (singleton)
  - `SqliteConnectionFactory` (singleton)
  - `ISyndicationClient` (via `AddSyndicationClient`, uses `RequestTimeoutSeconds`)
  - `FeedIngestionService` (scoped)
  - `FeedPollingBackgroundService` (hosted)
  - `RetentionCleanupBackgroundService` (hosted)

### Configuration/RiverOfNewsSettings.cs
- `RiverOfNewsSettings` - Root settings class
- `FeedSettings` - Nested class with:
  - `PollingIntervalMinutes` (default: 15)
  - `RequestTimeoutSeconds` (default: 10)
  - `RetentionDays` (default: 30)
  - `UnhealthyThreshold` (default: 3)
  - `BackoffLevel1Minutes`, `BackoffLevel2Minutes`, `BackoffLevel3Minutes` (default: 5, 15, 60)

### Api/MvpApi.cs
Static class containing JSON API handlers and DTOs (kept for backward compatibility).

**Page Handlers (deprecated, use DatastarApi):**
- `GetRiverPage()` - Returns full HTML page with embedded JavaScript (legacy)
- `GetRiverItemPage(itemId)` - Returns item detail HTML page (legacy)

**API Handlers:**
- `GetFeedsAsync` - Lists all feeds ordered by title
- `AddFeedAsync` - Creates feed, normalizes URL, handles duplicates (409)
- `DeleteFeedAsync` - Deletes feed by ID
- `RefreshAsync` - Triggers `FeedIngestionService.RefreshAllFeedsAsync`
- `GetItemsAsync` - Paginated items with optional filters (feed_ids, start_date, end_date)
- `GetItemByIdAsync` - Single item detail
- `GetLatest200PerformanceAsync` - Performance benchmark endpoint

**DTOs:**
- `AddFeedRequest`, `FeedResponse`, `RiverItemResponse`, `RiverItemDetailResponse`
- `RiverQueryResponse`, `ErrorResponse`, `Latest200PerformanceResponse`

### Api/DatastarApi.cs
Static class containing Datastar SSE handlers for reactive UI.

**Page Handlers:**
- `GetRiverPage()` - Returns HTML page with Datastar attributes, no JavaScript
- `GetRiverItemPage(itemId)` - Returns item detail HTML with Datastar bindings

**SSE Handlers:**
- `GetFeedsAsync` - Returns HTML fragment for feed list
- `ToggleFeedAsync` - Patches signals to toggle feed selection
- `GetItemsAsync` - Returns HTML fragments for items (supports append mode)
- `ClearFiltersAsync` - Resets filter signals and reloads data
- `AddFeedAsync` - Adds feed, patches signals, triggers reload
- `DeleteFeedAsync` - Deletes feed, patches signals, triggers reload
- `RefreshAsync` - Refreshes all feeds, patches status signal
- `GetItemDetailAsync` - Patches signals with item detail data

**Key Patterns:**
- Uses `SseHelper` for SSE response streaming
- `data-on:click="@post('/path')"` for actions
- `data-bind:value="signal"` for form inputs
- `data-text="$signal"` for text content
- `data-show="condition"` for conditional visibility
- Signals prefixed with `_` are local-only (not sent to backend)

### Datastar/SseHelper.cs
Helper class for Datastar SSE responses.

**Methods:**
- `StartAsync()` - Sets up SSE response headers
- `PatchElementsAsync()` - Sends `datastar-patch-elements` event
- `PatchSignalsAsync()` - Sends `datastar-patch-signals` event

**Extensions:**
- `CreateSseHelper(HttpResponse)` - Creates helper from response
- `ReadSignalsAsync(HttpRequest)` - Reads signals from request body

### Data/SqliteConnectionFactory.cs
- Opens connections with PRAGMA settings: `foreign_keys=ON`, `journal_mode=WAL`, `synchronous=NORMAL`
- Uses `SqliteCacheMode.Shared`

### Data/SqliteDatabaseBootstrapper.cs
- Creates database directory if needed
- Maintains `__migrations` table for tracking applied migrations
- Runs migrations in sorted order within transactions

### Services/FeedIngestionService.cs
Core feed processing logic. Uses `FeedSettings` for configuration.

**Key Methods:**
- `RefreshAllFeedsAsync()` - Forces refresh of all feeds
- `RefreshDueFeedsAsync()` - Only refreshes feeds due for polling
- `IsFeedDue()` - Uses configurable polling interval and backoff levels
- `IngestFeedItemsAsync()` - Deduplicates and inserts items
- `BuildCanonicalKey()` - Creates unique key: `guid:`, `url:`, or `unique:<sha256>`
- `CanonicalizeArticleUrl()` - Normalizes URLs, strips tracking params
- `BuildSnippet()` - HTML tag removal, 1000 char truncation
- `BuildImageUrl()` - Priority: media→enclosure→HTML img src

**Regex:**
- `HtmlRegex` - Strips HTML tags
- `HtmlImageSrcRegex` - Extracts image URLs from HTML

### Services/FeedPollingBackgroundService.cs
- Runs `RefreshDueFeedsAsync` on startup and every 1 minute via `PeriodicTimer`

### Services/RetentionCleanupBackgroundService.cs
- Uses `RetentionDays` from configuration
- Deletes items where `published_at < now - RetentionDays`
- Runs on startup and every 1 hour

### Migrations

**001_initial_schema.sql:**
```sql
feeds: id, url, normalized_url, title, status, consecutive_failures, last_error, timestamps
items: id, canonical_key, guid, url, canonical_url, title, snippet, timestamps
item_sources: item_id, feed_id, source_item_guid, source_item_url, first_seen_at
Indexes: ux_item_sources_item_feed_guid, ix_items_latest_cursor, ix_items_published_at, ix_item_sources_feed_item
```

**002_add_items_image_url.sql:**
```sql
ALTER TABLE items ADD COLUMN image_url TEXT;
```

## Testing Patterns

Tests use `TestDatabaseContext` helper that:
1. Creates temp directory with unique GUID
2. Initializes fresh SQLite database with migrations
3. Implements `IAsyncDisposable` for cleanup

**Test Classes:**
- `AddFeedApiTests` - Feed creation, duplicate handling, URL validation
- `ItemDetailsApiTests` - Item retrieval, not found handling
- `RiverPageHtmlTests` - HTML output verification (now tests DatastarApi)
- `FeedIngestionServiceTests` - Image priority, snippet truncation (uses `StubSyndicationClient`, passes default `RiverOfNewsSettings`)

## Build Commands
```bash
dotnet build Rocket.RiverOfNews.slnx                              # Build (warnings as errors)
dotnet test --project tests/Rocket.RiverOfNews.Tests              # Run all tests
dotnet run --project src/Rocket.RiverOfNews                       # Run application
```
