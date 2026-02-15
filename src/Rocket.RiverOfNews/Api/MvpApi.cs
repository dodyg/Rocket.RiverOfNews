using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Rocket.RiverOfNews.Data;
using Rocket.RiverOfNews.Services;

namespace Rocket.RiverOfNews.Api;

public static class MvpApi
{
	public static IResult GetRiverPage()
	{
		const string html = """
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
								<p id="feedActionStatus" class="mt-2 text-xs text-slate-400"></p>
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
					const FeedActionStatus = document.getElementById("feedActionStatus");

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
							const detailsLink = `/river/items/${encodeURIComponent(item.id)}`;
							const link = item.canonicalUrl || "#";
							const imageBlock = item.imageUrl
								? `<img class="mb-3 h-auto w-auto max-w-full rounded" src="${item.imageUrl}" alt="">`
								: "";
							article.innerHTML = `
								<div class="mb-1 text-xs text-slate-400">${sourceText}</div>
								<h2 class="mb-2 text-lg font-semibold"><a class="text-slate-100 hover:text-sky-300" href="${detailsLink}">${item.title}</a></h2>
								<div class="mb-2 text-xs text-slate-400">${new Date(item.publishedAt).toUTCString()}</div>
								${imageBlock}
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

					function setFeedActionStatus(message, isError) {
						FeedActionStatus.textContent = message || "";
						FeedActionStatus.classList.toggle("text-rose-400", !!isError);
						FeedActionStatus.classList.toggle("text-emerald-400", !!message && !isError);
						FeedActionStatus.classList.toggle("text-slate-400", !message);
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
							const row = document.createElement("div");
							row.className = "mb-1 flex items-center justify-between gap-2 text-xs";

							const label = document.createElement("label");
							label.className = "flex items-center gap-2";
							label.innerHTML = `
								<input id="${id}" type="checkbox" value="${feed.id}" class="accent-sky-500">
								<span>${feed.title} <span class="text-slate-500">(${feed.status})</span></span>
							`;
							const checkbox = label.querySelector("input");
							checkbox.checked = State.selectedFeedIds.has(feed.id);
							checkbox.addEventListener("change", (event) => {
								const checked = event.target.checked;
								if (checked) State.selectedFeedIds.add(feed.id);
								else State.selectedFeedIds.delete(feed.id);
							});

							const deleteButton = document.createElement("button");
							deleteButton.type = "button";
							deleteButton.className = "rounded border border-rose-700 px-2 py-1 text-[11px] text-rose-300 hover:bg-rose-950";
							deleteButton.textContent = "Delete";
							deleteButton.addEventListener("click", async () => {
								if (!confirm(`Delete feed "${feed.title}"?`)) return;
								deleteButton.disabled = true;
								setFeedActionStatus("Deleting feed...", false);
								try {
									const deleteResponse = await fetch(`/api/feeds/${encodeURIComponent(feed.id)}`, { method: "DELETE" });
									let payload = {};
									try {
										payload = await deleteResponse.json();
									} catch {
										payload = {};
									}
									if (!deleteResponse.ok) {
										setFeedActionStatus(payload.message || "Failed to delete feed.", true);
										deleteButton.disabled = false;
										return;
									}

									State.selectedFeedIds.delete(feed.id);
									setFeedActionStatus("Feed deleted.", false);
									await loadFeeds();
									State.cursor = null;
									await loadItems(true);
								} catch {
									setFeedActionStatus("Network error while deleting feed.", true);
									deleteButton.disabled = false;
								}
							});

							row.appendChild(label);
							row.appendChild(deleteButton);
							SourceFiltersContainer.appendChild(row);
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
							params.set("limit", "200");

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

		return Results.Content(html, "text/html; charset=utf-8");
	}

	public static IResult GetRiverItemPage(string itemId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
		string serializedItemId = JsonSerializer.Serialize(itemId);
		string html = $$"""
			<!doctype html>
			<html lang="en">
			<head>
				<meta charset="utf-8">
				<meta name="viewport" content="width=device-width, initial-scale=1">
				<title>River item details</title>
				<script src="https://cdn.jsdelivr.net/npm/@tailwindcss/browser@4"></script>
			</head>
			<body class="bg-slate-950 text-slate-100">
				<main class="mx-auto max-w-4xl p-4 md:p-8">
					<a href="/river" class="text-sm text-sky-400 hover:text-sky-300">← Back to river</a>
					<article class="mt-4 rounded border border-slate-800 bg-slate-900 p-4">
						<div id="itemSources" class="mb-2 text-xs text-slate-400"></div>
						<h1 id="itemTitle" class="mb-3 text-2xl font-semibold"></h1>
						<div id="itemDates" class="mb-3 text-xs text-slate-400"></div>
						<img id="itemImage" class="mb-4 hidden h-auto w-auto max-w-full rounded" alt="">
						<p id="itemSnippet" class="mb-4 text-sm text-slate-300 whitespace-pre-wrap"></p>
						<div id="itemLinks" class="flex flex-wrap gap-3 text-sm">
							<a id="itemCanonicalLink" class="text-sky-400 hover:text-sky-300" target="_blank" rel="noreferrer">Open canonical article</a>
							<a id="itemOriginalLink" class="text-sky-400 hover:text-sky-300" target="_blank" rel="noreferrer">Open original article URL</a>
						</div>
						<p id="itemLinkError" class="mt-3 hidden text-sm text-slate-500"></p>
						<p id="itemError" class="mt-4 text-sm text-rose-400"></p>
					</article>
				</main>
				<script>
					const itemId = {{serializedItemId}};
					const itemTitle = document.getElementById("itemTitle");
					const itemSources = document.getElementById("itemSources");
					const itemDates = document.getElementById("itemDates");
					const itemImage = document.getElementById("itemImage");
					const itemSnippet = document.getElementById("itemSnippet");
					const itemCanonicalLink = document.getElementById("itemCanonicalLink");
					const itemOriginalLink = document.getElementById("itemOriginalLink");
					const itemLinks = document.getElementById("itemLinks");
					const itemLinkError = document.getElementById("itemLinkError");
					const itemError = document.getElementById("itemError");

					async function loadItem() {
						const response = await fetch(`/api/items/${encodeURIComponent(itemId)}`);
						let payload = {};
						try {
							payload = await response.json();
						} catch {
							payload = {};
						}

						if (!response.ok) {
							itemError.textContent = payload.message || "Failed to load item details.";
							return;
						}

						itemTitle.textContent = payload.title;
						itemSources.textContent = payload.sourceNames || "Unknown source";
						itemDates.textContent = `Published: ${new Date(payload.publishedAt).toUTCString()} | Ingested: ${new Date(payload.ingestedAt).toUTCString()}`;
						if (payload.imageUrl) {
							itemImage.src = payload.imageUrl;
							itemImage.classList.remove("hidden");
						} else {
							itemImage.removeAttribute("src");
							itemImage.classList.add("hidden");
						}
						itemSnippet.textContent = payload.snippet || "";

						const hasCanonicalUrl = !!payload.canonicalUrl;
						const hasOriginalUrl = !!payload.url;
						if (!hasCanonicalUrl && !hasOriginalUrl) {
							itemLinks.classList.add("hidden");
							itemLinkError.textContent = "No article URL available for this item.";
							itemLinkError.classList.remove("hidden");
						} else {
							if (hasCanonicalUrl) {
								itemCanonicalLink.href = payload.canonicalUrl;
							} else {
								itemCanonicalLink.classList.add("hidden");
							}
							if (hasOriginalUrl) {
								itemOriginalLink.href = payload.url;
							} else {
								itemOriginalLink.classList.add("hidden");
							}
						}
					}

					loadItem();
				</script>
			</body>
			</html>
			""";

		return Results.Content(html, "text/html; charset=utf-8");
	}

	public static async Task<IResult> GetFeedsAsync(
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		const string sql = """
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

		await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
		IReadOnlyList<FeedResponse> feeds = (await connection.QueryAsync<FeedResponse>(new CommandDefinition(sql, cancellationToken: cancellationToken))).AsList();
		return Results.Ok(feeds);
	}

