using System.Globalization;
using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Rocket.RiverOfNews.Data;
using Rocket.RiverOfNews.Services;

namespace Rocket.RiverOfNews.Api;

public static class MvpApi
{
	public static IResult GetRiverPage()
	{
		const string Html = """
			<!doctype html>
			<html lang="en">
			<head>
				<meta charset="utf-8">
				<meta name="viewport" content="width=device-width, initial-scale=1">
				<title>Rocket River of News</title>
				<script src="https://cdn.jsdelivr.net/npm/@tailwindcss/browser@4"></script>
			</head>
			<body class="bg-slate-950 text-slate-100">
				<main class="mx-auto max-w-6xl p-4 md:p-8">
					<header class="mb-6 flex flex-wrap items-center justify-between gap-3">
						<div>
							<h1 class="text-2xl font-semibold">River of News</h1>
							<p class="text-sm text-slate-400">Unified newest-first feed stream</p>
						</div>
						<div class="flex items-center gap-2">
							<button id="refreshButton" class="rounded bg-sky-600 px-4 py-2 text-sm font-semibold hover:bg-sky-500">Refresh now</button>
							<span id="refreshStatus" class="text-xs text-slate-400"></span>
						</div>
					</header>

					<section class="mb-6 rounded border border-slate-800 bg-slate-900 p-4">
						<div class="mb-2 text-sm font-semibold">Add feed</div>
						<div class="grid gap-3 md:grid-cols-3">
							<label class="text-sm md:col-span-2">
								<span class="mb-1 block text-slate-300">Feed URL</span>
								<input id="addFeedUrl" type="url" placeholder="https://example.com/feed.xml" class="w-full rounded border border-slate-700 bg-slate-950 px-2 py-2 text-slate-100">
							</label>
							<label class="text-sm">
								<span class="mb-1 block text-slate-300">Title (optional)</span>
								<input id="addFeedTitle" type="text" placeholder="My feed" class="w-full rounded border border-slate-700 bg-slate-950 px-2 py-2 text-slate-100">
							</label>
						</div>
						<div class="mt-3 flex items-center gap-3">
							<button id="addFeedButton" class="rounded bg-emerald-600 px-3 py-2 text-sm font-semibold hover:bg-emerald-500">Add feed</button>
							<p id="addFeedStatus" class="text-sm text-slate-400"></p>
						</div>
					</section>

					<section class="mb-6 rounded border border-slate-800 bg-slate-900 p-4">
						<div class="mb-2 text-sm font-semibold">Filters</div>
						<div class="grid gap-3 md:grid-cols-3">
							<label class="text-sm">
								<span class="mb-1 block text-slate-300">Start date (UTC)</span>
								<input id="startDate" type="datetime-local" class="w-full rounded border border-slate-700 bg-slate-950 px-2 py-2 text-slate-100">
							</label>
							<label class="text-sm">
								<span class="mb-1 block text-slate-300">End date (UTC)</span>
								<input id="endDate" type="datetime-local" class="w-full rounded border border-slate-700 bg-slate-950 px-2 py-2 text-slate-100">
							</label>
							<div class="text-sm">
								<span class="mb-1 block text-slate-300">Sources</span>
								<div id="sourceFilters" class="max-h-36 overflow-auto rounded border border-slate-700 bg-slate-950 p-2"></div>
							</div>
						</div>
						<div class="mt-3 flex gap-2">
							<button id="applyFilters" class="rounded bg-slate-700 px-3 py-2 text-sm font-semibold hover:bg-slate-600">Apply filters</button>
							<button id="clearFilters" class="rounded border border-slate-600 px-3 py-2 text-sm hover:bg-slate-800">Clear</button>
						</div>
						<p id="filterError" class="mt-2 text-sm text-rose-400"></p>
					</section>

					<section id="items" class="space-y-3"></section>
					<div class="mt-4 flex items-center justify-between">
						<span id="itemsStatus" class="text-sm text-slate-400"></span>
						<button id="loadMore" class="rounded border border-slate-600 px-3 py-2 text-sm hover:bg-slate-800">Load more</button>
					</div>
				</main>

				<script>
					const State = {
						cursor: null,
						selectedFeedIds: new Set(),
						startDate: "",
						endDate: "",
						loading: false
					};

					const ItemsContainer = document.getElementById("items");
					const SourceFiltersContainer = document.getElementById("sourceFilters");
					const ItemsStatus = document.getElementById("itemsStatus");
					const FilterError = document.getElementById("filterError");
					const RefreshStatus = document.getElementById("refreshStatus");
					const LoadMoreButton = document.getElementById("loadMore");
					const AddFeedButton = document.getElementById("addFeedButton");
					const AddFeedStatus = document.getElementById("addFeedStatus");
					const AddFeedUrlInput = document.getElementById("addFeedUrl");
					const AddFeedTitleInput = document.getElementById("addFeedTitle");

					function toIsoUtc(datetimeLocalValue) {
						if (!datetimeLocalValue) return "";
						return new Date(datetimeLocalValue).toISOString();
					}

					function renderItems(items, replace) {
						if (replace) ItemsContainer.innerHTML = "";
						for (const item of items) {
							const article = document.createElement("article");
							article.className = "rounded border border-slate-800 bg-slate-900 p-4";
							const sourceText = item.sourceNames || "Unknown source";
							const link = item.canonicalUrl || "#";
							article.innerHTML = `
								<div class="mb-1 text-xs text-slate-400">${sourceText}</div>
								<h2 class="mb-2 text-lg font-semibold">${item.title}</h2>
								<div class="mb-2 text-xs text-slate-400">${new Date(item.publishedAt).toUTCString()}</div>
								<p class="mb-3 text-sm text-slate-300">${item.snippet || ""}</p>
								<a class="text-sm text-sky-400 hover:text-sky-300" href="${link}" target="_blank" rel="noreferrer">Open article</a>
							`;
							ItemsContainer.appendChild(article);
						}
					}

					function setAddFeedStatus(message, isError) {
						AddFeedStatus.textContent = message || "";
						AddFeedStatus.classList.toggle("text-rose-400", !!isError);
						AddFeedStatus.classList.toggle("text-emerald-400", !!message && !isError);
						AddFeedStatus.classList.toggle("text-slate-400", !message);
					}

					async function loadFeeds() {
						const response = await fetch("/api/feeds");
						const feeds = await response.json();
						SourceFiltersContainer.innerHTML = "";
						if (!feeds.length) {
							SourceFiltersContainer.innerHTML = "<div class='text-xs text-slate-500'>No feeds added yet.</div>";
							return;
						}
						for (const feed of feeds) {
							const id = `feed_${feed.id}`;
							const wrapper = document.createElement("label");
							wrapper.className = "mb-1 flex items-center gap-2 text-xs";
							wrapper.innerHTML = `
								<input id="${id}" type="checkbox" value="${feed.id}" class="accent-sky-500">
								<span>${feed.title} <span class="text-slate-500">(${feed.status})</span></span>
							`;
							wrapper.querySelector("input").addEventListener("change", (event) => {
								const checked = event.target.checked;
								if (checked) State.selectedFeedIds.add(feed.id);
								else State.selectedFeedIds.delete(feed.id);
							});
							SourceFiltersContainer.appendChild(wrapper);
						}
					}

					async function loadItems(replace) {
						if (State.loading) return;
						State.loading = true;
						FilterError.textContent = "";
						try {
							const params = new URLSearchParams();
							if (State.selectedFeedIds.size) params.set("feed_ids", [...State.selectedFeedIds].join(","));
							if (State.startDate) params.set("start_date", State.startDate);
							if (State.endDate) params.set("end_date", State.endDate);
							if (State.cursor) params.set("cursor", State.cursor);
							params.set("limit", "50");

							const response = await fetch(`/api/items?${params}`);
							const payload = await response.json();
							if (!response.ok) {
								FilterError.textContent = payload.message || "Failed to load items.";
								return;
							}

							renderItems(payload.items, replace);
							State.cursor = payload.nextCursor || null;
							LoadMoreButton.disabled = !State.cursor;
							LoadMoreButton.classList.toggle("opacity-50", !State.cursor);
							ItemsStatus.textContent = `Loaded ${replace ? payload.items.length : ItemsContainer.children.length} items`;
						} finally {
							State.loading = false;
						}
					}

					AddFeedButton.addEventListener("click", async () => {
						const url = AddFeedUrlInput.value.trim();
						const title = AddFeedTitleInput.value.trim();
						if (!url) {
							setAddFeedStatus("Feed URL is required.", true);
							return;
						}

						AddFeedButton.disabled = true;
						AddFeedButton.classList.add("opacity-50");
						setAddFeedStatus("Adding feed...", false);
						try {
							const response = await fetch("/api/feeds", {
								method: "POST",
								headers: { "Content-Type": "application/json" },
								body: JSON.stringify({ url, title: title || null })
							});
							let payload = {};
							try {
								payload = await response.json();
							} catch {
								payload = {};
							}

							if (!response.ok) {
								setAddFeedStatus(payload.message || "Failed to add feed.", true);
								return;
							}

							AddFeedUrlInput.value = "";
							AddFeedTitleInput.value = "";
							setAddFeedStatus("Feed added.", false);
							await loadFeeds();
							State.cursor = null;
							await loadItems(true);
						} catch {
							setAddFeedStatus("Network error while adding feed.", true);
						} finally {
							AddFeedButton.disabled = false;
							AddFeedButton.classList.remove("opacity-50");
						}
					});

					document.getElementById("applyFilters").addEventListener("click", () => {
						const startDate = document.getElementById("startDate").value;
						const endDate = document.getElementById("endDate").value;
						State.startDate = toIsoUtc(startDate);
						State.endDate = toIsoUtc(endDate);
						if (State.startDate && State.endDate && new Date(State.endDate) < new Date(State.startDate)) {
							FilterError.textContent = "Invalid date range. End date must be on or after start date.";
							return;
						}
						State.cursor = null;
						loadItems(true);
					});

					document.getElementById("clearFilters").addEventListener("click", () => {
						document.getElementById("startDate").value = "";
						document.getElementById("endDate").value = "";
						State.startDate = "";
						State.endDate = "";
						State.selectedFeedIds.clear();
						for (const input of SourceFiltersContainer.querySelectorAll("input[type='checkbox']")) input.checked = false;
						State.cursor = null;
						FilterError.textContent = "";
						loadItems(true);
					});

					LoadMoreButton.addEventListener("click", () => loadItems(false));

					document.getElementById("refreshButton").addEventListener("click", async () => {
						RefreshStatus.textContent = "Refreshing...";
						const response = await fetch("/api/refresh", { method: "POST" });
						const payload = await response.json();
						RefreshStatus.textContent = `Done: ${payload.successFeedCount} success, ${payload.failedFeedCount} failed`;
						await loadFeeds();
						State.cursor = null;
						await loadItems(true);
					});

					(async () => {
						await loadFeeds();
						await loadItems(true);
					})();
				</script>
			</body>
			</html>
			""";

		return Results.Content(Html, "text/html; charset=utf-8");
	}

