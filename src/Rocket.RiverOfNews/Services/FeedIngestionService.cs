using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Rocket.RiverOfNews.Configuration;
using Rocket.RiverOfNews.Data;
using Rocket.Syndication;
using Rocket.Syndication.Models;

namespace Rocket.RiverOfNews.Services;

public sealed partial class FeedIngestionService
{
	private static readonly Regex HtmlTagRegex = HtmlRegex();
	private static readonly Regex HtmlImageRegex = HtmlImageSrcRegex();
	private const int SnippetLengthLimit = 1000;
	private readonly SqliteConnectionFactory ConnectionFactory;
	private readonly ISyndicationClient SyndicationClient;
	private readonly FeedSettings Settings;

	public FeedIngestionService(
		SqliteConnectionFactory connectionFactory,
		ISyndicationClient syndicationClient,
		RiverOfNewsSettings settings)
	{
		ArgumentNullException.ThrowIfNull(connectionFactory);
		ArgumentNullException.ThrowIfNull(syndicationClient);
		ArgumentNullException.ThrowIfNull(settings);

		ConnectionFactory = connectionFactory;
		SyndicationClient = syndicationClient;
		Settings = settings.Feed;
	}

	public async Task<RefreshResult> RefreshAllFeedsAsync(CancellationToken cancellationToken)
	{
		return await RefreshFeedsAsync(true, cancellationToken);
	}

	public async Task<RefreshResult> RefreshDueFeedsAsync(CancellationToken cancellationToken)
	{
		return await RefreshFeedsAsync(false, cancellationToken);
	}

	private async Task<RefreshResult> RefreshFeedsAsync(
		bool forceAllFeeds,
		CancellationToken cancellationToken)
	{
		const string selectFeedsSql = """
			SELECT
				id AS Id,
				url AS Url,
				consecutive_failures AS ConsecutiveFailures,
				last_polled_at AS LastPolledAt
			FROM feeds
			ORDER BY normalized_url COLLATE NOCASE;
			""";

		await using SqliteConnection connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken);
		IReadOnlyList<FeedRecord> feeds = (await connection.QueryAsync<FeedRecord>(
			new CommandDefinition(selectFeedsSql, cancellationToken: cancellationToken))).AsList();

		int processedFeedCount = 0;
		int successFeedCount = 0;
		int failedFeedCount = 0;
		int insertedItemCount = 0;
		int mergedItemCount = 0;
		int skippedFeedCount = 0;

		foreach (FeedRecord feed in feeds)
		{
			if (!forceAllFeeds && !IsFeedDue(feed, DateTimeOffset.UtcNow))
			{
				skippedFeedCount++;
				continue;
			}

			processedFeedCount++;
			FeedResult feedResult = await SyndicationClient.GetFeedAsync(feed.Url, cancellationToken);
			if (!feedResult.IsSuccess || feedResult.Feed is null)
			{
				failedFeedCount++;
				string errorMessage = feedResult.Error?.Message ?? "Feed fetch failed.";
				await MarkFeedFailedAsync(connection, feed, errorMessage, cancellationToken);
				continue;
			}

			successFeedCount++;
			ItemIngestionResult itemIngestionResult = await IngestFeedItemsAsync(connection, feed, feedResult.Feed, cancellationToken);
			insertedItemCount += itemIngestionResult.InsertedItemCount;
			mergedItemCount += itemIngestionResult.MergedItemCount;
			await MarkFeedSucceededAsync(connection, feed.Id, cancellationToken);
		}