	public static async Task<IResult> AddFeedAsync(
		AddFeedRequest request,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentException.ThrowIfNullOrWhiteSpace(request.Url);

		string normalizedUrl;
		try
		{
			normalizedUrl = NormalizeUrl(request.Url);
		}
		catch (UriFormatException)
		{
			return Results.BadRequest(new ErrorResponse("Invalid feed URL."));
		}

		string feedId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
		string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

		const string sql = """
			INSERT INTO feeds (
				id, url, normalized_url, title, status, consecutive_failures, created_at, updated_at
			)
			VALUES (
				@Id, @Url, @NormalizedUrl, @Title, 'healthy', 0, @Now, @Now
			);
			""";

		FeedResponse feed = new()
		{
			Id = feedId,
			Url = request.Url,
			NormalizedUrl = normalizedUrl,
			Title = request.Title ?? normalizedUrl,
			Status = "healthy",
			ConsecutiveFailures = 0,
			LastError = null,
			LastPolledAt = null,
			LastSuccessAt = null
		};

		try
		{
			await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
			await connection.ExecuteAsync(
				new CommandDefinition(
					sql,
					new
					{
						Id = feed.Id,
						Url = feed.Url,
						NormalizedUrl = feed.NormalizedUrl,
						Title = feed.Title,
						Now = now
					},
					cancellationToken: cancellationToken));
		}
		catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
		{
			return Results.Conflict(new ErrorResponse("Feed URL already exists."));
		}

		return Results.Created($"/api/feeds/{feed.Id}", feed);
	}

