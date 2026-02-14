# Rocket.RiverOfNews - Functional Requirements Specification

## 1) Product Goal
Build a web-based "River of News" RSS aggregator that presents posts from multiple feeds in a single chronological stream for desktop web users.

## 2) MVP Scope
The MVP includes feed ingestion, aggregation, deduplication, filtering, and a performant river view for a single local user.

## 3) Functional Requirements (MVP)

### 3.1 Feed Management
- Users can manually add RSS/Atom feed URLs.
- Users can remove existing feeds.
- The system validates feed URLs and stores accepted feeds.

### 3.2 Feed Refresh and Ingestion
- The system automatically polls feeds on a fixed interval.
- Users can trigger a manual refresh of all feeds.
- On successful fetch, new items are parsed and added to the aggregated item store.

### 3.3 River View
- The default ordering is newest-first across all feeds.
- Each item in the river shows:
  - title
  - source/feed name
  - published time
  - short snippet
- Clicking an item should open the original article URL.

### 3.4 Read State
- No read/unread tracking is required for MVP.

### 3.5 Deduplication
- Duplicate articles from different feeds should be merged into one canonical item.
- The canonical item should retain references to all source feeds that contained it.

### 3.6 Filtering
- Users can filter river items by source/feed.
- Users can filter river items by date range.

### 3.7 Retention
- Articles are retained for 30 days by default.
- Items older than 30 days are eligible for cleanup.

### 3.8 Feed Failure Handling
- If a feed repeatedly fails to fetch, mark it as unhealthy.
- Preserve previously fetched items from unhealthy feeds.
- Show retry/error status to the user for unhealthy feeds.

### 3.9 User Model
- MVP is a single-user local app.
- No authentication or account system is required.

## 4) Non-Functional Requirements (MVP)
- Platform target: desktop web browsers first.
- Performance target: load the latest 200 river items in under 2 seconds on typical broadband.

## 5) Out of Scope (MVP)
- OPML import/export.
- Feed auto-discovery from arbitrary site URLs.
- User accounts, roles, or OAuth.
- Advanced read-state synchronization.
- Mobile-native apps.

## 6) Implementation Backlog (MVP)

### Epic A: Feed Source Management
**Story A1: Add feed URL**
- As a user, I can add an RSS/Atom URL so its items appear in the river.
- Acceptance criteria:
  - Valid feed URL can be submitted from UI.
  - Duplicate feed URLs are rejected with a clear message.
  - Newly added valid feed is persisted and appears in feed list.

**Story A2: Remove feed URL**
- As a user, I can remove a feed I no longer want.
- Acceptance criteria:
  - Feed can be removed from feed list.
  - Feed is no longer polled after removal.
  - Previously aggregated items remain available until retention cleanup.

### Epic B: Ingestion and Refresh Pipeline
**Story B1: Scheduled refresh**
- As a user, feeds refresh automatically on a fixed interval.
- Acceptance criteria:
  - Polling runs at configured fixed interval.
  - Successful polls ingest only new items.
  - Last refresh timestamp and result are tracked per feed.

**Story B2: Manual refresh**
- As a user, I can force an immediate refresh.
- Acceptance criteria:
  - Manual refresh action is available in UI.
  - Action triggers fetch for all active feeds.
  - UI shows in-progress and completion/failure state.

### Epic C: Aggregation and Deduplication
**Story C1: Canonical item merging**
- As a user, duplicate articles are not repeated in the river.
- Acceptance criteria:
  - Items with matching canonical identity are merged into one record.
  - Canonical item stores all source feed references.
  - River shows one entry for merged duplicates.

### Epic D: River Experience
**Story D1: Chronological river rendering**
- As a user, I can see a unified newest-first stream.
- Acceptance criteria:
  - River sorts by published timestamp descending.
  - Default list loads latest 200 items.
  - Each item shows title, source, time, and snippet.

**Story D2: Open original article**
- As a user, I can open the source article.
- Acceptance criteria:
  - Clicking item opens original URL.
  - Broken/missing URL is surfaced with a visible error state.

### Epic E: Filtering
**Story E1: Source filter**
- As a user, I can filter river items by feed/source.
- Acceptance criteria:
  - One or more sources can be selected.
  - Results update to match selected sources.
  - Clearing filter returns full river list.

**Story E2: Date range filter**
- As a user, I can filter by publish date range.
- Acceptance criteria:
  - Start and end dates can be set.
  - Results include only items in selected range.
  - Invalid range (end before start) is blocked with message.

### Epic F: Feed Health and Reliability
**Story F1: Unhealthy feed status**
- As a user, I can see when a feed is failing.
- Acceptance criteria:
  - Consecutive failures mark feed as unhealthy.
  - Feed status includes last error and retry indicator.
  - Existing items from that feed remain visible.

