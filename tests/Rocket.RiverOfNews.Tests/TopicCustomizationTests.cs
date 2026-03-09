using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Rocket.RiverOfNews.Api;
using Rocket.RiverOfNews.Data;
using Rocket.RiverOfNews.Services;
using TUnit.Core;

namespace Rocket.RiverOfNews.Tests;

public class TopicCustomizationTests
{
	[Test]
	public async Task BuildPageModelAsync_SelectsItemsFromTopicContext()
	{
		await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
		await SeedItemsAsync(database.ConnectionFactory);
		TopicCustomizationService service = new(database.ConnectionFactory);

		CustomizePageModel model = await service.BuildPageModelAsync("Machine Learning", CancellationToken.None);

		await Assert.That(model.SelectedTopic).IsEqualTo("machine learning");
		await Assert.That(model.Topics.Any(static topic => topic.Name == "machine learning")).IsTrue();
		await Assert.That(model.MatchingItems.Count).IsEqualTo(2);
		await Assert.That(model.MatchingItems.Any(static item => item.Title == "Machine Learning Breakthroughs in Robotics")).IsTrue();
		await Assert.That(model.MatchingItems.Any(static item => item.Title == "Machine Learning Chips Power Edge Devices")).IsTrue();
		await Assert.That(model.MatchingItems.Any(static item => item.Title == "Local Gardening Calendar for Spring")).IsFalse();
	}

	[Test]
	public async Task GetCustomizePage_RendersSuggestedTopicsAndMatchingItems()
	{
		await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
		await SeedItemsAsync(database.ConnectionFactory);
		TopicCustomizationService service = new(database.ConnectionFactory);

		DefaultHttpContext requestContext = new();
		requestContext.Request.QueryString = new QueryString("?topic=machine%20learning");
		IResult result = await DatastarApi.GetCustomizePage(requestContext.Request, service, CancellationToken.None);
		string body = await ExecuteResultAsync(result);

		await Assert.That(body.Contains("Customize your river")).IsTrue();
		await Assert.That(body.Contains("machine learning")).IsTrue();
		await Assert.That(body.Contains("Machine Learning Breakthroughs in Robotics")).IsTrue();
		await Assert.That(body.Contains("Machine Learning Chips Power Edge Devices")).IsTrue();
		await Assert.That(body.Contains("Local Gardening Calendar for Spring")).IsFalse();
	}

	private static async Task SeedItemsAsync(SqliteConnectionFactory connectionFactory)
	{
		string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
		string oneHourAgo = DateTimeOffset.UtcNow.AddHours(-1).ToString("O", CultureInfo.InvariantCulture);
		string twoHoursAgo = DateTimeOffset.UtcNow.AddHours(-2).ToString("O", CultureInfo.InvariantCulture);

		await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
			INSERT INTO feeds (id, url, normalized_url, title, status, consecutive_failures, created_at, updated_at)
			VALUES ('feed-1', 'https://example.com/feed.xml', 'https://example.com/feed.xml', 'Example Feed', 'healthy', 0, $now, $now);

			INSERT INTO items (id, canonical_key, guid, url, canonical_url, title, snippet, published_at, ingested_at, created_at, updated_at)
			VALUES
				('item-1', 'guid:item-1', 'item-1', 'https://example.com/robotics', 'https://example.com/robotics', 'Machine Learning Breakthroughs in Robotics', 'A deep dive into neural models for factory automation.', $now, $now, $now, $now),
				('item-2', 'guid:item-2', 'item-2', 'https://example.com/chips', 'https://example.com/chips', 'Machine Learning Chips Power Edge Devices', 'Smaller inference hardware brings smart models closer to sensors.', $oneHourAgo, $oneHourAgo, $oneHourAgo, $oneHourAgo),
				('item-3', 'guid:item-3', 'item-3', 'https://example.com/gardening', 'https://example.com/gardening', 'Local Gardening Calendar for Spring', 'Seed potatoes, trim berry canes, and prepare garden soil before the weekend.', $twoHoursAgo, $twoHoursAgo, $twoHoursAgo, $twoHoursAgo);

			INSERT INTO item_sources (item_id, feed_id, source_item_guid, source_item_url, first_seen_at)
			VALUES
				('item-1', 'feed-1', 'item-1', 'https://example.com/robotics', $now),
				('item-2', 'feed-1', 'item-2', 'https://example.com/chips', $oneHourAgo),
				('item-3', 'feed-1', 'item-3', 'https://example.com/gardening', $twoHoursAgo);
			""";
		command.Parameters.AddWithValue("$now", now);
		command.Parameters.AddWithValue("$oneHourAgo", oneHourAgo);
		command.Parameters.AddWithValue("$twoHoursAgo", twoHoursAgo);
		await command.ExecuteNonQueryAsync(CancellationToken.None);
	}

	private static async Task<string> ExecuteResultAsync(IResult result)
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
		using StreamReader reader = new(bodyStream);
		return await reader.ReadToEndAsync();
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
