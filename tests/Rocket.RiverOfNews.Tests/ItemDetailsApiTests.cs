using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Rocket.RiverOfNews.Api;
using Rocket.RiverOfNews.Data;
using TUnit.Core;

namespace Rocket.RiverOfNews.Tests;

public class ItemDetailsApiTests
{
	[Test]
	public async Task GetItemByIdAsync_WithExistingItem_ReturnsItemDetails()
	{
		await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
		await SeedItemAsync(database.ConnectionFactory, "item-1");

		IResult result = await MvpApi.GetItemByIdAsync("item-1", database.ConnectionFactory, CancellationToken.None);
		(int statusCode, JsonElement body) = await ExecuteResultAsync(result);

		await Assert.That(statusCode).IsEqualTo(StatusCodes.Status200OK);
		await Assert.That(body.GetProperty("id").GetString()).IsEqualTo("item-1");
		await Assert.That(body.GetProperty("title").GetString()).IsEqualTo("Example title");
		await Assert.That(body.GetProperty("url").GetString()).IsEqualTo("https://example.com/original");
		await Assert.That(body.GetProperty("canonicalUrl").GetString()).IsEqualTo("https://example.com/canonical");
		await Assert.That(body.GetProperty("imageUrl").GetString()).IsEqualTo("https://example.com/image.png");
		await Assert.That(body.GetProperty("sourceNames").GetString()).IsEqualTo("Example Feed");
	}

	[Test]
	public async Task GetItemByIdAsync_WithUnknownItem_ReturnsNotFound()
	{
		await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
		IResult result = await MvpApi.GetItemByIdAsync("missing-item", database.ConnectionFactory, CancellationToken.None);
		(int statusCode, JsonElement body) = await ExecuteResultAsync(result);

		await Assert.That(statusCode).IsEqualTo(StatusCodes.Status404NotFound);
		await Assert.That(body.GetProperty("message").GetString()).IsEqualTo("Item not found.");
	}

	private static async Task SeedItemAsync(SqliteConnectionFactory connectionFactory, string itemId)
	{
		string now = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
		await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
			INSERT INTO feeds (id, url, normalized_url, title, status, consecutive_failures, created_at, updated_at)
			VALUES ('feed-1', 'https://example.com/feed.xml', 'https://example.com/feed.xml', 'Example Feed', 'healthy', 0, $now, $now);

			INSERT INTO items (id, canonical_key, guid, url, canonical_url, image_url, title, snippet, published_at, ingested_at, created_at, updated_at)
			VALUES ($itemId, 'guid:example', 'guid:example', 'https://example.com/original', 'https://example.com/canonical', 'https://example.com/image.png', 'Example title', 'Example snippet', $now, $now, $now, $now);

			INSERT INTO item_sources (item_id, feed_id, source_item_guid, source_item_url, first_seen_at)
			VALUES ($itemId, 'feed-1', 'guid:example', 'https://example.com/original', $now);
			""";
		command.Parameters.AddWithValue("$itemId", itemId);
		command.Parameters.AddWithValue("$now", now);
		await command.ExecuteNonQueryAsync(CancellationToken.None);
	}

	private static async Task<(int statusCode, JsonElement body)> ExecuteResultAsync(IResult result)
	{
		DefaultHttpContext httpContext = new();
		ServiceCollection services = new();
		services.AddLogging();
		services.AddOptions();
		httpContext.RequestServices = services.BuildServiceProvider();
		await using MemoryStream bodyStream = new();
		httpContext.Response.Body = bodyStream;

		await result.ExecuteAsync(httpContext);
		bodyStream.Position = 0;
		using JsonDocument document = await JsonDocument.ParseAsync(bodyStream);
		return (httpContext.Response.StatusCode, document.RootElement.Clone());
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
