using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Rocket.RiverOfNews.Data;

namespace Rocket.RiverOfNews.Api;

internal static class RiverDataAccess
{
public static async Task<IReadOnlyList<FeedResponse>> GetFeedsAsync(
SqliteConnectionFactory connectionFactory,
CancellationToken cancellationToken)
{
const string sql = """
SELECT
id AS Id,
url AS Url,
normalized_url AS NormalizedUrl,
title AS Title,
status AS Status,
consecutive_failures AS ConsecutiveFailures,
last_error AS LastError,
last_polled_at AS LastPolledAt,
last_success_at AS LastSuccessAt
FROM feeds
ORDER BY title COLLATE NOCASE, normalized_url COLLATE NOCASE;
""";

await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
IReadOnlyList<FeedResponse> feeds = (await connection.QueryAsync<FeedResponse>(
new CommandDefinition(sql, cancellationToken: cancellationToken))).AsList();
return feeds;
}

public static async Task<AddFeedResult> AddFeedAsync(
SqliteConnectionFactory connectionFactory,
string url,
string? title,
CancellationToken cancellationToken)
{
string normalizedUrl;
try
{
normalizedUrl = NormalizeUrl(url);
}
catch (UriFormatException)
{
return new AddFeedResult(null, "Invalid feed URL.");
}

string feedId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
FeedResponse feed = new()
{
Id = feedId,
Url = url,
NormalizedUrl = normalizedUrl,
Title = string.IsNullOrWhiteSpace(title) ? normalizedUrl : title.Trim(),
Status = "healthy",
ConsecutiveFailures = 0,
LastError = null,
LastPolledAt = null,
LastSuccessAt = null
};

const string sql = """
INSERT INTO feeds (
id, url, normalized_url, title, status, consecutive_failures, created_at, updated_at
)
VALUES (
@Id, @Url, @NormalizedUrl, @Title, 'healthy', 0, @Now, @Now
);
""";

try
{
await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
await connection.ExecuteAsync(
new CommandDefinition(
sql,
new
{
Id = feed.Id,
Url = feed.Url,
NormalizedUrl = feed.NormalizedUrl,
Title = feed.Title,
Now = now
},
cancellationToken: cancellationToken));
}
catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
{
return new AddFeedResult(null, "Feed URL already exists.");
}

return new AddFeedResult(feed, null);
}

public static async Task<bool> DeleteFeedAsync(
SqliteConnectionFactory connectionFactory,
string feedId,
CancellationToken cancellationToken)
{
const string deleteOrphanedItemsSql = """
DELETE FROM items 
WHERE id IN (
SELECT is1.item_id 
FROM item_sources is1 
WHERE is1.feed_id = @FeedId
AND NOT EXISTS (
SELECT 1 FROM item_sources is2 
WHERE is2.item_id = is1.item_id 
AND is2.feed_id != @FeedId
)
);
""";
const string deleteFeedSql = "DELETE FROM feeds WHERE id = @FeedId;";

await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
		await using System.Data.Common.DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

await connection.ExecuteAsync(new CommandDefinition(deleteOrphanedItemsSql, new { FeedId = feedId }, transaction, cancellationToken: cancellationToken));
int rowsAffected = await connection.ExecuteAsync(new CommandDefinition(deleteFeedSql, new { FeedId = feedId }, transaction, cancellationToken: cancellationToken));

await transaction.CommitAsync(cancellationToken);
return rowsAffected > 0;
}

public static async Task<RiverQueryResponse> GetItemsAsync(
SqliteConnectionFactory connectionFactory,
string[] feedIds,
DateTimeOffset? startDateUtc,
DateTimeOffset? endDateUtc,
int limit,
string? cursorRaw,
CancellationToken cancellationToken)
{
CursorParts? cursor = ParseCursor(cursorRaw);
int fetchLimit = limit + 1;
string? startDate = startDateUtc?.ToString("O", CultureInfo.InvariantCulture);
string? endDate = endDateUtc?.ToString("O", CultureInfo.InvariantCulture);

const string sql = """
SELECT
i.id AS Id,
i.title AS Title,
i.url AS Url,
i.canonical_url AS CanonicalUrl,
i.image_url AS ImageUrl,
i.snippet AS Snippet,
i.published_at AS PublishedAt,
i.ingested_at AS IngestedAt,
(
SELECT group_concat(DISTINCT COALESCE(f.title, f.normalized_url))
FROM item_sources s
INNER JOIN feeds f ON f.id = s.feed_id
WHERE s.item_id = i.id
) AS SourceNames
FROM items i
WHERE
(@StartDate IS NULL OR i.published_at >= @StartDate)
AND (@EndDate IS NULL OR i.published_at <= @EndDate)
AND (
@HasCursor = 0
OR i.published_at < @CursorPublishedAt
OR (i.published_at = @CursorPublishedAt AND i.ingested_at < @CursorIngestedAt)
OR (i.published_at = @CursorPublishedAt AND i.ingested_at = @CursorIngestedAt AND i.id < @CursorId)
)
AND (
@HasFeedFilter = 0
OR EXISTS (
SELECT 1
FROM item_sources s
WHERE s.item_id = i.id
AND s.feed_id IN @FeedIds
)
)
ORDER BY i.published_at DESC, i.ingested_at DESC, i.id DESC
LIMIT @FetchLimit;
""";

await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
IReadOnlyList<RiverItemResponse> queriedItems = (await connection.QueryAsync<RiverItemResponse>(
new CommandDefinition(
sql,
new
{
StartDate = startDate,
EndDate = endDate,
HasCursor = cursor is not null ? 1 : 0,
CursorPublishedAt = cursor?.PublishedAt,
CursorIngestedAt = cursor?.IngestedAt,
CursorId = cursor?.Id,
HasFeedFilter = feedIds.Length > 0 ? 1 : 0,
FeedIds = feedIds,
FetchLimit = fetchLimit
},
cancellationToken: cancellationToken))).AsList();

IReadOnlyList<RiverItemResponse> items = queriedItems.Count > limit
? queriedItems.Take(limit).ToList()
: queriedItems;

string? nextCursor = null;
if (queriedItems.Count > limit)
{
RiverItemResponse lastItem = items[^1];
nextCursor = EncodeCursor(lastItem.PublishedAt, lastItem.IngestedAt, lastItem.Id);
}

return new RiverQueryResponse
{
Items = items,
NextCursor = nextCursor
};
}

public static async Task<RiverItemDetailResponse?> GetItemByIdAsync(
SqliteConnectionFactory connectionFactory,
string itemId,
CancellationToken cancellationToken)
{
const string sql = """
SELECT
i.id AS Id,
i.title AS Title,
i.url AS Url,
i.canonical_url AS CanonicalUrl,
i.image_url AS ImageUrl,
i.snippet AS Snippet,
i.published_at AS PublishedAt,
i.ingested_at AS IngestedAt,
(
SELECT group_concat(DISTINCT COALESCE(f.title, f.normalized_url))
FROM item_sources s
INNER JOIN feeds f ON f.id = s.feed_id
WHERE s.item_id = i.id
) AS SourceNames
FROM items i
WHERE i.id = @ItemId
LIMIT 1;
""";

await using SqliteConnection connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
return await connection.QuerySingleOrDefaultAsync<RiverItemDetailResponse>(
new CommandDefinition(sql, new { ItemId = itemId }, cancellationToken: cancellationToken));
}

public static string NormalizeUrl(string url)
{
Uri parsedUri = new(url, UriKind.Absolute);
if (!string.Equals(parsedUri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
&& !string.Equals(parsedUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
{
throw new UriFormatException("Only HTTP and HTTPS URLs are supported.");
}

UriBuilder builder = new(parsedUri)
{
Fragment = string.Empty
};

string scheme = builder.Scheme.ToLowerInvariant();
string host = builder.Host.ToLowerInvariant();
string port = builder.Port > 0 && !builder.Uri.IsDefaultPort
? $":{builder.Port.ToString(CultureInfo.InvariantCulture)}"
: string.Empty;
string path = builder.Path.TrimEnd('/');
if (string.IsNullOrWhiteSpace(path))
{
path = "/";
}

string query = builder.Query;
return $"{scheme}://{host}{port}{path}{query}";
}

public static string[] ParseFeedIds(string? feedIdsRaw)
{
if (string.IsNullOrWhiteSpace(feedIdsRaw))
{
return [];
}

return feedIdsRaw
.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
.Distinct(StringComparer.Ordinal)
.ToArray();
}

public static bool TryParseIsoDate(string? value, out DateTimeOffset? parsedValueUtc)
{
parsedValueUtc = null;
if (string.IsNullOrWhiteSpace(value))
{
return true;
}

if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedValue))
{
return false;
}

parsedValueUtc = parsedValue.ToUniversalTime();
return true;
}

public static bool TryParseCursor(string? cursorRaw, out RiverCursor? cursor)
{
CursorParts? parsed = ParseCursor(cursorRaw);
cursor = parsed is null ? null : new RiverCursor(parsed.PublishedAt, parsed.IngestedAt, parsed.Id);
return string.IsNullOrWhiteSpace(cursorRaw) || parsed is not null;
}

public static string EncodeCursor(string publishedAt, string ingestedAt, string id)
{
string cursorText = $"{publishedAt}|{ingestedAt}|{id}";
byte[] cursorBytes = Encoding.UTF8.GetBytes(cursorText);
return Convert.ToBase64String(cursorBytes);
}

private static CursorParts? ParseCursor(string? cursorRaw)
{
if (string.IsNullOrWhiteSpace(cursorRaw))
{
return null;
}

try
{
byte[] cursorBytes = Convert.FromBase64String(cursorRaw);
string cursorText = Encoding.UTF8.GetString(cursorBytes);
string[] parts = cursorText.Split('|', StringSplitOptions.None);
if (parts.Length != 3)
{
return null;
}

return new CursorParts(parts[0], parts[1], parts[2]);
}
catch (FormatException)
{
return null;
}
}

internal sealed record AddFeedResult(FeedResponse? Feed, string? ErrorMessage);
internal sealed record RiverCursor(string PublishedAt, string IngestedAt, string Id);
private sealed record CursorParts(string PublishedAt, string IngestedAt, string Id);
}