	public static async Task<IResult> GetFeedsAsync(
		SqliteConnectionFactory ConnectionFactory,
		CancellationToken CancellationToken)
	{
		const string Sql = """
			SELECT
				id AS Id,
				url AS Url,
				normalized_url AS NormalizedUrl,
				title AS Title,
				status AS Status,
				consecutive_failures AS ConsecutiveFailures,
				last_error AS LastError,
				last_polled_at AS LastPolledAt,
				last_success_at AS LastSuccessAt
			FROM feeds
			ORDER BY title COLLATE NOCASE, normalized_url COLLATE NOCASE;
			""";

		await using SqliteConnection Connection = await ConnectionFactory.OpenConnectionAsync(CancellationToken);
		IReadOnlyList<FeedResponse> Feeds = (await Connection.QueryAsync<FeedResponse>(new CommandDefinition(Sql, cancellationToken: CancellationToken))).AsList();
		return Results.Ok(Feeds);
	}

	public static async Task<IResult> AddFeedAsync(
		AddFeedRequest Request,
		SqliteConnectionFactory ConnectionFactory,
		CancellationToken CancellationToken)
	{
		ArgumentNullException.ThrowIfNull(Request);
		ArgumentException.ThrowIfNullOrWhiteSpace(Request.Url);

		string NormalizedUrl;
		try
		{
			NormalizedUrl = NormalizeUrl(Request.Url);
		}
		catch (UriFormatException)
		{
			return Results.BadRequest(new ErrorResponse("Invalid feed URL."));
		}

		string FeedId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
		string Now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

		const string Sql = """
			INSERT INTO feeds (
				id, url, normalized_url, title, status, consecutive_failures, created_at, updated_at
			)
			VALUES (
				@Id, @Url, @NormalizedUrl, @Title, 'healthy', 0, @Now, @Now
			);
			""";

		FeedResponse Feed = new()
		{
			Id = FeedId,
			Url = Request.Url,
			NormalizedUrl = NormalizedUrl,
			Title = Request.Title ?? NormalizedUrl,
			Status = "healthy",
			ConsecutiveFailures = 0,
			LastError = null,
			LastPolledAt = null,
			LastSuccessAt = null
		};

		try
		{
			await using SqliteConnection Connection = await ConnectionFactory.OpenConnectionAsync(CancellationToken);
			await Connection.ExecuteAsync(
				new CommandDefinition(
					Sql,
					new
					{
						Id = Feed.Id,
						Url = Feed.Url,
						NormalizedUrl = Feed.NormalizedUrl,
						Title = Feed.Title,
						Now
					},
					cancellationToken: CancellationToken));
		}
		catch (SqliteException Exception) when (Exception.SqliteErrorCode == 19)
		{
			return Results.Conflict(new ErrorResponse("Feed URL already exists."));
		}

		return Results.Created($"/api/feeds/{Feed.Id}", Feed);
	}

