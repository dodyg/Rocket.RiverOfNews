using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
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
				<main class="mx-auto max-w-6xl p-4 md:p-8" data-signals="{cursor:'',selectedFeedIds:'',startDate:'',endDate:'',addFeedUrl:'',addFeedTitle:'',_refreshStatus:'',_addFeedStatus:'',_filterError:'',_feedActionStatus:'',itemsCount:0,hasMore:false,_clearStatus:''}">
					<header class="mb-6 flex flex-wrap items-center justify-between gap-3">
						<div>
							<h1 class="text-2xl font-semibold">River of News</h1>
							<p class="text-sm text-slate-400">Unified newest-first feed stream</p>
						</div>
						<div class="flex items-center gap-2">
							<a href="/river/opml" class="rounded border border-slate-700 px-4 py-2 text-sm font-semibold hover:bg-slate-800">Import OPML</a>
							<a href="/river/customize" class="rounded border border-slate-700 px-4 py-2 text-sm font-semibold hover:bg-slate-800">Customize</a>
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
								<span class="mb-1 block text-slate-300">Start date (local time)</span>
								<input type="datetime-local" data-bind="startDate" class="w-full rounded border border-slate-700 bg-slate-950 px-2 py-2 text-slate-100">
							</label>
							<label class="text-sm">
								<span class="mb-1 block text-slate-300">End date (local time)</span>
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

					<section class="mb-6 rounded border border-rose-900 bg-slate-900 p-4">
						<div class="mb-2 text-sm font-semibold text-rose-400">Danger Zone</div>
						<div class="flex items-center gap-3">
							<button data-on:click="confirm('Delete all feed items?') && @delete('/river/items')" class="rounded border border-rose-700 bg-rose-950 px-3 py-2 text-sm font-semibold text-rose-300 hover:bg-rose-900">Clear all items</button>
							<span data-text="$_clearStatus" class="text-xs text-slate-400"></span>
						</div>
						<p class="mt-2 text-xs text-slate-500">This will remove all feed items but keep your feed subscriptions.</p>
					</section>

					<section id="items" class="space-y-3"></section>
					<div class="mt-4 flex items-center justify-between">
						<span data-text="`Loaded ${$itemsCount} items`" class="text-sm text-slate-400"></span>
						<button data-on:click="@get('/river/items')" data-show="$hasMore" class="rounded border border-slate-600 px-3 py-2 text-sm hover:bg-slate-800">Load more</button>
					</div>
				</main>

				<div data-init="@get('/river/items?reset=true')"></div>
			</body>
			</html>
			""";

		return Results.Content(html, "text/html; charset=utf-8");
	}

	public static async Task<IResult> GetCustomizePage(
		HttpRequest request,
		TopicCustomizationService topicCustomizationService,
		CancellationToken cancellationToken)
	{
		string selectedTopic = request.Query["topic"].ToString();
		CustomizePageModel model = await topicCustomizationService.BuildPageModelAsync(selectedTopic, cancellationToken);
		string topicListHtml = BuildTopicListHtml(model.Topics, model.SelectedTopic);
		string selectedTopicHeading = model.SelectedTopic is null
			? "Pick a topic"
			: HtmlEncoder.Default.Encode(model.SelectedTopic);
		string selectedTopicActions = model.SelectedTopic is null
			? string.Empty
			: """<a href="/river/customize" class="rounded border border-slate-700 px-3 py-2 text-sm font-semibold hover:bg-slate-800">Clear topic</a>""";
		string itemsHtml = model.MatchingItems.Count == 0
			? """<div class="rounded border border-dashed border-slate-700 bg-slate-900 p-6 text-sm text-slate-400">No feed items matched this topic yet.</div>"""
			: BuildItemCardsHtml(model.MatchingItems);
		string emptyTopicsHtml = """<div class="rounded border border-dashed border-slate-700 bg-slate-950 p-4 text-sm text-slate-400">Add feeds and ingest items to generate topic suggestions.</div>""";
		string html = $$"""
			<!doctype html>
			<html lang="en">
			<head>
				<meta charset="utf-8">
				<meta name="viewport" content="width=device-width, initial-scale=1">
				<title>Customize your river</title>
				{{TailwindScript}}
				{{DatastarScript}}
			</head>
			<body class="bg-slate-950 text-slate-100">
				<main class="mx-auto max-w-6xl p-4 md:p-8">
					<header class="mb-6 flex flex-wrap items-center justify-between gap-3">
						<div>
							<h1 class="text-2xl font-semibold">Customize your river</h1>
							<p class="text-sm text-slate-400">Derived from {{model.SourceItemCount}} stored feed items with local .NET topic analysis.</p>
						</div>
						<div class="flex items-center gap-2">
							<a href="/river" class="rounded border border-slate-700 px-4 py-2 text-sm font-semibold hover:bg-slate-800">Back to river</a>
							<a href="/river/customize" class="rounded bg-sky-600 px-4 py-2 text-sm font-semibold hover:bg-sky-500">Refresh topics</a>
						</div>
					</header>

					<div class="grid gap-6 lg:grid-cols-[20rem_minmax(0,1fr)]">
						<aside class="rounded border border-slate-800 bg-slate-900 p-4">
							<h2 class="text-lg font-semibold">Suggested topics</h2>
							<p class="mt-2 text-sm text-slate-400">Pick a topic to surface feed items with the strongest title and snippet context match.</p>
							<div class="mt-4 space-y-2">
								{{(model.Topics.Count == 0 ? emptyTopicsHtml : topicListHtml)}}
							</div>
						</aside>

						<section class="space-y-4">
							<div class="rounded border border-slate-800 bg-slate-900 p-4">
								<div class="flex flex-wrap items-center justify-between gap-3">
									<div>
										<h2 class="text-lg font-semibold">{{selectedTopicHeading}}</h2>
										<p class="mt-1 text-sm text-slate-400">The customize page scores items against the selected topic using weighted phrase and token overlap.</p>
									</div>
									{{selectedTopicActions}}
								</div>
							</div>
							<div class="space-y-3">
								{{itemsHtml}}
							</div>
						</section>
					</div>
				</main>
			</body>
			</html>
			""";

		return Results.Content(html, "text/html; charset=utf-8");
	}

	public static IResult GetOpmlPage()
	{
		string html = $$"""
			<!doctype html>
			<html lang="en">
			<head>
				<meta charset="utf-8">
				<meta name="viewport" content="width=device-width, initial-scale=1">
				<title>Import feeds from OPML</title>
				{{TailwindScript}}
				{{DatastarScript}}
			</head>
			<body class="bg-slate-950 text-slate-100">
				<main class="mx-auto max-w-6xl p-4 md:p-8" data-signals="{opmlUrl:'',opmlFeedPayload:'[]',selectedOpmlFeedIds:'',_opmlStatus:'',_opmlDiagnostics:'',_documentTitle:'',_healthStatus:'',_subscribeStatus:''}">
					<header class="mb-6 flex flex-wrap items-center justify-between gap-3">
						<div>
							<h1 class="text-2xl font-semibold">Import feeds from OPML</h1>
							<p class="text-sm text-slate-400">Fetch a remote OPML document, inspect feed health, and subscribe selected sources.</p>
						</div>
						<div class="flex items-center gap-2">
							<a href="/river" class="rounded border border-slate-700 px-4 py-2 text-sm font-semibold hover:bg-slate-800">Back to river</a>
						</div>
					</header>

					<section class="mb-6 rounded border border-slate-800 bg-slate-900 p-4">
						<div class="mb-2 text-sm font-semibold">Fetch OPML source</div>
						<label class="text-sm">
							<span class="mb-1 block text-slate-300">OPML URL</span>
							<input type="url" data-bind="opmlUrl" placeholder="http://news.bbc.co.uk/rss/newsonline_world_edition/feeds.opml" class="w-full rounded border border-slate-700 bg-slate-950 px-2 py-2 text-slate-100">
						</label>
						<div class="mt-3 flex flex-wrap items-center gap-3">
							<button data-on:click="@post('/river/opml/fetch')" class="rounded bg-sky-600 px-3 py-2 text-sm font-semibold hover:bg-sky-500">Fetch OPML</button>
							<span data-text="$_opmlStatus" class="text-sm text-slate-300"></span>
						</div>
						<p data-show="$_documentTitle" data-text="`Document: ${$_documentTitle}`" class="mt-2 text-xs text-slate-400"></p>
						<p data-text="$_opmlDiagnostics" class="mt-2 text-xs text-amber-300"></p>
					</section>

					<section class="rounded border border-slate-800 bg-slate-900 p-4">
						<div class="mb-4 flex flex-wrap items-center justify-between gap-3">
							<div>
								<h2 class="text-lg font-semibold">Imported feeds</h2>
								<p class="text-sm text-slate-400">Check the feeds you want to inspect or subscribe.</p>
							</div>
							<div class="flex flex-wrap items-center gap-2">
								<button data-on:click="@post('/river/opml/check')" class="rounded border border-slate-600 px-3 py-2 text-sm font-semibold hover:bg-slate-800">Check selected feeds</button>
								<button data-on:click="@post('/river/opml/subscribe')" class="rounded bg-emerald-600 px-3 py-2 text-sm font-semibold hover:bg-emerald-500">Subscribe selected</button>
							</div>
						</div>
						<p data-text="$_healthStatus" class="mb-2 text-sm text-slate-300"></p>
						<p data-text="$_subscribeStatus" class="mb-4 text-sm text-slate-300"></p>
						<div id="opml-import-results" class="overflow-x-auto">
							<div class="rounded border border-dashed border-slate-700 bg-slate-950 p-4 text-sm text-slate-400">Fetch an OPML document to list available feeds.</div>
						</div>
					</section>
				</main>
			</body>
			</html>
			""";

		return Results.Content(html, "text/html; charset=utf-8");
	}

	public static async Task GetFeedsAsync(
		HttpRequest request,
		HttpResponse response,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);
		string[] selectedFeedIds = DatastarSignals.GetCsvValues(signals, "selectedFeedIds");
		string html = await BuildFeedsHtmlAsync(connectionFactory, selectedFeedIds, cancellationToken);

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);
		await sse.PatchElementsAsync(html, "#source-filters", "inner", cancellationToken);
	}

	public static async Task FetchOpmlAsync(
		HttpRequest request,
		HttpResponse response,
		OpmlImportService opmlImportService,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);
		string opmlUrl = DatastarSignals.GetString(signals, "opmlUrl").Trim();

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);

		if (string.IsNullOrWhiteSpace(opmlUrl))
		{
			await sse.PatchSignalsAsync(new { _opmlStatus = "Error: OPML URL is required." }, cancellationToken);
			return;
		}

		OpmlImportResponse importResponse;
		try
		{
			importResponse = await opmlImportService.FetchFeedsAsync(opmlUrl, cancellationToken);
		}
		catch (UriFormatException)
		{
			await sse.PatchSignalsAsync(new { _opmlStatus = "Error: Invalid OPML URL." }, cancellationToken);
			return;
		}
		catch (InvalidOperationException exception)
		{
			await sse.PatchSignalsAsync(new { _opmlStatus = $"Error: {exception.Message}" }, cancellationToken);
			return;
		}
		catch (HttpRequestException exception)
		{
			await sse.PatchSignalsAsync(new { _opmlStatus = $"Error: {exception.Message}" }, cancellationToken);
			return;
		}
		catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			await sse.PatchSignalsAsync(new { _opmlStatus = "Error: OPML fetch timed out." }, cancellationToken);
			return;
		}

		string importedFeedsHtml = BuildImportedFeedsHtml(importResponse.Feeds, []);
		string statusMessage = importResponse.Feeds.Count == 0
			? "OPML loaded, but no feed URLs were found."
			: $"Loaded {importResponse.Feeds.Count.ToString(CultureInfo.InvariantCulture)} feed(s) from OPML.";
		string diagnostics = importResponse.Diagnostics.Count == 0
			? string.Empty
			: string.Join(" | ", importResponse.Diagnostics);
		string payload = SerializeImportedFeeds(importResponse.Feeds);

		await sse.PatchElementsAsync(importedFeedsHtml, "#opml-import-results", "inner", cancellationToken);
		await sse.PatchSignalsAsync(new
		{
			opmlFeedPayload = payload,
			selectedOpmlFeedIds = "",
			_opmlStatus = statusMessage,
			_opmlDiagnostics = diagnostics,
			_documentTitle = importResponse.DocumentTitle ?? "",
			_healthStatus = "",
			_subscribeStatus = ""
		}, cancellationToken);
	}

	public static async Task ToggleFeedAsync(
		string feedId,
		HttpRequest request,
		HttpResponse response,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);
		HashSet<string> selectedSet = DatastarSignals.GetCsvValues(signals, "selectedFeedIds").ToHashSet(StringComparer.Ordinal);
		if (selectedSet.Contains(feedId))
		{
			selectedSet.Remove(feedId);
		}
		else
		{
			selectedSet.Add(feedId);
		}

		string newSelected = DatastarSignals.ToCsv(selectedSet);

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);
		await sse.PatchSignalsAsync(new { selectedFeedIds = newSelected }, cancellationToken);
	}

	public static async Task ToggleOpmlFeedAsync(
		string feedId,
		HttpRequest request,
		HttpResponse response,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);
		HashSet<string> selectedSet = DatastarSignals.GetCsvValues(signals, "selectedOpmlFeedIds").ToHashSet(StringComparer.Ordinal);
		if (selectedSet.Contains(feedId))
		{
			selectedSet.Remove(feedId);
		}
		else
		{
			selectedSet.Add(feedId);
		}

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);
		await sse.PatchSignalsAsync(new
		{
			selectedOpmlFeedIds = DatastarSignals.ToCsv(selectedSet)
		}, cancellationToken);
	}

	public static async Task CheckOpmlFeedsAsync(
		HttpRequest request,
		HttpResponse response,
		OpmlImportService opmlImportService,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);
		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);

		if (!TryGetImportedFeeds(signals, out IReadOnlyList<OpmlImportedFeedCandidate>? importedFeeds, out string? errorMessage))
		{
			await sse.PatchSignalsAsync(new { _healthStatus = $"Error: {errorMessage}" }, cancellationToken);
			return;
		}

		string[] selectedFeedIds = DatastarSignals.GetCsvValues(signals, "selectedOpmlFeedIds");
		if (selectedFeedIds.Length == 0)
		{
			await sse.PatchSignalsAsync(new { _healthStatus = "Error: Select at least one imported feed." }, cancellationToken);
			return;
		}

		IReadOnlyList<OpmlImportedFeedCandidate> checkedFeeds = await opmlImportService.CheckFeedsAsync(importedFeeds!, selectedFeedIds, cancellationToken);
		string importedFeedsHtml = BuildImportedFeedsHtml(checkedFeeds, selectedFeedIds);
		string payload = SerializeImportedFeeds(checkedFeeds);
		int healthyCount = checkedFeeds.Count(feed => selectedFeedIds.Contains(feed.Id, StringComparer.Ordinal) && feed.HealthStatus == "healthy");
		int invalidCount = checkedFeeds.Count(feed => selectedFeedIds.Contains(feed.Id, StringComparer.Ordinal) && feed.HealthStatus == "invalid");
		int unhealthyCount = selectedFeedIds.Length - healthyCount - invalidCount;

		await sse.PatchElementsAsync(importedFeedsHtml, "#opml-import-results", "inner", cancellationToken);
		await sse.PatchSignalsAsync(new
		{
			opmlFeedPayload = payload,
			_healthStatus = $"Health check complete: {healthyCount.ToString(CultureInfo.InvariantCulture)} healthy, {unhealthyCount.ToString(CultureInfo.InvariantCulture)} unhealthy, {invalidCount.ToString(CultureInfo.InvariantCulture)} invalid."
		}, cancellationToken);
	}

	public static async Task SubscribeOpmlFeedsAsync(
		HttpRequest request,
		HttpResponse response,
		SqliteConnectionFactory connectionFactory,
		FeedIngestionService feedIngestionService,
		OpmlImportService opmlImportService,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);
		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);

		if (!TryGetImportedFeeds(signals, out IReadOnlyList<OpmlImportedFeedCandidate>? importedFeeds, out string? errorMessage))
		{
			await sse.PatchSignalsAsync(new { _subscribeStatus = $"Error: {errorMessage}" }, cancellationToken);
			return;
		}

		string[] selectedFeedIds = DatastarSignals.GetCsvValues(signals, "selectedOpmlFeedIds");
		if (selectedFeedIds.Length == 0)
		{
			await sse.PatchSignalsAsync(new { _subscribeStatus = "Error: Select at least one imported feed." }, cancellationToken);
			return;
		}

		HashSet<string> selectedSet = selectedFeedIds.ToHashSet(StringComparer.Ordinal);
		List<OpmlImportedFeedCandidate> updatedFeeds = new(importedFeeds!.Count);
		int addedCount = 0;
		int alreadySubscribedCount = 0;
		int failedCount = 0;
		foreach (OpmlImportedFeedCandidate importedFeed in importedFeeds)
		{
			if (!selectedSet.Contains(importedFeed.Id))
			{
				updatedFeeds.Add(importedFeed);
				continue;
			}

			RiverDataAccess.AddFeedResult addFeedResult = await RiverDataAccess.AddFeedAsync(
				connectionFactory,
				importedFeed.Url,
				importedFeed.Title,
				cancellationToken);
			if (addFeedResult.ErrorMessage is not null)
			{
				if (string.Equals(addFeedResult.ErrorMessage, "Feed URL already exists.", StringComparison.Ordinal))
				{
					alreadySubscribedCount++;
					updatedFeeds.Add(importedFeed with
					{
						AlreadySubscribed = true,
						Message = "Already subscribed."
					});
				}
				else
				{
					failedCount++;
					updatedFeeds.Add(importedFeed with
					{
						Message = addFeedResult.ErrorMessage
					});
				}

				continue;
			}

			addedCount++;
			FeedResponse addedFeed = addFeedResult.Feed!;
			RefreshResult refreshResult = await feedIngestionService.RefreshFeedAsync(addedFeed.Id, cancellationToken);
			string subscriptionMessage = refreshResult.FailedFeedCount > 0
				? "Subscribed, but the initial refresh failed."
				: "Subscribed and refreshed.";
			updatedFeeds.Add(importedFeed with
			{
				AlreadySubscribed = true,
				Message = subscriptionMessage
			});
		}

		IReadOnlyList<OpmlImportedFeedCandidate> refreshedFeeds = await opmlImportService.RefreshSubscriptionStateAsync(updatedFeeds, cancellationToken);
		string importedFeedsHtml = BuildImportedFeedsHtml(refreshedFeeds, selectedFeedIds);
		string payload = SerializeImportedFeeds(refreshedFeeds);
		string summary = $"Subscribed {addedCount.ToString(CultureInfo.InvariantCulture)} feed(s); {alreadySubscribedCount.ToString(CultureInfo.InvariantCulture)} already subscribed; {failedCount.ToString(CultureInfo.InvariantCulture)} failed.";

		await sse.PatchElementsAsync(importedFeedsHtml, "#opml-import-results", "inner", cancellationToken);
		await sse.PatchSignalsAsync(new
		{
			opmlFeedPayload = payload,
			_subscribeStatus = summary
		}, cancellationToken);
	}

	public static async Task GetItemsAsync(
		HttpRequest request,
		HttpResponse response,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);
		bool reset = request.Query["reset"] == "true";

		ItemsResult result = await BuildItemsHtmlAsync(signals, reset, connectionFactory, cancellationToken);

		SseHelper sseHelper = response.CreateSseHelper();
		await sseHelper.StartAsync(cancellationToken);

		if (result.FilterError is not null)
		{
			await sseHelper.PatchSignalsAsync(new { _filterError = result.FilterError }, cancellationToken);
			return;
		}

		string mode = reset ? "inner" : "append";
		await sseHelper.PatchElementsAsync(result.Html, "#items", mode, cancellationToken);

		int currentCount = reset ? result.Count : DatastarSignals.GetInt(signals, "itemsCount") + result.Count;

		await sseHelper.PatchSignalsAsync(new
		{
			cursor = result.NextCursor ?? "",
			itemsCount = currentCount,
			hasMore = result.HasMore,
			_filterError = ""
		}, cancellationToken);
	}

	public static async Task ClearFiltersAsync(
		HttpResponse response,
		SqliteConnectionFactory connectionFactory,
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
			itemsCount = 0,
			hasMore = false,
			_filterError = ""
		}, cancellationToken);

		string feedsHtml = await BuildFeedsHtmlAsync(connectionFactory, [], cancellationToken);
		await sse.PatchElementsAsync(feedsHtml, "#source-filters", "inner", cancellationToken);

		Dictionary<string, JsonElement> emptySignals = new();
		ItemsResult itemsResult = await BuildItemsHtmlAsync(emptySignals, true, connectionFactory, cancellationToken);
		await sse.PatchElementsAsync(itemsResult.Html, "#items", "inner", cancellationToken);
		await sse.PatchSignalsAsync(new
		{
			cursor = itemsResult.NextCursor ?? "",
			itemsCount = itemsResult.Count,
			hasMore = itemsResult.HasMore
		}, cancellationToken);
	}

	public static async Task AddFeedAsync(
		HttpRequest request,
		HttpResponse response,
		SqliteConnectionFactory connectionFactory,
		FeedIngestionService feedIngestionService,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);

		string url = DatastarSignals.GetString(signals, "addFeedUrl").Trim();
		string title = DatastarSignals.GetString(signals, "addFeedTitle").Trim();

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);

		if (string.IsNullOrWhiteSpace(url))
		{
			await sse.PatchSignalsAsync(new { _addFeedStatus = "Error: Feed URL is required." }, cancellationToken);
			return;
		}

		RiverDataAccess.AddFeedResult addFeedResult = await RiverDataAccess.AddFeedAsync(
			connectionFactory,
			url,
			title,
			cancellationToken);
		if (addFeedResult.ErrorMessage is not null)
		{
			await sse.PatchSignalsAsync(new { _addFeedStatus = $"Error: {addFeedResult.ErrorMessage}" }, cancellationToken);
			return;
		}

		FeedResponse addedFeed = addFeedResult.Feed!;
		RefreshResult refreshResult = await feedIngestionService.RefreshFeedAsync(addedFeed.Id, cancellationToken);
		string addFeedStatus = refreshResult.FailedFeedCount > 0
			? "Feed added, but the initial refresh failed."
			: "Feed added and refreshed.";

		await sse.PatchSignalsAsync(new
		{
			addFeedUrl = "",
			addFeedTitle = "",
			_addFeedStatus = addFeedStatus
		}, cancellationToken);

		string[] selectedFeedIds = DatastarSignals.GetCsvValues(signals, "selectedFeedIds");
		string feedsHtml = await BuildFeedsHtmlAsync(connectionFactory, selectedFeedIds, cancellationToken);
		await sse.PatchElementsAsync(feedsHtml, "#source-filters", "inner", cancellationToken);

		ItemsResult itemsResult = await BuildItemsHtmlAsync(signals, true, connectionFactory, cancellationToken);
		await sse.PatchElementsAsync(itemsResult.Html, "#items", "inner", cancellationToken);
		await sse.PatchSignalsAsync(new
		{
			cursor = itemsResult.NextCursor ?? "",
			itemsCount = itemsResult.Count,
			hasMore = itemsResult.HasMore
		}, cancellationToken);
	}

	public static async Task DeleteFeedAsync(
		string feedId,
		HttpRequest request,
		HttpResponse response,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);
		bool deleted = await RiverDataAccess.DeleteFeedAsync(connectionFactory, feedId, cancellationToken);

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);

		if (!deleted)
		{
			await sse.PatchSignalsAsync(new { _feedActionStatus = "Error: Feed not found." }, cancellationToken);
			return;
		}

		HashSet<string> selectedSet = DatastarSignals.GetCsvValues(signals, "selectedFeedIds").ToHashSet(StringComparer.Ordinal);
		selectedSet.Remove(feedId);
		string newSelected = DatastarSignals.ToCsv(selectedSet);

		await sse.PatchSignalsAsync(new
		{
			selectedFeedIds = newSelected,
			_feedActionStatus = "Feed deleted."
		}, cancellationToken);

		string feedsHtml = await BuildFeedsHtmlAsync(connectionFactory, selectedSet.ToArray(), cancellationToken);
		await sse.PatchElementsAsync(feedsHtml, "#source-filters", "inner", cancellationToken);

		Dictionary<string, JsonElement> updatedSignals = DatastarSignals.WithString(signals, "selectedFeedIds", newSelected);
		ItemsResult itemsResult = await BuildItemsHtmlAsync(updatedSignals, true, connectionFactory, cancellationToken);
		await sse.PatchElementsAsync(itemsResult.Html, "#items", "inner", cancellationToken);
		await sse.PatchSignalsAsync(new
		{
			cursor = itemsResult.NextCursor ?? "",
			itemsCount = itemsResult.Count,
			hasMore = itemsResult.HasMore
		}, cancellationToken);
	}

	public static async Task RefreshAsync(
		HttpRequest request,
		HttpResponse response,
		FeedIngestionService feedIngestionService,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		Dictionary<string, JsonElement> signals = await request.ReadSignalsAsync(cancellationToken);
		RefreshResult refreshResult = await feedIngestionService.RefreshAllFeedsAsync(cancellationToken);

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);
		await sse.PatchSignalsAsync(new
		{
			_refreshStatus = $"Done: {refreshResult.SuccessFeedCount} success, {refreshResult.FailedFeedCount} failed"
		}, cancellationToken);

		string[] selectedFeedIds = DatastarSignals.GetCsvValues(signals, "selectedFeedIds");
		string feedsHtml = await BuildFeedsHtmlAsync(connectionFactory, selectedFeedIds, cancellationToken);
		await sse.PatchElementsAsync(feedsHtml, "#source-filters", "inner", cancellationToken);

		ItemsResult itemsResult = await BuildItemsHtmlAsync(signals, true, connectionFactory, cancellationToken);
		await sse.PatchElementsAsync(itemsResult.Html, "#items", "inner", cancellationToken);
		await sse.PatchSignalsAsync(new
		{
			cursor = itemsResult.NextCursor ?? "",
			itemsCount = itemsResult.Count,
			hasMore = itemsResult.HasMore
		}, cancellationToken);
	}

	public static async Task ClearItemsAsync(
		HttpResponse response,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		const string deleteItemSourcesSql = "DELETE FROM item_sources;";
		const string deleteItemsSql = "DELETE FROM items;";

		await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
		await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

		await connection.ExecuteAsync(new CommandDefinition(deleteItemSourcesSql, transaction: transaction, cancellationToken: cancellationToken));
		await connection.ExecuteAsync(new CommandDefinition(deleteItemsSql, transaction: transaction, cancellationToken: cancellationToken));

		await transaction.CommitAsync(cancellationToken);

		SseHelper sse = response.CreateSseHelper();
		await sse.StartAsync(cancellationToken);

		await sse.PatchElementsAsync("", "#items", "inner", cancellationToken);
		await sse.PatchSignalsAsync(new
		{
			cursor = "",
			itemsCount = 0,
			hasMore = false,
			_clearStatus = "All items cleared."
		}, cancellationToken);
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
		RiverItemDetailResponse? item = await RiverDataAccess.GetItemByIdAsync(connectionFactory, itemId, cancellationToken);

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

	private static async Task<string> BuildFeedsHtmlAsync(
		SqliteConnectionFactory connectionFactory,
		IReadOnlyCollection<string> selectedFeedIds,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<FeedResponse> feeds = await RiverDataAccess.GetFeedsAsync(connectionFactory, cancellationToken);

		StringBuilder html = new();
		if (feeds.Count == 0)
		{
			html.AppendLine("<div class='text-xs text-slate-500'>No feeds added yet.</div>");
		}
		else
		{
			foreach (FeedResponse feed in feeds)
			{
				string encodedFeedId = HtmlEncoder.Default.Encode(feed.Id);
				string encodedCheckboxId = HtmlEncoder.Default.Encode($"feed_{feed.Id}");
				string encodedFeedTitle = HtmlEncoder.Default.Encode(feed.Title);
				string encodedFeedStatus = HtmlEncoder.Default.Encode(feed.Status);
				string checkedAttribute = selectedFeedIds.Contains(feed.Id) ? " checked" : string.Empty;
				html.AppendLine($"""<div class="mb-1 flex items-center justify-between gap-2 text-xs">""");
				html.AppendLine($"""  <label class="flex items-center gap-2">""");
				html.AppendLine($"""    <input type="checkbox" id="{encodedCheckboxId}" value="{encodedFeedId}" data-on:change="@get('/river/toggle-feed/{encodedFeedId}')" class="accent-sky-500"{checkedAttribute}>""");
				html.AppendLine($"""    <span>{encodedFeedTitle} <span class="text-slate-500">({encodedFeedStatus})</span></span>""");
				html.AppendLine($"""  </label>""");
				html.AppendLine($"""  <button data-on:click="confirm('Delete feed?') && @delete('/river/feeds/{encodedFeedId}')" class="rounded border border-rose-700 px-2 py-1 text-[11px] text-rose-300 hover:bg-rose-950">Delete</button>""");
				html.AppendLine($"""</div>""");
			}
		}

		return html.ToString();
	}

	private static async Task<ItemsResult> BuildItemsHtmlAsync(
		Dictionary<string, JsonElement> signals,
		bool reset,
		SqliteConnectionFactory connectionFactory,
		CancellationToken cancellationToken)
	{
		string currentCursor = reset ? string.Empty : DatastarSignals.GetString(signals, "cursor");
		string startDateRaw = DatastarSignals.GetString(signals, "startDate");
		string endDateRaw = DatastarSignals.GetString(signals, "endDate");
		string[] feedIds = DatastarSignals.GetCsvValues(signals, "selectedFeedIds");

		if (!TryParseDateTimeLocal(startDateRaw, out DateTimeOffset? startDate))
		{
			return new ItemsResult(string.Empty, null, false, 0, "Invalid start date.");
		}

		if (!TryParseDateTimeLocal(endDateRaw, out DateTimeOffset? endDate))
		{
			return new ItemsResult(string.Empty, null, false, 0, "Invalid end date.");
		}

		if (startDate.HasValue && endDate.HasValue && endDate.Value < startDate.Value)
		{
			return new ItemsResult("", null, false, 0, "Invalid date range. End date must be on or after start date.");
		}

		if (!RiverDataAccess.TryParseCursor(currentCursor, out _))
		{
			return new ItemsResult(string.Empty, null, false, 0, "Invalid cursor.");
		}

		const int limit = 50;
		RiverQueryResponse query = await RiverDataAccess.GetItemsAsync(
			connectionFactory,
			feedIds,
			startDate,
			endDate,
			limit,
			currentCursor,
			cancellationToken);
		IReadOnlyList<RiverItemResponse> items = query.Items;
		bool hasMore = !string.IsNullOrWhiteSpace(query.NextCursor);

		return new ItemsResult(BuildItemCardsHtml(items), query.NextCursor, hasMore, items.Count, null);
	}

	private static string BuildTopicListHtml(IReadOnlyList<TopicSuggestion> topics, string? selectedTopic)
	{
		StringBuilder html = new();
		foreach (TopicSuggestion topic in topics)
		{
			bool isSelected = string.Equals(topic.Name, selectedTopic, StringComparison.Ordinal);
			string containerClasses = isSelected
				? "border-sky-500 bg-sky-950 text-sky-100"
				: "border-slate-700 bg-slate-950 text-slate-100 hover:border-sky-600 hover:text-sky-200";
			string topicHref = $"/river/customize?topic={Uri.EscapeDataString(topic.Name)}";
			html.AppendLine($"""<a href="{HtmlEncoder.Default.Encode(topicHref)}" class="flex items-center justify-between rounded border px-3 py-2 text-sm {containerClasses}">""");
			html.AppendLine($"""  <span>{HtmlEncoder.Default.Encode(topic.Name)}</span>""");
			html.AppendLine($"""  <span class="text-xs text-slate-400">{topic.ItemCount.ToString(CultureInfo.InvariantCulture)} item(s)</span>""");
			html.AppendLine($"""</a>""");
		}

		return html.ToString();
	}

	private static string BuildImportedFeedsHtml(
		IReadOnlyList<OpmlImportedFeedCandidate> feeds,
		IReadOnlyCollection<string> selectedFeedIds)
	{
		if (feeds.Count == 0)
		{
			return """<div class="rounded border border-dashed border-slate-700 bg-slate-950 p-4 text-sm text-slate-400">No feeds were found in this OPML document.</div>""";
		}

		StringBuilder html = new();
		html.AppendLine("""<table class="min-w-full divide-y divide-slate-800 text-sm">""");
		html.AppendLine("""  <thead class="bg-slate-950 text-left text-xs uppercase tracking-wide text-slate-400">""");
		html.AppendLine("""    <tr>""");
		html.AppendLine("""      <th class="px-3 py-2">Select</th>""");
		html.AppendLine("""      <th class="px-3 py-2">Feed</th>""");
		html.AppendLine("""      <th class="px-3 py-2">Source</th>""");
		html.AppendLine("""      <th class="px-3 py-2">Subscription</th>""");
		html.AppendLine("""      <th class="px-3 py-2">Health</th>""");
		html.AppendLine("""      <th class="px-3 py-2">Format</th>""");
		html.AppendLine("""      <th class="px-3 py-2">Last updated</th>""");
		html.AppendLine("""      <th class="px-3 py-2">Notes</th>""");
		html.AppendLine("""    </tr>""");
		html.AppendLine("""  </thead>""");
		html.AppendLine("""  <tbody class="divide-y divide-slate-800">""");
		foreach (OpmlImportedFeedCandidate feed in feeds)
		{
			string encodedFeedId = HtmlEncoder.Default.Encode(feed.Id);
			string encodedTitle = HtmlEncoder.Default.Encode(feed.Title);
			string encodedUrl = HtmlEncoder.Default.Encode(feed.Url);
			string categoryPath = HtmlEncoder.Default.Encode(feed.CategoryPath ?? "Root");
			string note = HtmlEncoder.Default.Encode(feed.Message ?? string.Empty);
			string lastUpdated = string.IsNullOrWhiteSpace(feed.LastUpdatedAt)
				? "—"
				: HtmlEncoder.Default.Encode(FormatDate(feed.LastUpdatedAt));
			string properFormat = feed.ProperFormat switch
			{
				true => "Yes",
				false => "No",
				_ => "Unknown"
			};
			string checkedAttribute = selectedFeedIds.Contains(feed.Id) ? " checked" : string.Empty;
			string subscriptionText = feed.AlreadySubscribed ? "Already subscribed" : "Not subscribed";
			string subscriptionClass = feed.AlreadySubscribed ? "text-emerald-300" : "text-slate-400";
			html.AppendLine("""    <tr class="align-top">""");
			html.AppendLine($"""      <td class="px-3 py-3"><input type="checkbox" value="{encodedFeedId}" data-on:change="@post('/river/opml/toggle-feed/{encodedFeedId}')" class="accent-sky-500"{checkedAttribute}></td>""");
			html.AppendLine("""      <td class="px-3 py-3">""");
			html.AppendLine($"""        <div class="font-semibold text-slate-100">{encodedTitle}</div>""");
			html.AppendLine($"""        <div class="mt-1 break-all text-xs text-slate-400">{encodedUrl}</div>""");
			if (!string.IsNullOrWhiteSpace(feed.SiteUrl))
			{
				html.AppendLine($"""        <a href="{HtmlEncoder.Default.Encode(feed.SiteUrl)}" target="_blank" rel="noreferrer" class="mt-1 inline-block text-xs text-sky-400 hover:text-sky-300">Open site</a>""");
			}

			html.AppendLine("""      </td>""");
			html.AppendLine($"""      <td class="px-3 py-3 text-xs text-slate-400">{categoryPath}</td>""");
			html.AppendLine($"""      <td class="px-3 py-3 text-xs {subscriptionClass}">{subscriptionText}</td>""");
			html.AppendLine($"""      <td class="px-3 py-3 text-xs">{BuildHealthBadgeHtml(feed.HealthStatus)}</td>""");
			html.AppendLine($"""      <td class="px-3 py-3 text-xs text-slate-300">{properFormat}</td>""");
			html.AppendLine($"""      <td class="px-3 py-3 text-xs text-slate-300">{lastUpdated}</td>""");
			html.AppendLine($"""      <td class="px-3 py-3 text-xs text-slate-400">{note}</td>""");
			html.AppendLine("""    </tr>""");
		}

		html.AppendLine("""  </tbody>""");
		html.AppendLine("""</table>""");
		return html.ToString();
	}

	private static string BuildItemCardsHtml(IReadOnlyList<RiverItemResponse> items)
	{
		StringBuilder html = new();
		foreach (RiverItemResponse item in items)
		{
			string sourceText = HtmlEncoder.Default.Encode(item.SourceNames ?? "Unknown source");
			string detailsLink = $"/river/items/{Uri.EscapeDataString(item.Id)}";
			string encodedDetailsLink = HtmlEncoder.Default.Encode(detailsLink);
			string encodedTitle = HtmlEncoder.Default.Encode(item.Title);
			string snippet = HtmlEncoder.Default.Encode(item.Snippet ?? string.Empty);
			string imageBlock = item.ImageUrl is not null
				? $"""<img class="mb-3 h-auto w-auto max-w-full rounded" src="{HtmlEncoder.Default.Encode(item.ImageUrl)}" alt="">"""
				: "";
			string? articleLink = item.CanonicalUrl ?? item.Url;

			html.AppendLine($"""<article class="rounded border border-slate-800 bg-slate-900 p-4">""");
			html.AppendLine($"""  <div class="mb-1 text-xs text-slate-400">{sourceText}</div>""");
			html.AppendLine($"""  <h2 class="mb-2 text-lg font-semibold"><a class="text-slate-100 hover:text-sky-300" href="{encodedDetailsLink}">{encodedTitle}</a></h2>""");
			html.AppendLine($"""  <div class="mb-2 text-xs text-slate-400">{FormatDate(item.PublishedAt)}</div>""");
			if (!string.IsNullOrEmpty(imageBlock))
			{
				html.AppendLine($"""  {imageBlock}""");
			}

			html.AppendLine($"""  <p class="mb-3 text-sm text-slate-300">{snippet}</p>""");
			if (string.IsNullOrWhiteSpace(articleLink))
			{
				html.AppendLine($"""  <p class="text-sm text-slate-500">No article URL available for this item.</p>""");
			}
			else
			{
				html.AppendLine($"""  <a class="text-sm text-sky-400 hover:text-sky-300" href="{HtmlEncoder.Default.Encode(articleLink)}" target="_blank" rel="noreferrer">Open article</a>""");
			}

			html.AppendLine($"""</article>""");
		}

		return html.ToString();
	}

	private static string BuildHealthBadgeHtml(string healthStatus)
	{
		string normalizedStatus = string.IsNullOrWhiteSpace(healthStatus)
			? "unchecked"
			: healthStatus.Trim().ToLowerInvariant();
		string badgeClasses = normalizedStatus switch
		{
			"healthy" => "border-emerald-700 bg-emerald-950 text-emerald-300",
			"unhealthy" => "border-rose-700 bg-rose-950 text-rose-300",
			"invalid" => "border-amber-700 bg-amber-950 text-amber-300",
			_ => "border-slate-700 bg-slate-950 text-slate-300"
		};
		string label = normalizedStatus switch
		{
			"healthy" => "Healthy",
			"unhealthy" => "Unhealthy",
			"invalid" => "Invalid URL",
			_ => "Unchecked"
		};
		return $"""<span class="inline-flex rounded border px-2 py-1 {badgeClasses}">{HtmlEncoder.Default.Encode(label)}</span>""";
	}

	private static bool TryGetImportedFeeds(
		IReadOnlyDictionary<string, JsonElement> signals,
		out IReadOnlyList<OpmlImportedFeedCandidate>? importedFeeds,
		out string? errorMessage)
	{
		importedFeeds = null;
		errorMessage = null;
		string payload = DatastarSignals.GetString(signals, "opmlFeedPayload");
		if (string.IsNullOrWhiteSpace(payload))
		{
			errorMessage = "Fetch an OPML document first.";
			return false;
		}

		try
		{
			importedFeeds = JsonSerializer.Deserialize<List<OpmlImportedFeedCandidate>>(payload);
		}
		catch (JsonException)
		{
			errorMessage = "Imported feed state is invalid.";
			return false;
		}

		if (importedFeeds is null)
		{
			errorMessage = "Imported feed state is missing.";
			return false;
		}

		return true;
	}

	private static string SerializeImportedFeeds(IReadOnlyList<OpmlImportedFeedCandidate> feeds)
	{
		return JsonSerializer.Serialize(feeds);
	}

	private sealed record ItemsResult(string Html, string? NextCursor, bool HasMore, int Count, string? FilterError);

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

	private static bool TryParseDateTimeLocal(string value, out DateTimeOffset? parsedValueUtc)
	{
		parsedValueUtc = null;
		if (string.IsNullOrWhiteSpace(value))
		{
			return true;
		}

		if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime parsedLocal))
		{
			return false;
		}

		if (parsedLocal.Kind == DateTimeKind.Utc)
		{
			parsedValueUtc = new DateTimeOffset(parsedLocal, TimeSpan.Zero);
			return true;
		}

		DateTime localTime = parsedLocal.Kind == DateTimeKind.Unspecified
			? DateTime.SpecifyKind(parsedLocal, DateTimeKind.Local)
			: parsedLocal.ToLocalTime();

		parsedValueUtc = new DateTimeOffset(localTime).ToUniversalTime();
		return true;
	}
}
