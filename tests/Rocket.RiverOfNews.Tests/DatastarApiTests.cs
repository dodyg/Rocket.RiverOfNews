using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Rocket.RiverOfNews.Api;
using Rocket.RiverOfNews.Configuration;
using Rocket.RiverOfNews.Data;
using Rocket.RiverOfNews.Services;
using Rocket.Syndication;
using Rocket.Syndication.Authentication;
using Rocket.Syndication.Models;
using TUnit.Core;

namespace Rocket.RiverOfNews.Tests;

public class DatastarApiTests
{
[Test]
public async Task GetFeedsAsync_WithSelectedFeedIds_RendersCheckedCheckbox()
{
await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
await SeedFeedAsync(database.ConnectionFactory, "feed-1", "Example Feed");

DefaultHttpContext httpContext = CreateHttpContext();
httpContext.Request.QueryString = CreateDatastarQueryString("{\"selectedFeedIds\":\"feed-1\"}");

await DatastarApi.GetFeedsAsync(httpContext.Request, httpContext.Response, database.ConnectionFactory, CancellationToken.None);
string body = await ReadResponseAsync(httpContext.Response);

await Assert.That(body.Contains("value=\"feed-1\"")).IsTrue();
await Assert.That(body.Contains("checked")).IsTrue();
}

[Test]
public async Task GetItemsAsync_WithMissingArticleUrl_ShowsVisibleFallbackText()
{
await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
		await SeedItemAsync(database.ConnectionFactory, "feed-1", "item-1", "No Link Item", string.Empty, null);

DefaultHttpContext httpContext = CreateHttpContext();

await DatastarApi.GetItemsAsync(httpContext.Request, httpContext.Response, database.ConnectionFactory, CancellationToken.None);
string body = await ReadResponseAsync(httpContext.Response);

await Assert.That(body.Contains("No article URL available for this item.")).IsTrue();
await Assert.That(body.Contains("href=\"#\"")).IsFalse();
}

[Test]
public async Task RefreshAsync_PreservesSelectedFeedFilterWhenRebuildingItems()
{
await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
await SeedItemAsync(database.ConnectionFactory, "feed-1", "item-1", "Selected Item", "https://example.com/selected", "https://example.com/selected");
await SeedItemAsync(database.ConnectionFactory, "feed-2", "item-2", "Other Item", "https://example.com/other", "https://example.com/other");

FeedIngestionService service = new(database.ConnectionFactory, new StubSyndicationClient(new Dictionary<string, Feed>()), new RiverOfNewsSettings());
DefaultHttpContext httpContext = CreateHttpContext();
httpContext.Request.QueryString = CreateDatastarQueryString("{\"selectedFeedIds\":\"feed-1\"}");

await DatastarApi.RefreshAsync(httpContext.Request, httpContext.Response, service, database.ConnectionFactory, CancellationToken.None);
string body = await ReadResponseAsync(httpContext.Response);

await Assert.That(body.Contains("Selected Item")).IsTrue();
await Assert.That(body.Contains("Other Item")).IsFalse();
}

[Test]
public async Task AddFeedAsync_RefreshesNewFeedBeforeReloadingRiver()
{
await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
Feed seededFeed = new()
{
Title = "Example Feed",
Items =
[
new FeedItem
{
Id = "item-1",
Title = "Fresh Item",
Link = new Uri("https://example.com/articles/fresh"),
PublishedDate = DateTimeOffset.UtcNow,
Content = new FeedContent
{
PlainText = "Fresh content"
}
}
]
};
FeedIngestionService service = new(
database.ConnectionFactory,
new StubSyndicationClient(new Dictionary<string, Feed>
{
["https://example.com/feed.xml"] = seededFeed
}),
new RiverOfNewsSettings());
DefaultHttpContext httpContext = CreateHttpContext();
SetJsonBody(httpContext, "{\"addFeedUrl\":\"https://example.com/feed.xml\",\"addFeedTitle\":\"Example Feed\"}");

await DatastarApi.AddFeedAsync(httpContext.Request, httpContext.Response, database.ConnectionFactory, service, CancellationToken.None);
string body = await ReadResponseAsync(httpContext.Response);

await Assert.That(body.Contains("Feed added and refreshed.")).IsTrue();
await Assert.That(await CountItemsAsync(database.ConnectionFactory)).IsEqualTo(1);
}

[Test]
public async Task FetchOpmlAsync_LoadsRemoteOpmlCandidates()
{
await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
OpmlImportService service = CreateOpmlImportService(
	database.ConnectionFactory,
	new StubHttpMessageHandler(CreateStringResponse(SampleOpml, "text/xml")),
	new StubSyndicationClient(new Dictionary<string, Feed>()));
DefaultHttpContext httpContext = CreateHttpContext();
SetJsonBody(httpContext, "{\"opmlUrl\":\"https://example.com/feeds.opml\"}");

await DatastarApi.FetchOpmlAsync(httpContext.Request, httpContext.Response, service, CancellationToken.None);
string body = await ReadResponseAsync(httpContext.Response);

await Assert.That(body.Contains("Loaded 2 feed(s) from OPML.")).IsTrue();
await Assert.That(body.Contains("BBC World")).IsTrue();
await Assert.That(body.Contains("Tech Feed")).IsTrue();
await Assert.That(body.Contains("@post('/river/opml/toggle-feed/")).IsTrue();
}

[Test]
public async Task CheckOpmlFeedsAsync_ReportsHealthySelectedFeed()
{
await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
OpmlImportService service = CreateOpmlImportService(
	database.ConnectionFactory,
	new StubHttpMessageHandler(CreateStringResponse(string.Empty, "text/plain")),
	new StubSyndicationClient(new Dictionary<string, Feed>
	{
		["https://example.com/bbc.xml"] = new Feed
		{
			Title = "BBC World",
			Type = FeedType.Rss,
			LastUpdated = DateTimeOffset.UtcNow,
			Items =
			[
				new FeedItem
				{
					Id = "item-1",
					Title = "Headline",
					PublishedDate = DateTimeOffset.UtcNow
				}
			]
		}
	}));
DefaultHttpContext httpContext = CreateHttpContext();
string selectedId = CreateImportedFeedId("https://example.com/bbc.xml");
SetJsonBody(
	httpContext,
	JsonSerializer.Serialize(new
	{
		opmlFeedPayload = JsonSerializer.Serialize(new[]
		{
			CreateImportedFeedCandidate("BBC World", "https://example.com/bbc.xml")
		}),
		selectedOpmlFeedIds = selectedId
	}));

await DatastarApi.CheckOpmlFeedsAsync(httpContext.Request, httpContext.Response, service, CancellationToken.None);
string body = await ReadResponseAsync(httpContext.Response);

await Assert.That(body.Contains("Health check complete: 1 healthy, 0 unhealthy, 0 invalid.")).IsTrue();
await Assert.That(body.Contains("Healthy")).IsTrue();
await Assert.That(body.Contains("Feed parsed as Rss with 1 item(s).")).IsTrue();
}

[Test]
public async Task SubscribeOpmlFeedsAsync_AddsSelectedFeedsAndRefreshesThem()
{
await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
FeedIngestionService feedIngestionService = new(
	database.ConnectionFactory,
	new StubSyndicationClient(new Dictionary<string, Feed>
	{
		["https://example.com/bbc.xml"] = new Feed
		{
			Title = "BBC World",
			Type = FeedType.Rss,
			Items =
			[
				new FeedItem
				{
					Id = "item-1",
					Title = "Fresh OPML Item",
					Link = new Uri("https://example.com/articles/fresh-opml"),
					PublishedDate = DateTimeOffset.UtcNow,
					Content = new FeedContent
					{
						PlainText = "Fresh content"
					}
				}
			]
		}
	}),
	new RiverOfNewsSettings());
OpmlImportService opmlImportService = CreateOpmlImportService(
	database.ConnectionFactory,
	new StubHttpMessageHandler(CreateStringResponse(string.Empty, "text/plain")),
	new StubSyndicationClient(new Dictionary<string, Feed>()));
DefaultHttpContext httpContext = CreateHttpContext();
string selectedId = CreateImportedFeedId("https://example.com/bbc.xml");
SetJsonBody(
	httpContext,
	JsonSerializer.Serialize(new
	{
		opmlFeedPayload = JsonSerializer.Serialize(new[]
		{
			CreateImportedFeedCandidate("BBC World", "https://example.com/bbc.xml")
		}),
		selectedOpmlFeedIds = selectedId
	}));

await DatastarApi.SubscribeOpmlFeedsAsync(
	httpContext.Request,
	httpContext.Response,
	database.ConnectionFactory,
	feedIngestionService,
	opmlImportService,
	CancellationToken.None);
string body = await ReadResponseAsync(httpContext.Response);

await Assert.That(body.Contains("Subscribed 1 feed(s); 0 already subscribed; 0 failed.")).IsTrue();
await Assert.That(body.Contains("Subscribed and refreshed.")).IsTrue();
await Assert.That(await CountFeedsAsync(database.ConnectionFactory)).IsEqualTo(1);
await Assert.That(await CountItemsAsync(database.ConnectionFactory)).IsEqualTo(1);
}

private static DefaultHttpContext CreateHttpContext()
{
DefaultHttpContext httpContext = new();
httpContext.Response.Body = new MemoryStream();
return httpContext;
}

private static QueryString CreateDatastarQueryString(string json)
{
return new QueryString($"?datastar={Uri.EscapeDataString(json)}");
}

private static void SetJsonBody(DefaultHttpContext httpContext, string json)
{
byte[] payload = Encoding.UTF8.GetBytes(json);
httpContext.Request.Body = new MemoryStream(payload);
httpContext.Request.ContentType = "application/json";
httpContext.Request.ContentLength = payload.Length;
}

private static async Task<string> ReadResponseAsync(HttpResponse response)
{
response.Body.Position = 0;
using StreamReader reader = new(response.Body, Encoding.UTF8, leaveOpen: true);
return await reader.ReadToEndAsync();
}

private static async Task SeedFeedAsync(SqliteConnectionFactory connectionFactory, string feedId, string title)
{
string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
await using SqliteCommand command = connection.CreateCommand();
command.CommandText = """
INSERT INTO feeds (id, url, normalized_url, title, status, consecutive_failures, created_at, updated_at)
VALUES ($feedId, $url, $url, $title, 'healthy', 0, $now, $now);
""";
command.Parameters.AddWithValue("$feedId", feedId);
command.Parameters.AddWithValue("$url", $"https://example.com/{feedId}.xml");
command.Parameters.AddWithValue("$title", title);
command.Parameters.AddWithValue("$now", now);
await command.ExecuteNonQueryAsync(CancellationToken.None);
}

private static async Task SeedItemAsync(
SqliteConnectionFactory connectionFactory,
string feedId,
string itemId,
string title,
string? url,
string? canonicalUrl)
{
string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
await SeedFeedAsync(connectionFactory, feedId, $"Feed {feedId}");
await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
await using SqliteCommand command = connection.CreateCommand();
command.CommandText = """
INSERT INTO items (id, canonical_key, guid, url, canonical_url, image_url, title, snippet, published_at, ingested_at, created_at, updated_at)
VALUES ($itemId, $canonicalKey, $guid, $url, $canonicalUrl, NULL, $title, 'Example snippet', $now, $now, $now, $now);

INSERT INTO item_sources (item_id, feed_id, source_item_guid, source_item_url, first_seen_at)
VALUES ($itemId, $feedId, $guid, $url, $now);
""";
command.Parameters.AddWithValue("$itemId", itemId);
command.Parameters.AddWithValue("$canonicalKey", $"guid:{itemId}");
command.Parameters.AddWithValue("$guid", itemId);
command.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
command.Parameters.AddWithValue("$canonicalUrl", (object?)canonicalUrl ?? DBNull.Value);
command.Parameters.AddWithValue("$title", title);
command.Parameters.AddWithValue("$feedId", feedId);
command.Parameters.AddWithValue("$now", now);
await command.ExecuteNonQueryAsync(CancellationToken.None);
}

private static async Task<int> CountItemsAsync(SqliteConnectionFactory connectionFactory)
{
await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
await using SqliteCommand command = connection.CreateCommand();
command.CommandText = "SELECT COUNT(*) FROM items;";
object? scalar = await command.ExecuteScalarAsync(CancellationToken.None);
return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
}

private static async Task<int> CountFeedsAsync(SqliteConnectionFactory connectionFactory)
{
await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
await using SqliteCommand command = connection.CreateCommand();
command.CommandText = "SELECT COUNT(*) FROM feeds;";
object? scalar = await command.ExecuteScalarAsync(CancellationToken.None);
return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
}

private static OpmlImportService CreateOpmlImportService(
SqliteConnectionFactory connectionFactory,
HttpMessageHandler handler,
ISyndicationClient syndicationClient)
{
HttpClient httpClient = new(handler)
{
BaseAddress = new Uri("https://example.com/")
};
return new OpmlImportService(httpClient, connectionFactory, syndicationClient);
}

private static HttpResponseMessage CreateStringResponse(string content, string mediaType)
{
return new HttpResponseMessage(HttpStatusCode.OK)
{
Content = new StringContent(content, Encoding.UTF8, mediaType)
};
}

private static OpmlImportedFeedCandidate CreateImportedFeedCandidate(string title, string url)
{
return new OpmlImportedFeedCandidate
{
Id = CreateImportedFeedId(url),
Title = title,
Url = url,
NormalizedUrl = url,
SiteUrl = null,
CategoryPath = "News",
HealthStatus = "unchecked",
ProperFormat = null,
LastUpdatedAt = null,
Message = null,
AlreadySubscribed = false
};
}

private static string CreateImportedFeedId(string normalizedUrl)
{
byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl));
return Convert.ToHexString(hash)[..16].ToLowerInvariant();
}