	public static async Task<IResult> DeleteFeedAsync(
		string FeedId,
		SqliteConnectionFactory ConnectionFactory,
		CancellationToken CancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(FeedId);

		const string Sql = "DELETE FROM feeds WHERE id = @FeedId;";
		await using SqliteConnection Connection = await ConnectionFactory.OpenConnectionAsync(CancellationToken);
		int RowsAffected = await Connection.ExecuteAsync(new CommandDefinition(Sql, new { FeedId }, cancellationToken: CancellationToken));
		return RowsAffected > 0 ? Results.NoContent() : Results.NotFound(new ErrorResponse("Feed not found."));
	}

	public static async Task<IResult> RefreshAsync(
		FeedIngestionService FeedIngestionService,
		CancellationToken CancellationToken)
	{
		RefreshResult RefreshResult = await FeedIngestionService.RefreshAllFeedsAsync(CancellationToken);
		return Results.Ok(RefreshResult);
	}

	public static async Task<IResult> GetItemsAsync(
		HttpRequest Request,
		SqliteConnectionFactory ConnectionFactory,
		CancellationToken CancellationToken)
	{
		string? FeedIdsRaw = Request.Query["feed_ids"];
		string? StartDateRaw = Request.Query["start_date"];
		string? EndDateRaw = Request.Query["end_date"];
		string? CursorRaw = Request.Query["cursor"];
		string? LimitRaw = Request.Query["limit"];

		string[] FeedIds = ParseFeedIds(FeedIdsRaw);
		if (!TryParseIsoDate(StartDateRaw, out DateTimeOffset? StartDateUtc))
		{
			return Results.BadRequest(new ErrorResponse("Invalid start_date; expected ISO-8601 UTC."));
		}

		if (!TryParseIsoDate(EndDateRaw, out DateTimeOffset? EndDateUtc))
		{
			return Results.BadRequest(new ErrorResponse("Invalid end_date; expected ISO-8601 UTC."));
		}

		if (StartDateUtc.HasValue && EndDateUtc.HasValue && EndDateUtc.Value < StartDateUtc.Value)
		{
			return Results.BadRequest(new ErrorResponse("Invalid date range; end_date must be on or after start_date."));
		}

		int Limit = 200;
		if (!string.IsNullOrWhiteSpace(LimitRaw) && (!int.TryParse(LimitRaw, NumberStyles.None, CultureInfo.InvariantCulture, out Limit) || Limit <= 0 || Limit > 200))
		{
			return Results.BadRequest(new ErrorResponse("Invalid limit; expected integer in range 1..200."));
		}

		if (!TryParseCursor(CursorRaw, out CursorParts? Cursor))
		{
			return Results.BadRequest(new ErrorResponse("Invalid cursor."));
		}

		const string Sql = """
			SELECT
				i.id AS Id,
				i.title AS Title,
				i.canonical_url AS CanonicalUrl,
				i.snippet AS Snippet,
				i.published_at AS PublishedAt,
				i.ingested_at AS IngestedAt,
				(
					SELECT group_concat(DISTINCT COALESCE(f.title, f.normalized_url))
					FROM item_sources s
					INNER JOIN feeds f ON f.id = s.feed_id
					WHERE s.item_id = i.id
				) AS SourceNames
			FROM items i
			WHERE
				(@StartDate IS NULL OR i.published_at >= @StartDate)
				AND (@EndDate IS NULL OR i.published_at <= @EndDate)
				AND (
					@HasCursor = 0
					OR i.published_at < @CursorPublishedAt
					OR (i.published_at = @CursorPublishedAt AND i.ingested_at < @CursorIngestedAt)
					OR (i.published_at = @CursorPublishedAt AND i.ingested_at = @CursorIngestedAt AND i.id < @CursorId)
				)
				AND (
					@HasFeedFilter = 0
					OR EXISTS (
						SELECT 1
						FROM item_sources s
						WHERE s.item_id = i.id
						AND s.feed_id IN @FeedIds
					)
				)
			ORDER BY i.published_at DESC, i.ingested_at DESC, i.id DESC
			LIMIT @FetchLimit;
			""";

		int FetchLimit = Limit + 1;
		string? StartDate = StartDateUtc?.ToString("O", CultureInfo.InvariantCulture);
		string? EndDate = EndDateUtc?.ToString("O", CultureInfo.InvariantCulture);
		await using SqliteConnection Connection = await ConnectionFactory.OpenConnectionAsync(CancellationToken);
		IReadOnlyList<RiverItemResponse> QueriedItems = (await Connection.QueryAsync<RiverItemResponse>(
			new CommandDefinition(
				Sql,
				new
				{
					StartDate,
					EndDate,
					HasCursor = Cursor is not null ? 1 : 0,
					CursorPublishedAt = Cursor?.PublishedAt,
					CursorIngestedAt = Cursor?.IngestedAt,
					CursorId = Cursor?.Id,
					HasFeedFilter = FeedIds.Length > 0 ? 1 : 0,
					FeedIds,
					FetchLimit
				},
				cancellationToken: CancellationToken))).AsList();

		IReadOnlyList<RiverItemResponse> Items = QueriedItems.Count > Limit
			? QueriedItems.Take(Limit).ToList()
			: QueriedItems;

		string? NextCursor = null;
		if (QueriedItems.Count > Limit)
		{
			RiverItemResponse LastItem = Items[^1];
			NextCursor = EncodeCursor(LastItem.PublishedAt, LastItem.IngestedAt, LastItem.Id);
		}

		return Results.Ok(new RiverQueryResponse
		{
			Items = Items,
			NextCursor = NextCursor
		});
	}

