namespace Rocket.RiverOfNews.Configuration;

public sealed class RiverOfNewsSettings
{
	public FeedSettings Feed { get; init; } = new();
}

public sealed class FeedSettings
{
	public int PollingIntervalMinutes { get; init; } = 15;
	public int RequestTimeoutSeconds { get; init; } = 10;
	public int RetentionDays { get; init; } = 30;
	public int UnhealthyThreshold { get; init; } = 3;
	public int BackoffLevel1Minutes { get; init; } = 5;
	public int BackoffLevel2Minutes { get; init; } = 15;
	public int BackoffLevel3Minutes { get; init; } = 60;
}