private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
{
private readonly HttpResponseMessage Response = response;

protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
{
return Task.FromResult(Response);
}
}

private sealed class StubSyndicationClient(IDictionary<string, Feed> feedsByUrl) : ISyndicationClient
{
private readonly IDictionary<string, Feed> FeedsByUrl = feedsByUrl;

		public Task<FeedResult> GetFeedAsync(string url, CancellationToken cancellationToken)
		{
			bool found = FeedsByUrl.TryGetValue(url, out Feed? feed);
			return Task.FromResult(found
				? new FeedResult { IsSuccess = true, Feed = feed }
				: new FeedResult { IsSuccess = true, Feed = new Feed { Title = string.Empty, Items = [] } });
		}

public Task<FeedResult> GetFeedAsync(Uri url, CancellationToken cancellationToken)
{
return GetFeedAsync(url.AbsoluteUri, cancellationToken);
}

public Task<FeedResult> GetFeedAsync(string url, FeedCredentials credentials, CancellationToken cancellationToken)
{
return GetFeedAsync(url, cancellationToken);
}

public Task<FeedResult> ParseFeedAsync(string content, CancellationToken cancellationToken)
{
throw new NotSupportedException();
}

public Task<FeedResult> ParseFeedAsync(Stream stream, CancellationToken cancellationToken)
{
throw new NotSupportedException();
}

public Task<IReadOnlyList<FeedLink>> DiscoverFeedsAsync(string websiteUrl, CancellationToken cancellationToken)
{
throw new NotSupportedException();
}
}

