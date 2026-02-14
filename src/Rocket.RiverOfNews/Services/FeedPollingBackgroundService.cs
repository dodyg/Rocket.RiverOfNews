namespace Rocket.RiverOfNews.Services;

public sealed class FeedPollingBackgroundService : BackgroundService
{
	private readonly IServiceScopeFactory ServiceScopeFactory;

	public FeedPollingBackgroundService(IServiceScopeFactory serviceScopeFactory)
	{
		ArgumentNullException.ThrowIfNull(serviceScopeFactory);
		ServiceScopeFactory = serviceScopeFactory;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await RunScheduledRefreshAsync(stoppingToken);
		using PeriodicTimer timer = new(TimeSpan.FromMinutes(1));
		while (await timer.WaitForNextTickAsync(stoppingToken))
		{
			await RunScheduledRefreshAsync(stoppingToken);
		}
	}

	private async Task RunScheduledRefreshAsync(CancellationToken cancellationToken)
	{
		using IServiceScope serviceScope = ServiceScopeFactory.CreateScope();
		FeedIngestionService feedIngestionService = serviceScope.ServiceProvider.GetRequiredService<FeedIngestionService>();
		await feedIngestionService.RefreshDueFeedsAsync(cancellationToken);
	}
}
