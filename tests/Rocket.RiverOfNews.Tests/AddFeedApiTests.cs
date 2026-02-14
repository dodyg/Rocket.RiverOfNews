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
		await using TestDatabaseContext Database = await TestDatabaseContext.CreateAsync();
		MvpApi.AddFeedRequest Request = new()
		{
			Url = "https://example.com/feed.xml",
			Title = "Example Feed"
		};

		IResult Result = await MvpApi.AddFeedAsync(Request, Database.ConnectionFactory, CancellationToken.None);
		(int StatusCode, JsonElement Body) = await ExecuteResultAsync(Result);

		await Assert.That(StatusCode).IsEqualTo(StatusCodes.Status201Created);
		await Assert.That(Body.GetProperty("url").GetString()).IsEqualTo("https://example.com/feed.xml");
		await Assert.That(Body.GetProperty("title").GetString()).IsEqualTo("Example Feed");
		await Assert.That(await CountFeedsAsync(Database.ConnectionFactory)).IsEqualTo(1);
	}

	[Test]
	public async Task AddFeedAsync_WithDuplicateUrl_ReturnsConflict()
	{
		await using TestDatabaseContext Database = await TestDatabaseContext.CreateAsync();
		await MvpApi.AddFeedAsync(
			new MvpApi.AddFeedRequest
			{
				Url = "https://example.com/feed.xml"
			},
			Database.ConnectionFactory,
			CancellationToken.None);

		IResult DuplicateResult = await MvpApi.AddFeedAsync(
			new MvpApi.AddFeedRequest
			{
				Url = "https://example.com/feed.xml#fragment"
			},
			Database.ConnectionFactory,
			CancellationToken.None);

		(int StatusCode, JsonElement Body) = await ExecuteResultAsync(DuplicateResult);
		await Assert.That(StatusCode).IsEqualTo(StatusCodes.Status409Conflict);
		await Assert.That(Body.GetProperty("message").GetString()).IsEqualTo("Feed URL already exists.");
	}

	[Test]
	public async Task AddFeedAsync_WithInvalidUrl_ReturnsBadRequest()
	{
		await using TestDatabaseContext Database = await TestDatabaseContext.CreateAsync();
		IResult Result = await MvpApi.AddFeedAsync(
			new MvpApi.AddFeedRequest
			{
				Url = "not-a-valid-url"
			},
			Database.ConnectionFactory,
			CancellationToken.None);

		(int StatusCode, JsonElement Body) = await ExecuteResultAsync(Result);
		await Assert.That(StatusCode).IsEqualTo(StatusCodes.Status400BadRequest);
		await Assert.That(Body.GetProperty("message").GetString()).IsEqualTo("Invalid feed URL.");
	}

	private static async Task<(int StatusCode, JsonElement Body)> ExecuteResultAsync(IResult Result)
	{
		DefaultHttpContext HttpContext = new();
		ServiceCollection Services = new();
		Services.AddLogging();
		Services.AddOptions();
		HttpContext.RequestServices = Services.BuildServiceProvider();
		await using MemoryStream BodyStream = new();
		HttpContext.Response.Body = BodyStream;

		await Result.ExecuteAsync(HttpContext);
		BodyStream.Position = 0;
		using JsonDocument Document = await JsonDocument.ParseAsync(BodyStream);
		return (HttpContext.Response.StatusCode, Document.RootElement.Clone());
	}

	private static async Task<int> CountFeedsAsync(SqliteConnectionFactory ConnectionFactory)
	{
		await using SqliteConnection Connection = await ConnectionFactory.OpenConnectionAsync(CancellationToken.None);
		await using SqliteCommand Command = Connection.CreateCommand();
		Command.CommandText = "SELECT COUNT(*) FROM feeds;";
		object? Scalar = await Command.ExecuteScalarAsync(CancellationToken.None);
		return Convert.ToInt32(Scalar, System.Globalization.CultureInfo.InvariantCulture);
	}

	private sealed class TestDatabaseContext : IAsyncDisposable
	{
		public required string RootPath { get; init; }
		public required SqliteConnectionFactory ConnectionFactory { get; init; }

		public static async Task<TestDatabaseContext> CreateAsync()
		{
			string RootPath = Path.Combine(Path.GetTempPath(), "Rocket.RiverOfNews.Tests", Guid.NewGuid().ToString("N"));
			string DatabasePath = Path.Combine(RootPath, "db", "river.test.db");
			string MigrationsPath = Path.Combine(GetRepositoryRootPath(), "db", "migrations");

			await SqliteDatabaseBootstrapper.InitializeAsync(DatabasePath, MigrationsPath, CancellationToken.None);
			return new TestDatabaseContext
			{
				RootPath = RootPath,
				ConnectionFactory = new SqliteConnectionFactory(DatabasePath)
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
		DirectoryInfo? Current = new(AppContext.BaseDirectory);
		while (Current is not null)
		{
			string Candidate = Path.Combine(Current.FullName, "Rocket.RiverOfNews.slnx");
			if (File.Exists(Candidate))
			{
				return Current.FullName;
			}

			Current = Current.Parent;
		}

		throw new DirectoryNotFoundException("Could not find Rocket.RiverOfNews.slnx from test base directory.");
	}
}
