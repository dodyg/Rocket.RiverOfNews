using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using Rocket.OPML;
using Rocket.RiverOfNews.Api;
using Rocket.RiverOfNews.Data;
using Rocket.Syndication;
using Rocket.Syndication.Models;

namespace Rocket.RiverOfNews.Services;

public sealed class OpmlImportService
{
	private readonly HttpClient HttpClient;
	private readonly SqliteConnectionFactory ConnectionFactory;
	private readonly ISyndicationClient SyndicationClient;

	public OpmlImportService(
		HttpClient httpClient,
		SqliteConnectionFactory connectionFactory,
		ISyndicationClient syndicationClient)
	{
		ArgumentNullException.ThrowIfNull(httpClient);
		ArgumentNullException.ThrowIfNull(connectionFactory);
		ArgumentNullException.ThrowIfNull(syndicationClient);

		HttpClient = httpClient;
		ConnectionFactory = connectionFactory;
		SyndicationClient = syndicationClient;
	}

	public async Task<OpmlImportResponse> FetchFeedsAsync(string opmlUrl, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(opmlUrl);

		string normalizedOpmlUrl = RiverDataAccess.NormalizeUrl(opmlUrl.Trim());
		using HttpRequestMessage request = new(HttpMethod.Get, normalizedOpmlUrl);
		using HttpResponseMessage response = await HttpClient.SendAsync(
			request,
			HttpCompletionOption.ResponseHeadersRead,
			cancellationToken);
		response.EnsureSuccessStatusCode();

		await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
		ParseResult<OpmlDocument> parseResult = await OpmlDocument.ParseAsync(
			stream,
			OpmlParserOptions.Default,
			cancellationToken);

		if (!parseResult.IsSuccess || parseResult.Document is null)
		{
			string errorMessage = parseResult.Errors.FirstOrDefault()?.Message ?? "The remote document could not be parsed as OPML.";
			throw new InvalidOperationException(errorMessage);
		}

		ValidationResult validationResult = parseResult.Document.Validate();
		HashSet<string> existingNormalizedUrls = await GetExistingNormalizedUrlsAsync(cancellationToken);
		IReadOnlyList<OpmlImportedFeedCandidate> feeds = BuildImportedFeeds(parseResult.Document, existingNormalizedUrls);
		IReadOnlyList<string> diagnostics = BuildDiagnostics(parseResult, validationResult);

		return new OpmlImportResponse
		{
			SourceUrl = normalizedOpmlUrl,
			DocumentTitle = string.IsNullOrWhiteSpace(parseResult.Document.Head.Title) ? null : parseResult.Document.Head.Title.Trim(),
			Feeds = feeds,
			Diagnostics = diagnostics
		};
	}

	public async Task<IReadOnlyList<OpmlImportedFeedCandidate>> CheckFeedsAsync(
		IReadOnlyList<OpmlImportedFeedCandidate> feeds,
		IReadOnlyCollection<string> selectedFeedIds,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(feeds);
		ArgumentNullException.ThrowIfNull(selectedFeedIds);

		HashSet<string> selectedSet = selectedFeedIds.ToHashSet(StringComparer.Ordinal);
		List<OpmlImportedFeedCandidate> updatedFeeds = new(feeds.Count);
		foreach (OpmlImportedFeedCandidate feed in feeds)
		{
			if (!selectedSet.Contains(feed.Id))
			{
				updatedFeeds.Add(feed);
				continue;
			}

			if (string.IsNullOrWhiteSpace(feed.NormalizedUrl))
			{
				updatedFeeds.Add(feed with
				{
					HealthStatus = "invalid",
					ProperFormat = false,
					LastUpdatedAt = null,
					Message = "Feed URL is invalid or uses an unsupported scheme."
				});
				continue;
			}

			FeedResult feedResult = await SyndicationClient.GetFeedAsync(feed.Url, cancellationToken);
			if (!feedResult.IsSuccess || feedResult.Feed is null)
			{
				string errorMessage = feedResult.Error?.Message ?? "Feed fetch failed.";
				updatedFeeds.Add(feed with
				{
					HealthStatus = "unhealthy",
					ProperFormat = false,
					LastUpdatedAt = null,
					Message = errorMessage
				});
				continue;
			}

			Feed parsedFeed = feedResult.Feed;
			DateTimeOffset? lastUpdatedAt = GetLastUpdatedAt(parsedFeed);
			string statusMessage = BuildHealthMessage(parsedFeed);
			string resolvedTitle = string.IsNullOrWhiteSpace(parsedFeed.Title)
				? feed.Title
				: parsedFeed.Title.Trim();
			updatedFeeds.Add(feed with
			{
				Title = resolvedTitle,
				HealthStatus = "healthy",
				ProperFormat = true,
				LastUpdatedAt = lastUpdatedAt?.ToString("O", CultureInfo.InvariantCulture),
				Message = statusMessage
			});
		}

		return await RefreshSubscriptionStateAsync(updatedFeeds, cancellationToken);
	}

	public async Task<IReadOnlyList<OpmlImportedFeedCandidate>> RefreshSubscriptionStateAsync(
		IReadOnlyList<OpmlImportedFeedCandidate> feeds,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(feeds);

		HashSet<string> existingNormalizedUrls = await GetExistingNormalizedUrlsAsync(cancellationToken);
		return feeds
			.Select(feed => feed with
			{
				AlreadySubscribed = !string.IsNullOrWhiteSpace(feed.NormalizedUrl)
					&& existingNormalizedUrls.Contains(feed.NormalizedUrl)
			})
			.ToList();
	}

