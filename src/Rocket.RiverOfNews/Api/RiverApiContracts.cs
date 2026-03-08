namespace Rocket.RiverOfNews.Api;

public sealed record AddFeedRequest
{
public required string Url { get; init; }
public string? Title { get; init; }
}

public sealed record FeedResponse
{
public required string Id { get; init; }
public required string Url { get; init; }
public required string NormalizedUrl { get; init; }
public required string Title { get; init; }
public required string Status { get; init; }
public required int ConsecutiveFailures { get; init; }
public string? LastError { get; init; }
public string? LastPolledAt { get; init; }
public string? LastSuccessAt { get; init; }
}

public sealed record RiverItemResponse
{
public required string Id { get; init; }
public required string Title { get; init; }
public string? CanonicalUrl { get; init; }
public string? Url { get; init; }
public string? ImageUrl { get; init; }
public string? Snippet { get; init; }
public required string PublishedAt { get; init; }
public required string IngestedAt { get; init; }
public string? SourceNames { get; init; }
}

public sealed record RiverItemDetailResponse
{
public required string Id { get; init; }
public required string Title { get; init; }
public string? Url { get; init; }
public string? CanonicalUrl { get; init; }
public string? ImageUrl { get; init; }
public string? Snippet { get; init; }
public required string PublishedAt { get; init; }
public required string IngestedAt { get; init; }
public string? SourceNames { get; init; }
}

public sealed record RiverQueryResponse
{
public required IReadOnlyList<RiverItemResponse> Items { get; init; }
public string? NextCursor { get; init; }
}

public sealed record ErrorResponse(string Message);

public sealed record Latest200PerformanceResponse
{
public required int ItemCount { get; init; }
public required long DurationMilliseconds { get; init; }
public required int ThresholdMilliseconds { get; init; }
public required bool MeetsTarget { get; init; }
}