	public static async Task<IResult> GetLatest200PerformanceAsync(
		SqliteConnectionFactory ConnectionFactory,
		CancellationToken CancellationToken)
	{
		const string Sql = """
			SELECT
				i.id AS Id,
				i.title AS Title,
				i.canonical_url AS CanonicalUrl,
				i.snippet AS Snippet,
				i.published_at AS PublishedAt,
				i.ingested_at AS IngestedAt,
				(
					SELECT group_concat(DISTINCT COALESCE(f.title, f.normalized_url))
					FROM item_sources s
					INNER JOIN feeds f ON f.id = s.feed_id
					WHERE s.item_id = i.id
				) AS SourceNames
			FROM items i
			ORDER BY i.published_at DESC, i.ingested_at DESC, i.id DESC
			LIMIT 200;
			""";

		await using SqliteConnection Connection = await ConnectionFactory.OpenConnectionAsync(CancellationToken);
		Stopwatch Stopwatch = Stopwatch.StartNew();
		IReadOnlyList<RiverItemResponse> Items = (await Connection.QueryAsync<RiverItemResponse>(
			new CommandDefinition(Sql, cancellationToken: CancellationToken))).AsList();
		Stopwatch.Stop();

		return Results.Ok(new Latest200PerformanceResponse
		{
			ItemCount = Items.Count,
			DurationMilliseconds = Stopwatch.ElapsedMilliseconds,
			ThresholdMilliseconds = 2000,
			MeetsTarget = Stopwatch.ElapsedMilliseconds < 2000
		});
	}