private sealed class TestDatabaseContext : IAsyncDisposable
{
public required string RootPath { get; init; }
public required SqliteConnectionFactory ConnectionFactory { get; init; }

public static async Task<TestDatabaseContext> CreateAsync()
{
string rootPath = Path.Combine(Path.GetTempPath(), "Rocket.RiverOfNews.Tests", Guid.NewGuid().ToString("N"));
string databasePath = Path.Combine(rootPath, "db", "river.test.db");
string migrationsPath = Path.Combine(GetRepositoryRootPath(), "db", "migrations");

await SqliteDatabaseBootstrapper.InitializeAsync(databasePath, migrationsPath, CancellationToken.None);
return new TestDatabaseContext
{
RootPath = rootPath,
ConnectionFactory = new SqliteConnectionFactory(databasePath)
};
}

public ValueTask DisposeAsync()
{
if (Directory.Exists(RootPath))
{
Directory.Delete(RootPath, recursive: true);
}

return ValueTask.CompletedTask;
}
}

private static string GetRepositoryRootPath()
{
DirectoryInfo? current = new(AppContext.BaseDirectory);
while (current is not null)
{
string candidate = Path.Combine(current.FullName, "Rocket.RiverOfNews.slnx");
if (File.Exists(candidate))
{
return current.FullName;
}

current = current.Parent;
}

throw new DirectoryNotFoundException("Could not find Rocket.RiverOfNews.slnx from test base directory.");
}

private const string SampleOpml = """
<?xml version="1.0" encoding="UTF-8"?>
<opml version="2.0">
	<head>
		<title>Example feed bundle</title>
	</head>
	<body>
		<outline text="News">
			<outline text="BBC World" title="BBC World" type="rss" xmlUrl="https://example.com/bbc.xml" htmlUrl="https://example.com/bbc" />
			<outline text="Tech Feed" title="Tech Feed" type="rss" xmlUrl="https://example.com/tech.xml" />
		</outline>
	</body>
</opml>
""";
}