### Epic G: Data Retention and Cleanup
**Story G1: 30-day retention enforcement**
- As a user, old data is cleaned automatically.
- Acceptance criteria:
  - Items older than 30 days are removed by cleanup job.
  - Cleanup runs without blocking normal reads/refresh.
  - Retention boundary is based on published timestamp.

### Epic H: Performance Baseline
**Story H1: River load performance target**
- As a user, river loads quickly with recent items.
- Acceptance criteria:
  - Latest 200 items load in under 2 seconds on typical broadband.
  - Query path for latest items is indexed/optimized.
  - Performance check is documented and repeatable.

## 7) Pre-Implementation Decisions (Locked)

### 7.1 Deduplication Identity Rules
- Deduplication key priority: `guid` (if present and non-empty) -> canonicalized article URL -> no merge.
- URL canonicalization removes fragment and known tracking query params (`utm_*`, `fbclid`, `gclid`) before matching.
- If neither `guid` nor URL is usable, the item is treated as unique for MVP (no fuzzy title-based merge).
- Canonical merged item is the earliest ingested matching item; later duplicates attach as additional sources.

### 7.2 Polling, Retry, and Unhealthy Threshold
- Default polling interval: every 15 minutes (configurable).
- Feed request timeout: 10 seconds per feed fetch.
- A feed is marked unhealthy after 3 consecutive failed fetch attempts.
- Retry backoff for unhealthy feeds: 5m -> 15m -> 60m (cap at 60m) until success.
- Any successful fetch resets failure count and returns feed status to healthy.
- Manual refresh attempts all feeds immediately, including unhealthy feeds.

### 7.3 River Sorting and Pagination Behavior
- River ordering is stable and deterministic: `published_at DESC`, then `ingested_at DESC`, then `canonical_item_id DESC`.
- Default river query returns latest 200 items.
- Additional loading uses cursor-based pagination (not offset-based) to avoid duplicates/skips during concurrent ingestion.
- Date range filtering is inclusive (`start_date <= published_at <= end_date`).

### 7.4 API Contract (MVP)
- `GET /api/feeds` -> list feeds with health metadata.
- `POST /api/feeds` -> add a feed URL; rejects duplicates by normalized URL.
- `DELETE /api/feeds/{feed_id}` -> remove feed from polling.
- `POST /api/refresh` -> trigger immediate refresh of all active feeds.
- `GET /api/items` -> river query with optional `feed_ids`, `start_date`, `end_date`, `limit`, `cursor`.

### 7.5 Data Model Contract (MVP)
- `feeds`: `id`, `url`, `normalized_url` (unique), `title`, `status`, `consecutive_failures`, `last_error`, `last_polled_at`, `last_success_at`, timestamps.
- `items`: `id`, `canonical_key`, `guid`, `url`, `canonical_url`, `title`, `snippet`, `published_at`, `ingested_at`, timestamps.
- `item_sources`: `item_id`, `feed_id`, `source_item_guid`, `source_item_url`, `first_seen_at`; unique on (`item_id`, `feed_id`, `source_item_guid`).
- Retention cleanup removes rows from `items` older than 30 days by `published_at` and cascades related `item_sources`.

## 8) Prioritized Implementation Sequence

1. Data layer foundation
   - Create `feeds`, `items`, and `item_sources` tables with required uniqueness and query indexes.
   - Ensure sort/query path supports latest-200 and cursor pagination.

2. Core API surface
   - Implement `GET/POST/DELETE /api/feeds`.
   - Implement `POST /api/refresh`.
   - Implement `GET /api/items` with `feed_ids`, date range, `limit`, and `cursor`.

3. Ingestion and dedup pipeline
   - Build fetch/parse flow for RSS/Atom items.
   - Apply canonical URL normalization and dedup key priority (`guid` -> canonical URL -> unique).
   - Merge duplicates into canonical items and attach source references.

4. Scheduler and reliability controls
   - Add 15-minute polling scheduler and 10-second per-feed timeout.
   - Track consecutive failures; mark unhealthy at 3 failures.
   - Apply unhealthy retry backoff ladder (5m -> 15m -> 60m), reset on success.

5. River UI and interaction
   - Render newest-first river with latest 200 default load.
   - Support cursor-based load-more behavior.
   - Add source filter, inclusive date-range filter, and manual refresh status UX.
   - Show unhealthy feed status with last error/retry indicator.

6. Retention and performance validation
   - Add retention cleanup job (30-day boundary by `published_at`) with cascading source cleanup.
   - Validate latest 200 load target (<2s) and document repeatable measurement steps.