	private static string NormalizeUrl(string Url)
	{
		Uri ParsedUri = new(Url, UriKind.Absolute);
		if (!string.Equals(ParsedUri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(ParsedUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
		{
			throw new UriFormatException("Only HTTP and HTTPS URLs are supported.");
		}

		UriBuilder Builder = new(ParsedUri)
		{
			Fragment = string.Empty
		};

		string Scheme = Builder.Scheme.ToLowerInvariant();
		string Host = Builder.Host.ToLowerInvariant();
		string Port = Builder.Port > 0 && !Builder.Uri.IsDefaultPort
			? $":{Builder.Port.ToString(CultureInfo.InvariantCulture)}"
			: string.Empty;
		string Path = Builder.Path.TrimEnd('/');
		if (string.IsNullOrWhiteSpace(Path))
		{
			Path = "/";
		}

		string Query = Builder.Query;
		return $"{Scheme}://{Host}{Port}{Path}{Query}";
	}

	private static string[] ParseFeedIds(string? FeedIdsRaw)
	{
		if (string.IsNullOrWhiteSpace(FeedIdsRaw))
		{
			return [];
		}

		string[] FeedIds = FeedIdsRaw
			.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		return FeedIds;
	}

	private static bool TryParseIsoDate(string? Value, out DateTimeOffset? ParsedValueUtc)
	{
		ParsedValueUtc = null;
		if (string.IsNullOrWhiteSpace(Value))
		{
			return true;
		}

		if (!DateTimeOffset.TryParse(Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTimeOffset ParsedValue))
		{
			return false;
		}

		ParsedValueUtc = ParsedValue.ToUniversalTime();
		return true;
	}

	private static string EncodeCursor(string PublishedAt, string IngestedAt, string Id)
	{
		string CursorText = $"{PublishedAt}|{IngestedAt}|{Id}";
		byte[] CursorBytes = System.Text.Encoding.UTF8.GetBytes(CursorText);
		return Convert.ToBase64String(CursorBytes);
	}

	private static bool TryParseCursor(string? CursorRaw, out CursorParts? Cursor)
	{
		Cursor = null;
		if (string.IsNullOrWhiteSpace(CursorRaw))
		{
			return true;
		}

		try
		{
			byte[] CursorBytes = Convert.FromBase64String(CursorRaw);
			string CursorText = System.Text.Encoding.UTF8.GetString(CursorBytes);
			string[] Parts = CursorText.Split('|', StringSplitOptions.None);
			if (Parts.Length != 3)
			{
				return false;
			}

			Cursor = new CursorParts(Parts[0], Parts[1], Parts[2]);
			return true;
		}
		catch (FormatException)
		{
			return false;
		}
	}

	public sealed record AddFeedRequest
	{
		public required string Url { get; init; }
		public string? Title { get; init; }
	}

	public sealed record FeedResponse
	{
		public required string Id { get; init; }
		public required string Url { get; init; }
		public required string NormalizedUrl { get; init; }
		public required string Title { get; init; }
		public required string Status { get; init; }
		public required int ConsecutiveFailures { get; init; }
		public string? LastError { get; init; }
		public string? LastPolledAt { get; init; }
		public string? LastSuccessAt { get; init; }
	}

	public sealed record RiverItemResponse
	{
		public required string Id { get; init; }
		public required string Title { get; init; }
		public string? CanonicalUrl { get; init; }
		public string? Snippet { get; init; }
		public required string PublishedAt { get; init; }
		public required string IngestedAt { get; init; }
		public string? SourceNames { get; init; }
	}

	public sealed record RiverQueryResponse
	{
		public required IReadOnlyList<RiverItemResponse> Items { get; init; }
		public string? NextCursor { get; init; }
	}

	public sealed record ErrorResponse(string Message);

	private sealed record CursorParts(string PublishedAt, string IngestedAt, string Id);

	public sealed record Latest200PerformanceResponse
	{
		public required int ItemCount { get; init; }
		public required long DurationMilliseconds { get; init; }
		public required int ThresholdMilliseconds { get; init; }
		public required bool MeetsTarget { get; init; }
	}
}