	public static async Task<IResult> DeleteFeedAsync(
		string feedId,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(feedId);

		const string deleteOrphanedItemsSql = """
			DELETE FROM items 
			WHERE id IN (
				SELECT is1.item_id 
				FROM item_sources is1 
				WHERE is1.feed_id = @FeedId
				AND NOT EXISTS (
					SELECT 1 FROM item_sources is2 
					WHERE is2.item_id = is1.item_id 
					AND is2.feed_id != @FeedId
				)
			);
			""";
		const string deleteFeedSql = "DELETE FROM feeds WHERE id = @FeedId;";

		await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
		await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

		await connection.ExecuteAsync(new CommandDefinition(deleteOrphanedItemsSql, new { FeedId = feedId }, transaction, cancellationToken: cancellationToken));
		int rowsAffected = await connection.ExecuteAsync(new CommandDefinition(deleteFeedSql, new { FeedId = feedId }, transaction, cancellationToken: cancellationToken));

		await transaction.CommitAsync(cancellationToken);

		return rowsAffected > 0 ? Results.NoContent() : Results.NotFound(new ErrorResponse("Feed not found."));
	}

	public static async Task<IResult> RefreshAsync(
		FeedIngestionService feedIngestionService,
		CancellationToken cancellationToken)
	{
		RefreshResult refreshResult = await feedIngestionService.RefreshAllFeedsAsync(cancellationToken);
		return Results.Ok(refreshResult);
	}