		return new RefreshResult
		{
			Status = "completed",
			ProcessedFeedCount = processedFeedCount,
			SuccessFeedCount = successFeedCount,
			FailedFeedCount = failedFeedCount,
			SkippedFeedCount = skippedFeedCount,
			InsertedItemCount = insertedItemCount,
			MergedItemCount = mergedItemCount
		};
	}

	private bool IsFeedDue(FeedRecord feed, DateTimeOffset nowUtc)
	{
		if (!TryParseUtcTimestamp(feed.LastPolledAt, out DateTimeOffset? lastPolledAtUtc))
		{
			return true;
		}

		DateTimeOffset lastPolledAt = lastPolledAtUtc!.Value;
		if (feed.ConsecutiveFailures >= Settings.UnhealthyThreshold)
		{
			TimeSpan retryDelay = feed.ConsecutiveFailures switch
			{
				3 => TimeSpan.FromMinutes(Settings.BackoffLevel1Minutes),
				4 => TimeSpan.FromMinutes(Settings.BackoffLevel2Minutes),
				_ => TimeSpan.FromMinutes(Settings.BackoffLevel3Minutes)
			};

			return (nowUtc - lastPolledAt) >= retryDelay;
		}

		return (nowUtc - lastPolledAt) >= TimeSpan.FromMinutes(Settings.PollingIntervalMinutes);
	}

	private static async Task<ItemIngestionResult> IngestFeedItemsAsync(
		SqliteConnection connection,
		FeedRecord feed,
		Feed parsedFeed,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<FeedItem> items = parsedFeed.Items ?? [];
		int insertedItemCount = 0;
		int mergedItemCount = 0;

		await using System.Data.Common.DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
		foreach (FeedItem item in items)
		{
			string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
			string? sourceGuid = GetSourceGuid(item);
			string? canonicalUrl = CanonicalizeArticleUrl(item.Link?.AbsoluteUri);
			string canonicalKey = BuildCanonicalKey(feed.Id, sourceGuid, canonicalUrl, item);
			string? existingItemId = await connection.QuerySingleOrDefaultAsync<string>(
				new CommandDefinition(
					"SELECT id FROM items WHERE canonical_key = @CanonicalKey;",
					new
					{
						CanonicalKey = canonicalKey
					},
					transaction,
					cancellationToken: cancellationToken));

			string itemId = existingItemId ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
			if (existingItemId is null)
			{
				string title = string.IsNullOrWhiteSpace(item.Title) ? "(untitled)" : item.Title.Trim();
				string itemUrl = item.Link?.AbsoluteUri ?? feed.Url;
				string? imageUrl = BuildImageUrl(item);
				string snippet = BuildSnippet(item);
				string publishedAt = (item.PublishedDate ?? item.UpdatedDate ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture);

				await connection.ExecuteAsync(
					new CommandDefinition(
						"""
						INSERT INTO items (
							id, canonical_key, guid, url, canonical_url, image_url, title, snippet, published_at, ingested_at, created_at, updated_at
						)
						VALUES (
							@Id, @CanonicalKey, @Guid, @Url, @CanonicalUrl, @ImageUrl, @Title, @Snippet, @PublishedAt, @IngestedAt, @CreatedAt, @UpdatedAt
						);
						""",
						new
						{
							Id = itemId,
							CanonicalKey = canonicalKey,
							Guid = sourceGuid,
							Url = itemUrl,
							CanonicalUrl = canonicalUrl,
							ImageUrl = imageUrl,
							Title = title,
							Snippet = snippet,
							PublishedAt = publishedAt,
							IngestedAt = now,
							CreatedAt = now,
							UpdatedAt = now
						},
						transaction,
						cancellationToken: cancellationToken));
				insertedItemCount++;
			}
			else
			{
				mergedItemCount++;
			}

			await connection.ExecuteAsync(
				new CommandDefinition(
					"""
					INSERT INTO item_sources (
						item_id, feed_id, source_item_guid, source_item_url, first_seen_at
					)
					VALUES (
						@ItemId, @FeedId, @SourceItemGuid, @SourceItemUrl, @FirstSeenAt
					)
					ON CONFLICT(item_id, feed_id) DO NOTHING;
					""",
					new
					{
						ItemId = itemId,
						FeedId = feed.Id,
						SourceItemGuid = sourceGuid ?? string.Empty,
						SourceItemUrl = item.Link?.AbsoluteUri,
						FirstSeenAt = now
					},
					transaction,
					cancellationToken: cancellationToken));
		}

		await transaction.CommitAsync(cancellationToken);
		return new ItemIngestionResult(insertedItemCount, mergedItemCount);
	}

	private static async Task MarkFeedSucceededAsync(
		SqliteConnection connection,
		string feedId,
		CancellationToken cancellationToken)
	{
		string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
		await connection.ExecuteAsync(
			new CommandDefinition(
				"""
				UPDATE feeds
				SET
					status = 'healthy',
					consecutive_failures = 0,
					last_error = NULL,
					last_polled_at = @Now,
					last_success_at = @Now,
					updated_at = @Now
				WHERE id = @FeedId;
				""",
				new
				{
					FeedId = feedId,
					Now = now
				},
				cancellationToken: cancellationToken));
	}

	private async Task MarkFeedFailedAsync(
		SqliteConnection connection,
		FeedRecord feed,
		string errorMessage,
		CancellationToken cancellationToken)
	{
		string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
		int newFailures = feed.ConsecutiveFailures + 1;
		string status = newFailures >= Settings.UnhealthyThreshold ? "unhealthy" : "healthy";

		await connection.ExecuteAsync(
			new CommandDefinition(
				"""
				UPDATE feeds
				SET
					status = @Status,
					consecutive_failures = @ConsecutiveFailures,
					last_error = @LastError,
					last_polled_at = @Now,
					updated_at = @Now
				WHERE id = @FeedId;
				""",
				new
				{
					FeedId = feed.Id,
					Status = status,
					ConsecutiveFailures = newFailures,
					LastError = errorMessage,
					Now = now
				},
				cancellationToken: cancellationToken));
	}

	private static string? GetSourceGuid(FeedItem item)
	{
		if (!string.IsNullOrWhiteSpace(item.Id))
		{
			return item.Id.Trim();
		}

		if (!string.IsNullOrWhiteSpace(item.RssData?.Guid))
		{
			return item.RssData.Guid.Trim();
		}

		return null;
	}

	private static string BuildCanonicalKey(
		string feedId,
		string? sourceGuid,
		string? canonicalUrl,
		FeedItem item)
	{
		if (!string.IsNullOrWhiteSpace(sourceGuid))
		{
			return $"guid:{sourceGuid}";
		}

		if (!string.IsNullOrWhiteSpace(canonicalUrl))
		{
			return $"url:{canonicalUrl}";
		}

		string sourcePayload = string.Join(
			"|",
			feedId,
			item.Title ?? string.Empty,
			item.Link?.AbsoluteUri ?? string.Empty,
			item.PublishedDate?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
			item.UpdatedDate?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
			item.Content?.PlainText ?? string.Empty);

		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourcePayload));
		string hashHex = Convert.ToHexString(hash);
		return $"unique:{hashHex}";
	}

	private static string BuildSnippet(FeedItem item)
	{
		string candidate = item.Content?.PlainText
			?? item.Content?.Html
			?? string.Empty;
		if (string.IsNullOrWhiteSpace(candidate))
		{
			return string.Empty;
		}

		string plain = HtmlTagRegex.Replace(candidate, " ").Trim();
		if (plain.Length <= SnippetLengthLimit)
		{
			return plain;
		}

		return $"{plain[..SnippetLengthLimit].TrimEnd()}...";
	}

	private static string? BuildImageUrl(FeedItem item)
	{
		string? mediaImage = item.Media?.ThumbnailUrl?.AbsoluteUri
			?? item.Media?.Url?.AbsoluteUri;
		if (!string.IsNullOrWhiteSpace(mediaImage))
		{
			return mediaImage;
		}

		foreach (FeedEnclosure enclosure in item.Enclosures ?? [])
		{
			if (enclosure.Url is null)
			{
				continue;
			}

			if (IsImageMimeType(enclosure.MimeType))
			{
				return enclosure.Url.AbsoluteUri;
			}
		}

		string? htmlContent = item.Content?.Html;
		if (string.IsNullOrWhiteSpace(htmlContent))
		{
			return null;
		}

		Match match = HtmlImageRegex.Match(htmlContent);
		if (!match.Success)
		{
			return null;
		}

		string imageCandidate = match.Groups["url"].Value.Trim();
		if (Uri.TryCreate(imageCandidate, UriKind.Absolute, out Uri? imageUri))
		{
			return imageUri.AbsoluteUri;
		}

		return null;
	}

	private static bool IsImageMimeType(string? mimeType)
	{
		return !string.IsNullOrWhiteSpace(mimeType)
			&& mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
	}

	private static string? CanonicalizeArticleUrl(string? url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return null;
		}

		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsedUri))
		{
			return null;
		}

		UriBuilder builder = new(parsedUri)
		{
			Fragment = string.Empty
		};

		string query = builder.Query;
		if (!string.IsNullOrWhiteSpace(query))
		{
			string trimmedQuery = query.TrimStart('?');
			IReadOnlyList<string> retainedQueryPairs = trimmedQuery
				.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(queryPart =>
				{
					int equalsIndex = queryPart.IndexOf('=', StringComparison.Ordinal);
					string key = (equalsIndex > -1 ? queryPart[..equalsIndex] : queryPart).Trim();
					if (key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}

					if (string.Equals(key, "fbclid", StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}

					return !string.Equals(key, "gclid", StringComparison.OrdinalIgnoreCase);
				})
				.ToArray();

			builder.Query = retainedQueryPairs.Count > 0
				? string.Join("&", retainedQueryPairs)
				: string.Empty;
		}

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

		string canonicalQuery = builder.Query;
		return $"{scheme}://{host}{port}{path}{canonicalQuery}";
	}

	[GeneratedRegex("<[^>]+>", RegexOptions.Compiled, 1000)]
	private static partial Regex HtmlRegex();

	[GeneratedRegex("<img\\b[^>]*\\bsrc\\s*=\\s*['\"](?<url>[^'\"]+)['\"][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled, 1000)]
	private static partial Regex HtmlImageSrcRegex();

	private static bool TryParseUtcTimestamp(string? timestamp, out DateTimeOffset? parsedUtc)
	{
		parsedUtc = null;
		if (string.IsNullOrWhiteSpace(timestamp))
		{
			return false;
		}

		if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
		{
			return false;
		}

		parsedUtc = parsed.ToUniversalTime();
		return true;
	}

	private sealed record FeedRecord
	{
		public required string Id { get; init; }
		public required string Url { get; init; }
		public required int ConsecutiveFailures { get; init; }
		public string? LastPolledAt { get; init; }
	}

	private sealed record ItemIngestionResult(int InsertedItemCount, int MergedItemCount);
}

public sealed record RefreshResult
{
	public required string Status { get; init; }
	public required int ProcessedFeedCount { get; init; }
	public required int SuccessFeedCount { get; init; }
	public required int FailedFeedCount { get; init; }
	public required int SkippedFeedCount { get; init; }
	public required int InsertedItemCount { get; init; }
	public required int MergedItemCount { get; init; }
}
