using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Rocket.RiverOfNews.Data;
using Rocket.RiverOfNews.Datastar;
using Rocket.RiverOfNews.Services;

namespace Rocket.RiverOfNews.Api;

public static class DatastarApi
{
	private const string DatastarScript = """
		<script type="module" src="https://cdn.jsdelivr.net/gh/starfederation/datastar@1.0.0-RC.7/bundles/datastar.js"></script>
		""";

	private const string TailwindScript = """
		<script src="https://cdn.jsdelivr.net/npm/@tailwindcss/browser@4"></script>
		""";

	public static IResult GetRiverPage()
	{
		string html = $$"""
			<!doctype html>
			<html lang="en">
			<head>
				<meta charset="utf-8">
				<meta name="viewport" content="width=device-width, initial-scale=1">
				<title>Rocket River of News</title>
				{{TailwindScript}}
				{{DatastarScript}}
			</head>
			<body class="bg-slate-950 text-slate-100">
				<main class="mx-auto max-w-6xl p-4 md:p-8" data-signals="{cursor:'',selectedFeedIds:'',startDate:'',endDate:'',addFeedUrl:'',addFeedTitle:'',_refreshStatus:'',_addFeedStatus:'',_filterError:'',_feedActionStatus:'',_itemsCount:0,_hasMore:false}">
					<header class="mb-6 flex flex-wrap items-center justify-between gap-3">
						<div>
							<h1 class="text-2xl font-semibold">River of News</h1>
							<p class="text-sm text-slate-400">Unified newest-first feed stream</p>
						</div>
						<div class="flex items-center gap-2">
							<button data-on:click="@post('/river/refresh')" class="rounded bg-sky-600 px-4 py-2 text-sm font-semibold hover:bg-sky-500">Refresh now</button>
							<span data-text="$_refreshStatus" class="text-xs text-slate-400"></span>
						</div>
					</header>

					<section class="mb-6 rounded border border-slate-800 bg-slate-900 p-4">
						<div class="mb-2 text-sm font-semibold">Add feed</div>
						<div class="grid gap-3 md:grid-cols-3">
							<label class="text-sm md:col-span-2">
								<span class="mb-1 block text-slate-300">Feed URL</span>
								<input type="url" data-bind="addFeedUrl" placeholder="https://example.com/feed.xml" class="w-full rounded border border-slate-700 bg-slate-950 px-2 py-2 text-slate-100">
							</label>
							<label class="text-sm">
								<span class="mb-1 block text-slate-300">Title (optional)</span>
								<input type="text" data-bind="addFeedTitle" placeholder="My feed" class="w-full rounded border border-slate-700 bg-slate-950 px-2 py-2 text-slate-100">
							</label>
						</div>
						<div class="mt-3 flex items-center gap-3">
							<button data-on:click="@post('/river/feeds')" class="rounded bg-emerald-600 px-3 py-2 text-sm font-semibold hover:bg-emerald-500">Add feed</button>
							<span data-text="$_addFeedStatus" data-class="{'text-rose-400': $_addFeedStatus.startsWith('Error'), 'text-emerald-400': $_addFeedStatus.startsWith('Feed added')}" class="text-sm text-slate-400"></span>
						</div>
					</section>

					<section class="mb-6 rounded border border-slate-800 bg-slate-900 p-4">
						<div class="mb-2 text-sm font-semibold">Filters</div>
						<div class="grid gap-3 md:grid-cols-3">
							<label class="text-sm">
								<span class="mb-1 block text-slate-300">Start date (UTC)</span>
								<input type="datetime-local" data-bind="startDate" class="w-full rounded border border-slate-700 bg-slate-950 px-2 py-2 text-slate-100">
							</label>
							<label class="text-sm">
								<span class="mb-1 block text-slate-300">End date (UTC)</span>
								<input type="datetime-local" data-bind="endDate" class="w-full rounded border border-slate-700 bg-slate-950 px-2 py-2 text-slate-100">
							</label>
							<div class="text-sm">
								<span class="mb-1 block text-slate-300">Sources</span>
								<div id="source-filters" data-init="@get('/river/feeds')" class="max-h-36 overflow-auto rounded border border-slate-700 bg-slate-950 p-2"></div>
								<p data-text="$_feedActionStatus" class="mt-2 text-xs text-slate-400"></p>
							</div>
						</div>
						<div class="mt-3 flex gap-2">
							<button data-on:click="@get('/river/items?reset=true')" class="rounded bg-slate-700 px-3 py-2 text-sm font-semibold hover:bg-slate-600">Apply filters</button>
							<button data-on:click="@get('/river/clear-filters')" class="rounded border border-slate-600 px-3 py-2 text-sm hover:bg-slate-800">Clear</button>
						</div>
						<p data-text="$_filterError" class="mt-2 text-sm text-rose-400"></p>
					</section>

					<section id="items" class="space-y-3"></section>
					<div class="mt-4 flex items-center justify-between">
						<span data-text="`Loaded ${$_itemsCount} items`" class="text-sm text-slate-400"></span>
						<button data-on:click="@get('/river/items')" data-show="$_hasMore" class="rounded border border-slate-600 px-3 py-2 text-sm hover:bg-slate-800">Load more</button>
					</div>
				</main>

				<div data-init="@get('/river/items?reset=true')"></div>
			</body>
			</html>
			""";

		return Results.Content(html, "text/html; charset=utf-8");
	}

