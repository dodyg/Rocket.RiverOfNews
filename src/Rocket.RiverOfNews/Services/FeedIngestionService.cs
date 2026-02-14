using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Rocket.RiverOfNews.Data;
using Rocket.Syndication;
using Rocket.Syndication.Models;

namespace Rocket.RiverOfNews.Services;

public sealed partial class FeedIngestionService
{
	private static readonly Regex HtmlTagRegex = HtmlRegex();
	private readonly SqliteConnectionFactory ConnectionFactory;
	private readonly ISyndicationClient SyndicationClient;

	public FeedIngestionService(
		SqliteConnectionFactory ConnectionFactory,
		ISyndicationClient SyndicationClient)
	{
		ArgumentNullException.ThrowIfNull(ConnectionFactory);
		ArgumentNullException.ThrowIfNull(SyndicationClient);

		this.ConnectionFactory = ConnectionFactory;
		this.SyndicationClient = SyndicationClient;
	}

	public async Task<RefreshResult> RefreshAllFeedsAsync(CancellationToken CancellationToken)
	{
		return await RefreshFeedsAsync(true, CancellationToken);
	}

	public async Task<RefreshResult> RefreshDueFeedsAsync(CancellationToken CancellationToken)
	{
		return await RefreshFeedsAsync(false, CancellationToken);
	}

	private async Task<RefreshResult> RefreshFeedsAsync(
		bool ForceAllFeeds,
		CancellationToken CancellationToken)
	{
		const string SelectFeedsSql = """
			SELECT
				id AS Id,
				url AS Url,
				consecutive_failures AS ConsecutiveFailures,
				last_polled_at AS LastPolledAt
			FROM feeds
			ORDER BY normalized_url COLLATE NOCASE;
			""";

		await using SqliteConnection Connection = await ConnectionFactory.OpenConnectionAsync(CancellationToken);
		IReadOnlyList<FeedRecord> Feeds = (await Connection.QueryAsync<FeedRecord>(
			new CommandDefinition(SelectFeedsSql, cancellationToken: CancellationToken))).AsList();

		int ProcessedFeedCount = 0;
		int SuccessFeedCount = 0;
		int FailedFeedCount = 0;
		int InsertedItemCount = 0;
		int MergedItemCount = 0;
		int SkippedFeedCount = 0;

		foreach (FeedRecord Feed in Feeds)
		{
			if (!ForceAllFeeds && !IsFeedDue(Feed, DateTimeOffset.UtcNow))
			{
				SkippedFeedCount++;
				continue;
			}

			ProcessedFeedCount++;
			FeedResult FeedResult = await SyndicationClient.GetFeedAsync(Feed.Url, CancellationToken);
			if (!FeedResult.IsSuccess || FeedResult.Feed is null)
			{
				FailedFeedCount++;
				string ErrorMessage = FeedResult.Error?.Message ?? "Feed fetch failed.";
				await MarkFeedFailedAsync(Connection, Feed, ErrorMessage, CancellationToken);
				continue;
			}

			SuccessFeedCount++;
			ItemIngestionResult ItemIngestionResult = await IngestFeedItemsAsync(Connection, Feed, FeedResult.Feed, CancellationToken);
			InsertedItemCount += ItemIngestionResult.InsertedItemCount;
			MergedItemCount += ItemIngestionResult.MergedItemCount;
			await MarkFeedSucceededAsync(Connection, Feed.Id, CancellationToken);
		}

		return new RefreshResult
		{
			Status = "completed",
			ProcessedFeedCount = ProcessedFeedCount,
			SuccessFeedCount = SuccessFeedCount,
			FailedFeedCount = FailedFeedCount,
			SkippedFeedCount = SkippedFeedCount,
			InsertedItemCount = InsertedItemCount,
			MergedItemCount = MergedItemCount
		};
	}

	private static bool IsFeedDue(FeedRecord Feed, DateTimeOffset NowUtc)
	{
		if (!TryParseUtcTimestamp(Feed.LastPolledAt, out DateTimeOffset? LastPolledAtUtc))
		{
			return true;
		}

		DateTimeOffset LastPolledAt = LastPolledAtUtc!.Value;
		if (Feed.ConsecutiveFailures >= 3)
		{
			TimeSpan RetryDelay = Feed.ConsecutiveFailures switch
			{
				3 => TimeSpan.FromMinutes(5),
				4 => TimeSpan.FromMinutes(15),
				_ => TimeSpan.FromMinutes(60)
			};

			return (NowUtc - LastPolledAt) >= RetryDelay;
		}

		return (NowUtc - LastPolledAt) >= TimeSpan.FromMinutes(15);
	}

