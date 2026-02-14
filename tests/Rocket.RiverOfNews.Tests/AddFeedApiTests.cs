using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Rocket.RiverOfNews.Api;
using Rocket.RiverOfNews.Data;
using TUnit.Core;

namespace Rocket.RiverOfNews.Tests;

public class AddFeedApiTests
{
	[Test]
	public async Task AddFeedAsync_WithValidUrl_ReturnsCreatedAndPersistsFeed()
	{
		await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
		MvpApi.AddFeedRequest request = new()
		{
			Url = "https://example.com/feed.xml",
			Title = "Example Feed"
		};

		IResult result = await MvpApi.AddFeedAsync(request, database.ConnectionFactory, CancellationToken.None);
		(int statusCode, JsonElement body) = await ExecuteResultAsync(result);

		await Assert.That(statusCode).IsEqualTo(StatusCodes.Status201Created);
		await Assert.That(body.GetProperty("url").GetString()).IsEqualTo("https://example.com/feed.xml");
		await Assert.That(body.GetProperty("title").GetString()).IsEqualTo("Example Feed");
		await Assert.That(await CountFeedsAsync(database.ConnectionFactory)).IsEqualTo(1);
	}

	[Test]
	public async Task AddFeedAsync_WithDuplicateUrl_ReturnsConflict()
	{
		await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
		await MvpApi.AddFeedAsync(
			new MvpApi.AddFeedRequest
			{
				Url = "https://example.com/feed.xml"
			},
			database.ConnectionFactory,
			CancellationToken.None);

		IResult duplicateResult = await MvpApi.AddFeedAsync(
			new MvpApi.AddFeedRequest
			{
				Url = "https://example.com/feed.xml#fragment"
			},
			database.ConnectionFactory,
			CancellationToken.None);

		(int statusCode, JsonElement body) = await ExecuteResultAsync(duplicateResult);
		await Assert.That(statusCode).IsEqualTo(StatusCodes.Status409Conflict);
		await Assert.That(body.GetProperty("message").GetString()).IsEqualTo("Feed URL already exists.");
	}

	[Test]
	public async Task AddFeedAsync_WithInvalidUrl_ReturnsBadRequest()
	{
		await using TestDatabaseContext database = await TestDatabaseContext.CreateAsync();
		IResult result = await MvpApi.AddFeedAsync(
			new MvpApi.AddFeedRequest
			{
				Url = "not-a-valid-url"
			},
			database.ConnectionFactory,
			CancellationToken.None);

		(int statusCode, JsonElement body) = await ExecuteResultAsync(result);
		await Assert.That(statusCode).IsEqualTo(StatusCodes.Status400BadRequest);
		await Assert.That(body.GetProperty("message").GetString()).IsEqualTo("Invalid feed URL.");
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

	private static async Task<int> CountFeedsAsync(SqliteConnectionFactory connectionFactory)
	{
		await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(CancellationToken.None);
		await using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM feeds;";
		object? scalar = await command.ExecuteScalarAsync(CancellationToken.None);
		return Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
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