	public static async Task GetFeedsAsync(
		HttpResponse response,
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

		StringBuilder html = new();
		if (feeds.Count == 0)
		{
			html.AppendLine("<div class='text-xs text-slate-500'>No feeds added yet.</div>");
		}
		else
		{
			foreach (FeedResponse feed in feeds)
			{
				string checkboxId = $"feed_{feed.Id}";
				html.AppendLine($"""<div class="mb-1 flex items-center justify-between gap-2 text-xs">""");
				html.AppendLine($"""  <label class="flex items-center gap-2">""");
				html.AppendLine($"""    <input type="checkbox" id="{checkboxId}" value="{feed.Id}" data-on:change="@get('/river/toggle-feed/{feed.Id}')" class="accent-sky-500">""");
				html.AppendLine($"""    <span>{feed.Title} <span class="text-slate-500">({feed.Status})</span></span>""");
				html.AppendLine($"""  </label>""");
				html.AppendLine($"""  <button data-on:click="@confirm('Delete feed?') && @delete('/river/feeds/{feed.Id}')" class="rounded border border-rose-700 px-2 py-1 text-[11px] text-rose-300 hover:bg-rose-950">Delete</button>""");
				html.AppendLine($"""</div>""");
			}
		}

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);
		await sse.PatchElementsAsync(html.ToString(), "#source-filters", "inner", cancellationToken);
	}

	public static async Task ToggleFeedAsync(
		string feedId,
		HttpRequest request,
		HttpResponse response,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);
		string currentSelected = signals.TryGetValue("selectedFeedIds", out JsonElement selectedElement) && selectedElement.ValueKind == JsonValueKind.String
			? selectedElement.GetString() ?? ""
			: "";

		HashSet<string> selectedSet = currentSelected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
		if (selectedSet.Contains(feedId))
		{
			selectedSet.Remove(feedId);
		}
		else
		{
			selectedSet.Add(feedId);
		}

		string newSelected = string.Join(",", selectedSet);

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);
		await sse.PatchSignalsAsync(new { selectedFeedIds = newSelected }, cancellationToken);
	}

	public static async Task GetItemsAsync(
		HttpRequest request,
		HttpResponse response,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);
		bool reset = request.Query["reset"] == "true";

		string currentCursor = reset ? "" : (signals.TryGetValue("cursor", out JsonElement cursorElement) && cursorElement.ValueKind == JsonValueKind.String
			? cursorElement.GetString() ?? ""
			: "");

		string selectedFeedIds = signals.TryGetValue("selectedFeedIds", out JsonElement feedIdsElement) && feedIdsElement.ValueKind == JsonValueKind.String
			? feedIdsElement.GetString() ?? ""
			: "";

		string startDateStr = signals.TryGetValue("startDate", out JsonElement startDateElement) && startDateElement.ValueKind == JsonValueKind.String
			? startDateElement.GetString() ?? ""
			: "";

		string endDateStr = signals.TryGetValue("endDate", out JsonElement endDateElement) && endDateElement.ValueKind == JsonValueKind.String
			? endDateElement.GetString() ?? ""
			: "";

		string[] feedIds = selectedFeedIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		DateTimeOffset? startDate = ParseDateTimeLocal(startDateStr);
		DateTimeOffset? endDate = ParseDateTimeLocal(endDateStr);

		if (startDate.HasValue && endDate.HasValue && endDate.Value < startDate.Value)
		{
			SseHelper sse = response.CreateSseHelper();
			await sse.StartAsync(cancellationToken);
			await sse.PatchSignalsAsync(new { _filterError = "Invalid date range. End date must be on or after start date." }, cancellationToken);
			return;
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

		int limit = 50;
		int fetchLimit = limit + 1;
		CursorParts? cursor = ParseCursor(currentCursor);

		await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
		IReadOnlyList<RiverItemResponse> queriedItems = (await connection.QueryAsync<RiverItemResponse>(
			new CommandDefinition(
				sql,
				new
				{
					StartDate = startDate?.ToString("O", CultureInfo.InvariantCulture),
					EndDate = endDate?.ToString("O", CultureInfo.InvariantCulture),
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
		bool hasMore = false;
		if (queriedItems.Count > limit)
		{
			RiverItemResponse lastItem = items[^1];
			nextCursor = EncodeCursor(lastItem.PublishedAt, lastItem.IngestedAt, lastItem.Id);
			hasMore = true;
		}

		StringBuilder html = new();
		foreach (RiverItemResponse item in items)
		{
			string sourceText = item.SourceNames ?? "Unknown source";
			string detailsLink = $"/river/items/{Uri.EscapeDataString(item.Id)}";
			string link = item.CanonicalUrl ?? "#";
			string imageBlock = item.ImageUrl is not null
				? $"""<img class="mb-3 h-auto w-auto max-w-full rounded" src="{item.ImageUrl}" alt="">"""
				: "";

			html.AppendLine($"""<article class="rounded border border-slate-800 bg-slate-900 p-4">""");
			html.AppendLine($"""  <div class="mb-1 text-xs text-slate-400">{sourceText}</div>""");
			html.AppendLine($"""  <h2 class="mb-2 text-lg font-semibold"><a class="text-slate-100 hover:text-sky-300" href="{detailsLink}">{EscapeHtml(item.Title)}</a></h2>""");
			html.AppendLine($"""  <div class="mb-2 text-xs text-slate-400">{FormatDate(item.PublishedAt)}</div>""");
			if (!string.IsNullOrEmpty(imageBlock))
			{
				html.AppendLine($"""  {imageBlock}""");
			}
			html.AppendLine($"""  <p class="mb-3 text-sm text-slate-300">{EscapeHtml(item.Snippet ?? "")}</p>""");
			html.AppendLine($"""  <a class="text-sm text-sky-400 hover:text-sky-300" href="{link}" target="_blank" rel="noreferrer">Open article</a>""");
			html.AppendLine($"""</article>""");
		}

		SseHelper sseHelper = response.CreateSseHelper();
		await sseHelper.StartAsync(cancellationToken);

		string mode = reset ? "inner" : "append";
		string selector = reset ? "#items" : "#items";
		await sseHelper.PatchElementsAsync(html.ToString(), selector, mode, cancellationToken);

		int currentCount = reset ? items.Count : (signals.TryGetValue("_itemsCount", out JsonElement countElement) && countElement.ValueKind == JsonValueKind.Number
			? countElement.GetInt32() + items.Count
			: items.Count);

		await sseHelper.PatchSignalsAsync(new
		{
			cursor = nextCursor ?? "",
			_itemsCount = currentCount,
			_hasMore = hasMore,
			_filterError = ""
		}, cancellationToken);
	}

	public static async Task ClearFiltersAsync(
		HttpResponse response,
		CancellationToken cancellationToken)
	{
		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);
		await sse.PatchSignalsAsync(new
		{
			selectedFeedIds = "",
			startDate = "",
			endDate = "",
			cursor = "",
			_filterError = ""
		}, cancellationToken);
		await sse.PatchElementsAsync("<div data-init=\"@get('/river/feeds'); @get('/river/items?reset=true')\"></div>", "body", "append", cancellationToken);
	}

	public static async Task AddFeedAsync(
		HttpRequest request,
		HttpResponse response,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);

		string url = signals.TryGetValue("addFeedUrl", out JsonElement urlElement) && urlElement.ValueKind == JsonValueKind.String
			? urlElement.GetString()?.Trim() ?? ""
			: "";

		string title = signals.TryGetValue("addFeedTitle", out JsonElement titleElement) && titleElement.ValueKind == JsonValueKind.String
			? titleElement.GetString()?.Trim() ?? ""
			: "";

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);

		if (string.IsNullOrWhiteSpace(url))
		{
			await sse.PatchSignalsAsync(new { _addFeedStatus = "Error: Feed URL is required." }, cancellationToken);
			return;
		}

		string normalizedUrl;
		try
		{
			normalizedUrl = NormalizeUrl(url);
		}
		catch (UriFormatException)
		{
			await sse.PatchSignalsAsync(new { _addFeedStatus = "Error: Invalid feed URL." }, cancellationToken);
			return;
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

		try
		{
			await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
			await connection.ExecuteAsync(
				new CommandDefinition(
					sql,
					new
					{
						Id = feedId,
						Url = url,
						NormalizedUrl = normalizedUrl,
						Title = title.Length > 0 ? title : normalizedUrl,
						Now = now
					},
					cancellationToken: cancellationToken));
		}
		catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
		{
			await sse.PatchSignalsAsync(new { _addFeedStatus = "Error: Feed URL already exists." }, cancellationToken);
			return;
		}

		await sse.PatchSignalsAsync(new
		{
			addFeedUrl = "",
			addFeedTitle = "",
			_addFeedStatus = "Feed added.",
			cursor = "",
			_itemsCount = 0
		}, cancellationToken);

		await sse.PatchElementsAsync("<div data-init=\"@get('/river/feeds'); @get('/river/items?reset=true')\"></div>", "body", "append", cancellationToken);
	}

	public static async Task DeleteFeedAsync(
		string feedId,
		HttpRequest request,
		HttpResponse response,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);

		const string sql = "DELETE FROM feeds WHERE id = @FeedId;";
		await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
		int rowsAffected = await connection.ExecuteAsync(new CommandDefinition(sql, new { FeedId = feedId }, cancellationToken: cancellationToken));

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);

		if (rowsAffected == 0)
		{
			await sse.PatchSignalsAsync(new { _feedActionStatus = "Error: Feed not found." }, cancellationToken);
			return;
		}

		string currentSelected = signals.TryGetValue("selectedFeedIds", out JsonElement selectedElement) && selectedElement.ValueKind == JsonValueKind.String
			? selectedElement.GetString() ?? ""
			: "";

		HashSet<string> selectedSet = currentSelected.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
		selectedSet.Remove(feedId);
		string newSelected = string.Join(",", selectedSet);

		await sse.PatchSignalsAsync(new
		{
			selectedFeedIds = newSelected,
			_feedActionStatus = "Feed deleted.",
			cursor = "",
			_itemsCount = 0
		}, cancellationToken);

		await sse.PatchElementsAsync("<div data-init=\"@get('/river/feeds'); @get('/river/items?reset=true')\"></div>", "body", "append", cancellationToken);
	}

	public static async Task RefreshAsync(
		HttpResponse response,
		FeedIngestionService feedIngestionService,
		CancellationToken cancellationToken)
	{
		RefreshResult refreshResult = await feedIngestionService.RefreshAllFeedsAsync(cancellationToken);

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);
		await sse.PatchSignalsAsync(new
		{
			_refreshStatus = $"Done: {refreshResult.SuccessFeedCount} success, {refreshResult.FailedFeedCount} failed",
			cursor = "",
			_itemsCount = 0
		}, cancellationToken);

		await sse.PatchElementsAsync("<div data-init=\"@get('/river/feeds'); @get('/river/items?reset=true')\"></div>", "body", "append", cancellationToken);
	}

	public static IResult GetRiverItemPage(string itemId)
	{
		string html = $$"""
			<!doctype html>
			<html lang="en">
			<head>
				<meta charset="utf-8">
				<meta name="viewport" content="width=device-width, initial-scale=1">
				<title>River item details</title>
				{{TailwindScript}}
				{{DatastarScript}}
			</head>
			<body class="bg-slate-950 text-slate-100">
				<main class="mx-auto max-w-4xl p-4 md:p-8" data-signals="{id:'{{itemId}}',title:'',sourceNames:'',publishedAt:'',ingestedAt:'',imageUrl:'',canonicalUrl:'',url:'',snippet:'',_error:''}">
					<a href="/river" class="text-sm text-sky-400 hover:text-sky-300">← Back to river</a>
					<article class="mt-4 rounded border border-slate-800 bg-slate-900 p-4">
						<div data-text="$sourceNames" class="mb-2 text-xs text-slate-400"></div>
						<h1 data-text="$title" class="mb-3 text-2xl font-semibold"></h1>
						<div data-text="`Published: ${$publishedAt} | Ingested: ${$ingestedAt}`" class="mb-3 text-xs text-slate-400"></div>
						<img data-show="$imageUrl" data-attr:src="$imageUrl" class="mb-4 h-auto w-auto max-w-full rounded" alt="">
						<p data-text="$snippet" class="mb-4 text-sm text-slate-300 whitespace-pre-wrap"></p>
						<div data-show="$canonicalUrl || $url" class="flex flex-wrap gap-3 text-sm">
							<a data-show="$canonicalUrl" data-attr:href="$canonicalUrl" class="text-sky-400 hover:text-sky-300" target="_blank" rel="noreferrer">Open canonical article</a>
							<a data-show="$url" data-attr:href="$url" class="text-sky-400 hover:text-sky-300" target="_blank" rel="noreferrer">Open original article URL</a>
						</div>
						<p data-show="!$canonicalUrl && !$url && !$_error" class="mt-3 text-sm text-slate-500">No article URL available for this item.</p>
						<p data-text="$_error" class="mt-4 text-sm text-rose-400"></p>
					</article>
				</main>
				<div data-init="@get('/river/items/{{itemId}}/detail')"></div>
			</body>
			</html>
			""";

		return Results.Content(html, "text/html; charset=utf-8");
	}

	public static async Task GetItemDetailAsync(
		string itemId,
		HttpResponse response,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
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

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);

		if (item is null)
		{
			await sse.PatchSignalsAsync(new { _error = "Item not found." }, cancellationToken);
			return;
		}

		await sse.PatchSignalsAsync(new
		{
			title = item.Title,
			sourceNames = item.SourceNames ?? "Unknown source",
			publishedAt = FormatDate(item.PublishedAt),
			ingestedAt = FormatDate(item.IngestedAt),
			imageUrl = item.ImageUrl ?? "",
			canonicalUrl = item.CanonicalUrl ?? "",
			url = item.Url ?? "",
			snippet = item.Snippet ?? ""
		}, cancellationToken);
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

	private static DateTimeOffset? ParseDateTimeLocal(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedValue))
		{
			return parsedValue.ToUniversalTime();
		}

		return null;
	}

	private static string FormatDate(string isoDate)
	{
		if (string.IsNullOrWhiteSpace(isoDate))
		{
			return "";
		}

		if (DateTimeOffset.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTimeOffset date))
		{
			return date.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture);
		}

		return isoDate;
	}

	private static string EscapeHtml(string text)
	{
		return text
			.Replace("&", "&amp;")
			.Replace("<", "&lt;")
			.Replace(">", "&gt;")
			.Replace("\"", "&quot;")
			.Replace("'", "&#39;");
	}

	private static string EncodeCursor(string publishedAt, string ingestedAt, string id)
	{
		string cursorText = $"{publishedAt}|{ingestedAt}|{id}";
		byte[] cursorBytes = Encoding.UTF8.GetBytes(cursorText);
		return Convert.ToBase64String(cursorBytes);
	}

	private static CursorParts? ParseCursor(string? cursorRaw)
	{
		if (string.IsNullOrWhiteSpace(cursorRaw))
		{
			return null;
		}

		try
		{
			byte[] cursorBytes = Convert.FromBase64String(cursorRaw);
			string cursorText = Encoding.UTF8.GetString(cursorBytes);
			string[] parts = cursorText.Split('|', StringSplitOptions.None);
			if (parts.Length != 3)
			{
				return null;
			}

			return new CursorParts(parts[0], parts[1], parts[2]);
		}
		catch (FormatException)
		{
			return null;
		}
	}

	private sealed record CursorParts(string PublishedAt, string IngestedAt, string Id);

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
}
