using System.Globalization;
using Microsoft.Data.Sqlite;
using Rocket.RiverOfNews.Configuration;
using Rocket.RiverOfNews.Data;
using Rocket.RiverOfNews.Services;
using Rocket.Syndication;
using Rocket.Syndication.Authentication;
using Rocket.Syndication.Models;
using TUnit.Core;

namespace Rocket.RiverOfNews.Tests;

public class FeedIngestionServiceTests
{
[Test]
public async Task RefreshAllFeedsAsync_UsesImagePriorityAndSnippetThreshold()
{
await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
await SeedFeedAsync(database.ConnectionFactory);

DateTimeOffset now = DateTimeOffset.UtcNow;
		Feed feed = new()
		{
			Title = "Example feed",
			Items =
			[
new FeedItem
{
Id = "item-short",
Title = "Short item",
Link = new Uri("https://example.com/short"),
PublishedDate = now,
Content = new FeedContent
{
PlainText = new string('a', 999),
Html = "<p>short</p><img src='https://example.com/fallback-short.png'>"
},
Media = new FeedMediaContent
{
ThumbnailUrl = new Uri("https://example.com/media-short.png")
}
},
new FeedItem
{
Id = "item-long",
Title = "Long item",
Link = new Uri("https://example.com/long"),
PublishedDate = now.AddMinutes(-1),
Content = new FeedContent
{
PlainText = new string('b', 1001),
Html = "<p>long</p><img src='https://example.com/fallback-long.png'>"
}
}
]
};

		FeedIngestionService service = new(database.ConnectionFactory, new StubSyndicationClient(feed), new RiverOfNewsSettings());
await service.RefreshAllFeedsAsync(CancellationToken.None);

(string? shortSnippet, string? shortImageUrl) = await GetItemDataByGuidAsync(database.ConnectionFactory, "item-short");
(string? longSnippet, string? longImageUrl) = await GetItemDataByGuidAsync(database.ConnectionFactory, "item-long");

await Assert.That(shortSnippet).IsEqualTo(new string('a', 999));
await Assert.That(shortImageUrl).IsEqualTo("https://example.com/media-short.png");
await Assert.That(longSnippet).IsEqualTo($"{new string('b', 1000)}...");
await Assert.That(longImageUrl).IsEqualTo("https://example.com/fallback-long.png");
}

private static async Task SeedFeedAsync(SqliteConnectionFactory connectionFactory)
{
string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
await using SqliteCommand command = connection.CreateCommand();
command.CommandText = """
INSERT INTO feeds (id, url, normalized_url, title, status, consecutive_failures, created_at, updated_at)
VALUES ('feed-1', 'https://example.com/feed.xml', 'https://example.com/feed.xml', 'Example Feed', 'healthy', 0, $now, $now);
""";
command.Parameters.AddWithValue("$now", now);
await command.ExecuteNonQueryAsync(CancellationToken.None);
}

private static async Task<(string? snippet, string? imageUrl)> GetItemDataByGuidAsync(SqliteConnectionFactory connectionFactory, string guid)
{
await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
await using SqliteCommand command = connection.CreateCommand();
command.CommandText = "SELECT snippet, image_url FROM items WHERE guid = $guid LIMIT 1;";
command.Parameters.AddWithValue("$guid", guid);
await using SqliteDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);
await reader.ReadAsync(CancellationToken.None);
return (
reader.IsDBNull(0) ? null : reader.GetString(0),
reader.IsDBNull(1) ? null : reader.GetString(1));
}

private sealed class StubSyndicationClient(Feed feed) : ISyndicationClient
{
private readonly Feed Feed = feed;

public Task<FeedResult> GetFeedAsync(string url, CancellationToken cancellationToken)
{
return Task.FromResult(new FeedResult
{
IsSuccess = true,
Feed = Feed
});
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
}