	public static async Task<IResult> GetItemsAsync(
		HttpRequest request,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		string? feedIdsRaw = request.Query["feed_ids"];
		string? startDateRaw = request.Query["start_date"];
		string? endDateRaw = request.Query["end_date"];
		string? cursorRaw = request.Query["cursor"];
		string? limitRaw = request.Query["limit"];

		string[] feedIds = ParseFeedIds(feedIdsRaw);
		if (!TryParseIsoDate(startDateRaw, out DateTimeOffset? startDateUtc))
		{
			return Results.BadRequest(new ErrorResponse("Invalid start_date; expected ISO-8601 UTC."));
		}

		if (!TryParseIsoDate(endDateRaw, out DateTimeOffset? endDateUtc))
		{
			return Results.BadRequest(new ErrorResponse("Invalid end_date; expected ISO-8601 UTC."));
		}

		if (startDateUtc.HasValue && endDateUtc.HasValue && endDateUtc.Value < startDateUtc.Value)
		{
			return Results.BadRequest(new ErrorResponse("Invalid date range; end_date must be on or after start_date."));
		}

		int limit = 200;
		if (!string.IsNullOrWhiteSpace(limitRaw) && (!int.TryParse(limitRaw, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit <= 0 || limit > 200))
		{
			return Results.BadRequest(new ErrorResponse("Invalid limit; expected integer in range 1..200."));
		}

		if (!TryParseCursor(cursorRaw, out CursorParts? cursor))
		{
			return Results.BadRequest(new ErrorResponse("Invalid cursor."));
		}

		const string sql = """
			SELECT
				i.id AS Id,
				i.title AS Title,
				i.canonical_url AS CanonicalUrl,
				i.image_url AS ImageUrl,
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

		int fetchLimit = limit + 1;
		string? startDate = startDateUtc?.ToString("O", CultureInfo.InvariantCulture);
		string? endDate = endDateUtc?.ToString("O", CultureInfo.InvariantCulture);
		await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
		IReadOnlyList<RiverItemResponse> queriedItems = (await connection.QueryAsync<RiverItemResponse>(
			new CommandDefinition(
				sql,
				new
				{
					StartDate = startDate,
					EndDate = endDate,
					HasCursor = cursor is not null ? 1 : 0,
					CursorPublishedAt = cursor?.PublishedAt,
					CursorIngestedAt = cursor?.IngestedAt,
					CursorId = cursor?.Id,
					HasFeedFilter = feedIds.Length > 0 ? 1 : 0,
					FeedIds = feedIds,
					FetchLimit = fetchLimit
				},
				cancellationToken: cancellationToken))).AsList();

		IReadOnlyList<RiverItemResponse> items = queriedItems.Count > limit
			? queriedItems.Take(limit).ToList()
			: queriedItems;

		string? nextCursor = null;
		if (queriedItems.Count > limit)
		{
			RiverItemResponse lastItem = items[^1];
			nextCursor = EncodeCursor(lastItem.PublishedAt, lastItem.IngestedAt, lastItem.Id);
		}

		return Results.Ok(new RiverQueryResponse
		{
			Items = items,
			NextCursor = nextCursor
		});
	}

	public static async Task<IResult> GetItemByIdAsync(
		string itemId,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

		const string sql = """
			SELECT
				i.id AS Id,
				i.title AS Title,
				i.url AS Url,
				i.canonical_url AS CanonicalUrl,
				i.image_url AS ImageUrl,
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
			WHERE i.id = @ItemId
			LIMIT 1;
			""";

		await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
		RiverItemDetailResponse? item = await connection.QuerySingleOrDefaultAsync<RiverItemDetailResponse>(
			new CommandDefinition(sql, new { ItemId = itemId }, cancellationToken: cancellationToken));
		return item is null
			? Results.NotFound(new ErrorResponse("Item not found."))
			: Results.Ok(item);
	}

	public static async Task<IResult> GetLatest200PerformanceAsync(
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		const string sql = """
			SELECT
				i.id AS Id,
				i.title AS Title,
				i.canonical_url AS CanonicalUrl,
				i.image_url AS ImageUrl,
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

		await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
		Stopwatch stopwatch = Stopwatch.StartNew();
		IReadOnlyList<RiverItemResponse> items = (await connection.QueryAsync<RiverItemResponse>(
			new CommandDefinition(sql, cancellationToken: cancellationToken))).AsList();
		stopwatch.Stop();

		return Results.Ok(new Latest200PerformanceResponse
		{
			ItemCount = items.Count,
			DurationMilliseconds = stopwatch.ElapsedMilliseconds,
			ThresholdMilliseconds = 2000,
			MeetsTarget = stopwatch.ElapsedMilliseconds < 2000
		});
	}

	private static string NormalizeUrl(string url)
	{
		Uri parsedUri = new(url, UriKind.Absolute);
		if (!string.Equals(parsedUri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(parsedUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
		{
			throw new UriFormatException("Only HTTP and HTTPS URLs are supported.");
		}

		UriBuilder builder = new(parsedUri)
		{
			Fragment = string.Empty
		};

		string scheme = builder.Scheme.ToLowerInvariant();
		string host = builder.Host.ToLowerInvariant();
		string port = builder.Port > 0 && !builder.Uri.IsDefaultPort
			? $":{builder.Port.ToString(CultureInfo.InvariantCulture)}"
			: string.Empty;
		string path = builder.Path.TrimEnd('/');
		if (string.IsNullOrWhiteSpace(path))
		{
			path = "/";
		}

		string query = builder.Query;
		return $"{scheme}://{host}{port}{path}{query}";
	}

	private static string[] ParseFeedIds(string? feedIdsRaw)
	{
		if (string.IsNullOrWhiteSpace(feedIdsRaw))
		{
			return [];
		}

		string[] feedIds = feedIdsRaw
			.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		return feedIds;
	}

	private static bool TryParseIsoDate(string? value, out DateTimeOffset? parsedValueUtc)
	{
		parsedValueUtc = null;
		if (string.IsNullOrWhiteSpace(value))
		{
			return true;
		}

		if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedValue))
		{
			return false;
		}

		parsedValueUtc = parsedValue.ToUniversalTime();
		return true;
	}

	private static string EncodeCursor(string publishedAt, string ingestedAt, string id)
	{
		string cursorText = $"{publishedAt}|{ingestedAt}|{id}";
		byte[] cursorBytes = System.Text.Encoding.UTF8.GetBytes(cursorText);
		return Convert.ToBase64String(cursorBytes);
	}

	private static bool TryParseCursor(string? cursorRaw, out CursorParts? cursor)
	{
		cursor = null;
		if (string.IsNullOrWhiteSpace(cursorRaw))
		{
			return true;
		}

		try
		{
			byte[] cursorBytes = Convert.FromBase64String(cursorRaw);
			string cursorText = System.Text.Encoding.UTF8.GetString(cursorBytes);
			string[] parts = cursorText.Split('|', StringSplitOptions.None);
			if (parts.Length != 3)
			{
				return false;
			}

			cursor = new CursorParts(parts[0], parts[1], parts[2]);
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
		public string? ImageUrl { get; init; }
		public string? Snippet { get; init; }
		public required string PublishedAt { get; init; }
		public required string IngestedAt { get; init; }
		public string? SourceNames { get; init; }
	}

	public sealed record RiverItemDetailResponse
	{
		public required string Id { get; init; }
		public required string Title { get; init; }
		public string? Url { get; init; }
		public string? CanonicalUrl { get; init; }
		public string? ImageUrl { get; init; }
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