	private static async Task<ItemIngestionResult> IngestFeedItemsAsync(
		SqliteConnection Connection,
		FeedRecord Feed,
		Feed ParsedFeed,
		CancellationToken CancellationToken)
	{
		IReadOnlyList<FeedItem> Items = ParsedFeed.Items ?? [];
		int InsertedItemCount = 0;
		int MergedItemCount = 0;

		await using System.Data.Common.DbTransaction Transaction = await Connection.BeginTransactionAsync(CancellationToken);
		foreach (FeedItem Item in Items)
		{
			string Now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
			string? SourceGuid = GetSourceGuid(Item);
			string? CanonicalUrl = CanonicalizeArticleUrl(Item.Link?.AbsoluteUri);
			string CanonicalKey = BuildCanonicalKey(Feed.Id, SourceGuid, CanonicalUrl, Item);
			string? ExistingItemId = await Connection.QuerySingleOrDefaultAsync<string>(
				new CommandDefinition(
					"SELECT id FROM items WHERE canonical_key = @CanonicalKey;",
					new
					{
						CanonicalKey
					},
					Transaction,
					cancellationToken: CancellationToken));

			string ItemId = ExistingItemId ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
			if (ExistingItemId is null)
			{
				string Title = string.IsNullOrWhiteSpace(Item.Title) ? "(untitled)" : Item.Title.Trim();
				string ItemUrl = Item.Link?.AbsoluteUri ?? Feed.Url;
				string Snippet = BuildSnippet(Item);
				string PublishedAt = (Item.PublishedDate ?? Item.UpdatedDate ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture);

				await Connection.ExecuteAsync(
					new CommandDefinition(
						"""
						INSERT INTO items (
							id, canonical_key, guid, url, canonical_url, title, snippet, published_at, ingested_at, created_at, updated_at
						)
						VALUES (
							@Id, @CanonicalKey, @Guid, @Url, @CanonicalUrl, @Title, @Snippet, @PublishedAt, @IngestedAt, @CreatedAt, @UpdatedAt
						);
						""",
						new
						{
							Id = ItemId,
							CanonicalKey,
							Guid = SourceGuid,
							Url = ItemUrl,
							CanonicalUrl,
							Title,
							Snippet,
							PublishedAt,
							IngestedAt = Now,
							CreatedAt = Now,
							UpdatedAt = Now
						},
						Transaction,
						cancellationToken: CancellationToken));
				InsertedItemCount++;
			}
			else
			{
				MergedItemCount++;
			}

			await Connection.ExecuteAsync(
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
						ItemId,
						FeedId = Feed.Id,
						SourceItemGuid = SourceGuid ?? string.Empty,
						SourceItemUrl = Item.Link?.AbsoluteUri,
						FirstSeenAt = Now
					},
					Transaction,
					cancellationToken: CancellationToken));
		}

		await Transaction.CommitAsync(CancellationToken);
		return new ItemIngestionResult(InsertedItemCount, MergedItemCount);
	}

	private static async Task MarkFeedSucceededAsync(
		SqliteConnection Connection,
		string FeedId,
		CancellationToken CancellationToken)
	{
		string Now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
		await Connection.ExecuteAsync(
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
					FeedId,
					Now
				},
				cancellationToken: CancellationToken));
	}

	private static async Task MarkFeedFailedAsync(
		SqliteConnection Connection,
		FeedRecord Feed,
		string ErrorMessage,
		CancellationToken CancellationToken)
	{
		string Now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
		int NewFailures = Feed.ConsecutiveFailures + 1;
		string Status = NewFailures >= 3 ? "unhealthy" : "healthy";

		await Connection.ExecuteAsync(
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
					FeedId = Feed.Id,
					Status,
					ConsecutiveFailures = NewFailures,
					LastError = ErrorMessage,
					Now
				},
				cancellationToken: CancellationToken));
	}

	private static string? GetSourceGuid(FeedItem Item)
	{
		if (!string.IsNullOrWhiteSpace(Item.Id))
		{
			return Item.Id.Trim();
		}

		if (!string.IsNullOrWhiteSpace(Item.RssData?.Guid))
		{
			return Item.RssData.Guid.Trim();
		}

		return null;
	}

	private static string BuildCanonicalKey(
		string FeedId,
		string? SourceGuid,
		string? CanonicalUrl,
		FeedItem Item)
	{
		if (!string.IsNullOrWhiteSpace(SourceGuid))
		{
			return $"guid:{SourceGuid}";
		}

		if (!string.IsNullOrWhiteSpace(CanonicalUrl))
		{
			return $"url:{CanonicalUrl}";
		}

		string SourcePayload = string.Join(
			"|",
			FeedId,
			Item.Title ?? string.Empty,
			Item.Link?.AbsoluteUri ?? string.Empty,
			Item.PublishedDate?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
			Item.UpdatedDate?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
			Item.Content?.PlainText ?? string.Empty);

		byte[] Hash = SHA256.HashData(Encoding.UTF8.GetBytes(SourcePayload));
		string HashHex = Convert.ToHexString(Hash);
		return $"unique:{HashHex}";
	}

	private static string BuildSnippet(FeedItem Item)
	{
		string Candidate = Item.Content?.PlainText
			?? Item.Content?.Html
			?? string.Empty;
		if (string.IsNullOrWhiteSpace(Candidate))
		{
			return string.Empty;
		}

		string Plain = HtmlTagRegex.Replace(Candidate, " ").Trim();
		if (Plain.Length <= 320)
		{
			return Plain;
		}

		return $"{Plain[..320].TrimEnd()}...";
	}

	private static string? CanonicalizeArticleUrl(string? Url)
	{
		if (string.IsNullOrWhiteSpace(Url))
		{
			return null;
		}

		if (!Uri.TryCreate(Url, UriKind.Absolute, out Uri? ParsedUri))
		{
			return null;
		}

		UriBuilder Builder = new(ParsedUri)
		{
			Fragment = string.Empty
		};

		string Query = Builder.Query;
		if (!string.IsNullOrWhiteSpace(Query))
		{
			string TrimmedQuery = Query.TrimStart('?');
			IReadOnlyList<string> RetainedQueryPairs = TrimmedQuery
				.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.Where(QueryPart =>
				{
					int EqualsIndex = QueryPart.IndexOf('=', StringComparison.Ordinal);
					string Key = (EqualsIndex > -1 ? QueryPart[..EqualsIndex] : QueryPart).Trim();
					if (Key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}

					if (string.Equals(Key, "fbclid", StringComparison.OrdinalIgnoreCase))
					{
						return false;
					}

					return !string.Equals(Key, "gclid", StringComparison.OrdinalIgnoreCase);
				})
				.ToArray();

			Builder.Query = RetainedQueryPairs.Count > 0
				? string.Join("&", RetainedQueryPairs)
				: string.Empty;
		}

		string Scheme = Builder.Scheme.ToLowerInvariant();
		string Host = Builder.Host.ToLowerInvariant();
		string Port = Builder.Port > 0 && !Builder.Uri.IsDefaultPort
			? $":{Builder.Port.ToString(CultureInfo.InvariantCulture)}"
			: string.Empty;
		string Path = Builder.Path.TrimEnd('/');
		if (string.IsNullOrWhiteSpace(Path))
		{
			Path = "/";
		}

		string CanonicalQuery = Builder.Query;
		return $"{Scheme}://{Host}{Port}{Path}{CanonicalQuery}";
	}

	[GeneratedRegex("<[^>]+>", RegexOptions.Compiled, 1000)]
	private static partial Regex HtmlRegex();

	private static bool TryParseUtcTimestamp(string? Timestamp, out DateTimeOffset? ParsedUtc)
	{
		ParsedUtc = null;
		if (string.IsNullOrWhiteSpace(Timestamp))
		{
			return false;
		}

		if (!DateTimeOffset.TryParse(Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset Parsed))
		{
			return false;
		}

		ParsedUtc = Parsed.ToUniversalTime();
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
