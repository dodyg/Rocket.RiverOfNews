using System.Globalization;
using Dapper;
using Rocket.RiverOfNews.Data;

namespace Rocket.RiverOfNews.Services;

public sealed class RetentionCleanupBackgroundService : BackgroundService
{
	private readonly IServiceScopeFactory ServiceScopeFactory;

	public RetentionCleanupBackgroundService(IServiceScopeFactory serviceScopeFactory)
	{
		ArgumentNullException.ThrowIfNull(serviceScopeFactory);
		ServiceScopeFactory = serviceScopeFactory;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await RunCleanupAsync(stoppingToken);
		using PeriodicTimer timer = new(TimeSpan.FromHours(1));
		while (await timer.WaitForNextTickAsync(stoppingToken))
		{
			await RunCleanupAsync(stoppingToken);
		}
	}

	private async Task RunCleanupAsync(CancellationToken cancellationToken)
	{
		using IServiceScope scope = ServiceScopeFactory.CreateScope();
		SqliteConnectionFactory connectionFactory = scope.ServiceProvider.GetRequiredService<SqliteConnectionFactory>();
		await using Microsoft.Data.Sqlite.SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

		string cutoffTimestamp = DateTimeOffset.UtcNow
			.AddDays(-30)
			.ToString("O", CultureInfo.InvariantCulture);

		const string sql = """
			DELETE FROM items
			WHERE published_at < @CutoffTimestamp;
			""";

		await connection.ExecuteAsync(new CommandDefinition(
			sql,
			new
			{
				CutoffTimestamp = cutoffTimestamp
			},
			cancellationToken: cancellationToken));
	}
}