	private async Task<HashSet<string>> GetExistingNormalizedUrlsAsync(CancellationToken cancellationToken)
	{
		const string sql = """
			SELECT normalized_url
			FROM feeds;
			""";

		await using SqliteConnection connection = await ConnectionFactory.OpenConnectionAsync(cancellationToken);
		IReadOnlyList<string> existingUrls = (await connection.QueryAsync<string>(
			new CommandDefinition(sql, cancellationToken: cancellationToken))).AsList();
		return existingUrls.ToHashSet(StringComparer.Ordinal);
	}

	private static IReadOnlyList<OpmlImportedFeedCandidate> BuildImportedFeeds(
		OpmlDocument document,
		IReadOnlySet<string> existingNormalizedUrls)
	{
		List<OpmlImportedFeedCandidate> feeds = [];
		HashSet<string> seenKeys = new(StringComparer.Ordinal);
		CollectImportedFeeds(document.Body.Outlines, string.Empty, feeds, seenKeys, existingNormalizedUrls);
		return feeds;
	}

	private static void CollectImportedFeeds(
		IReadOnlyList<Outline> outlines,
		string folderPath,
		ICollection<OpmlImportedFeedCandidate> feeds,
		ISet<string> seenKeys,
		IReadOnlySet<string> existingNormalizedUrls)
	{
		foreach (Outline outline in outlines)
		{
			string title = FirstNonEmpty(outline.Title, outline.Text, outline.XmlUrl?.Original, "(untitled feed)");
			string? rawXmlUrl = outline.XmlUrl?.Original?.Trim();
			string? normalizedUrl = TryNormalizeFeedUrl(rawXmlUrl);
			if (!string.IsNullOrWhiteSpace(rawXmlUrl))
			{
				string dedupeKey = normalizedUrl ?? $"raw:{rawXmlUrl}";
				if (seenKeys.Add(dedupeKey))
				{
					feeds.Add(new OpmlImportedFeedCandidate
					{
						Id = BuildFeedId(dedupeKey),
						Title = title,
						Url = rawXmlUrl,
						NormalizedUrl = normalizedUrl,
						SiteUrl = outline.HtmlUrl?.Original?.Trim(),
						CategoryPath = string.IsNullOrWhiteSpace(folderPath) ? null : folderPath,
						HealthStatus = string.IsNullOrWhiteSpace(normalizedUrl) ? "invalid" : "unchecked",
						ProperFormat = null,
						LastUpdatedAt = null,
						Message = null,
						AlreadySubscribed = normalizedUrl is not null && existingNormalizedUrls.Contains(normalizedUrl)
					});
				}
			}

			if (outline.Children.Count == 0)
			{
				continue;
			}

			string nextFolderPath = folderPath;
			if (string.IsNullOrWhiteSpace(rawXmlUrl))
			{
				nextFolderPath = string.IsNullOrWhiteSpace(folderPath)
					? title
					: $"{folderPath} / {title}";
			}

			CollectImportedFeeds(outline.Children, nextFolderPath, feeds, seenKeys, existingNormalizedUrls);
		}
	}

	private static IReadOnlyList<string> BuildDiagnostics(
		ParseResult<OpmlDocument> parseResult,
		ValidationResult validationResult)
	{
		List<string> diagnostics = [];
		diagnostics.AddRange(parseResult.Diagnostics.Select(FormatParseDiagnostic));
		diagnostics.AddRange(validationResult.Diagnostics.Select(FormatValidationDiagnostic));
		return diagnostics
			.Where(static message => !string.IsNullOrWhiteSpace(message))
			.Distinct(StringComparer.Ordinal)
			.ToList();
	}

	private static string FormatParseDiagnostic(ParseDiagnostic diagnostic)
	{
		return string.IsNullOrWhiteSpace(diagnostic.Path)
			? diagnostic.Message
			: $"{diagnostic.Path}: {diagnostic.Message}";
	}

	private static string FormatValidationDiagnostic(ValidationDiagnostic diagnostic)
	{
		return string.IsNullOrWhiteSpace(diagnostic.Path)
			? diagnostic.Message
			: $"{diagnostic.Path}: {diagnostic.Message}";
	}

	private static string? TryNormalizeFeedUrl(string? url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return null;
		}

		try
		{
			return RiverDataAccess.NormalizeUrl(url);
		}
		catch (UriFormatException)
		{
			return null;
		}
	}

	private static string BuildFeedId(string value)
	{
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
		return Convert.ToHexString(hash)[..16].ToLowerInvariant();
	}

	private static string FirstNonEmpty(params string?[] values)
	{
		foreach (string? value in values)
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value.Trim();
			}
		}

		return string.Empty;
	}

	private static DateTimeOffset? GetLastUpdatedAt(Feed feed)
	{
		if (feed.LastUpdated.HasValue)
		{
			return feed.LastUpdated.Value.ToUniversalTime();
		}

		IEnumerable<DateTimeOffset> itemDates = (feed.Items ?? [])
			.Select(item => item.UpdatedDate ?? item.PublishedDate)
			.OfType<DateTimeOffset>()
			.Select(static itemDate => itemDate.ToUniversalTime());
		return itemDates.Any() ? itemDates.Max() : null;
	}

	private static string BuildHealthMessage(Feed feed)
	{
		int itemCount = feed.Items?.Count ?? 0;
		string feedType = feed.Type.ToString();
		return $"Feed parsed as {feedType} with {itemCount.ToString(CultureInfo.InvariantCulture)} item(s).";
	}
}
