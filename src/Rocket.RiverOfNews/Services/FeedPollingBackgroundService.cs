namespace Rocket.RiverOfNews.Services;

public sealed class FeedPollingBackgroundService : BackgroundService
{
	private readonly IServiceScopeFactory ServiceScopeFactory;

	public FeedPollingBackgroundService(IServiceScopeFactory ServiceScopeFactory)
	{
		ArgumentNullException.ThrowIfNull(ServiceScopeFactory);
		this.ServiceScopeFactory = ServiceScopeFactory;
	}

	protected override async Task ExecuteAsync(CancellationToken StoppingToken)
	{
		await RunScheduledRefreshAsync(StoppingToken);
		using PeriodicTimer Timer = new(TimeSpan.FromMinutes(1));
		while (await Timer.WaitForNextTickAsync(StoppingToken))
		{
			await RunScheduledRefreshAsync(StoppingToken);
		}
	}

	private async Task RunScheduledRefreshAsync(CancellationToken CancellationToken)
	{
		using IServiceScope ServiceScope = ServiceScopeFactory.CreateScope();
		FeedIngestionService FeedIngestionService = ServiceScope.ServiceProvider.GetRequiredService<FeedIngestionService>();
		await FeedIngestionService.RefreshDueFeedsAsync(CancellationToken);
	}
}
