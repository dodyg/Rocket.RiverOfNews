using System.Globalization;
using Dapper;
using Rocket.RiverOfNews.Data;

namespace Rocket.RiverOfNews.Services;

public sealed class RetentionCleanupBackgroundService : BackgroundService
{
	private readonly IServiceScopeFactory ServiceScopeFactory;

	public RetentionCleanupBackgroundService(IServiceScopeFactory ServiceScopeFactory)
	{
		ArgumentNullException.ThrowIfNull(ServiceScopeFactory);
		this.ServiceScopeFactory = ServiceScopeFactory;
	}

	protected override async Task ExecuteAsync(CancellationToken StoppingToken)
	{
		await RunCleanupAsync(StoppingToken);
		using PeriodicTimer Timer = new(TimeSpan.FromHours(1));
		while (await Timer.WaitForNextTickAsync(StoppingToken))
		{
			await RunCleanupAsync(StoppingToken);
		}
	}

	private async Task RunCleanupAsync(CancellationToken CancellationToken)
	{
		using IServiceScope Scope = ServiceScopeFactory.CreateScope();
		SqliteConnectionFactory ConnectionFactory = Scope.ServiceProvider.GetRequiredService<SqliteConnectionFactory>();
		await using Microsoft.Data.Sqlite.SqliteConnection Connection = await ConnectionFactory.OpenConnectionAsync(CancellationToken);

		string CutoffTimestamp = DateTimeOffset.UtcNow
			.AddDays(-30)
			.ToString("O", CultureInfo.InvariantCulture);

		const string Sql = """
			DELETE FROM items
			WHERE published_at < @CutoffTimestamp;
			""";

		await Connection.ExecuteAsync(new CommandDefinition(
			Sql,
			new
			{
				CutoffTimestamp
			},
			cancellationToken: CancellationToken));
	}
}
