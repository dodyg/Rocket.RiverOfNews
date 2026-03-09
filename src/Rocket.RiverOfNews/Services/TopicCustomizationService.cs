using System.Collections.Frozen;
using System.Globalization;
using System.Text.RegularExpressions;
using Rocket.RiverOfNews.Api;
using Rocket.RiverOfNews.Data;

namespace Rocket.RiverOfNews.Services;

public sealed partial class TopicCustomizationService
{
	private static readonly FrozenSet<string> StopWords = CreateStopWords();
	private const int MaxSuggestedTopics = 18;
	private const int MaxMatchingItems = 40;
	private readonly SqliteConnectionFactory ConnectionFactory;

	public TopicCustomizationService(SqliteConnectionFactory connectionFactory)
	{
		ArgumentNullException.ThrowIfNull(connectionFactory);
		ConnectionFactory = connectionFactory;
	}

	public async Task<CustomizePageModel> BuildPageModelAsync(string? selectedTopic, CancellationToken cancellationToken)
	{
		IReadOnlyList<RiverItemResponse> items = await RiverDataAccess.GetAllItemsForCustomizationAsync(ConnectionFactory, cancellationToken);
		if (items.Count == 0)
		{
			return new CustomizePageModel
			{
				SourceItemCount = 0,
				Topics = [],
				SelectedTopic = null,
				MatchingItems = []
			};
		}

		IReadOnlyList<AnalyzedItem> analyzedItems = [.. items.Select(CreateAnalyzedItem)];
		IReadOnlyList<TopicSuggestion> topics = BuildTopicSuggestions(analyzedItems);
		string? normalizedSelectedTopic = NormalizeTopicPhrase(selectedTopic);
		string? effectiveTopic = !string.IsNullOrWhiteSpace(normalizedSelectedTopic)
			? normalizedSelectedTopic
			: topics.Count > 0
				? topics[0].Name
				: null;
		IReadOnlyList<RiverItemResponse> matchingItems = string.IsNullOrWhiteSpace(effectiveTopic)
			? []
			: [.. MatchItems(analyzedItems, effectiveTopic).Select(static match => match.Item)];

		return new CustomizePageModel
		{
			SourceItemCount = items.Count,
			Topics = topics,
			SelectedTopic = effectiveTopic,
			MatchingItems = matchingItems
		};
	}

	private static AnalyzedItem CreateAnalyzedItem(RiverItemResponse item)
	{
		string title = item.Title;
		string body = string.IsNullOrWhiteSpace(item.Snippet)
			? title
			: $"{title} {item.Snippet}";
		string[] titleTokens = Tokenize(title);
		string[] bodyTokens = Tokenize(body);
		DateTimeOffset publishedAtUtc = ParseUtc(item.PublishedAt);

		return new AnalyzedItem(
			item,
			NormalizeTopicPhrase(title) ?? string.Empty,
			NormalizeTopicPhrase(body) ?? string.Empty,
			titleTokens,
			titleTokens.ToFrozenSet(StringComparer.Ordinal),
			bodyTokens,
			bodyTokens.ToFrozenSet(StringComparer.Ordinal),
			publishedAtUtc);
	}

	private static IReadOnlyList<TopicSuggestion> BuildTopicSuggestions(IReadOnlyList<AnalyzedItem> analyzedItems)
	{
		Dictionary<string, TopicAccumulator> candidates = new(StringComparer.Ordinal);

		foreach (AnalyzedItem item in analyzedItems)
		{
			HashSet<string> candidatesForItem = new(StringComparer.Ordinal);
			foreach (string candidate in ExtractCandidatePhrases(item.TitleTokens))
			{
				candidatesForItem.Add(candidate);
			}

			foreach (string candidate in candidatesForItem)
			{
				int score = GetCandidateScore(candidate);
				if (candidates.TryGetValue(candidate, out TopicAccumulator? existing))
				{
					candidates[candidate] = existing with
					{
						Score = existing.Score + score,
						ItemCount = existing.ItemCount + 1
					};
				}
				else
				{
					candidates.Add(candidate, new TopicAccumulator(candidate, score, 1));
				}
			}
		}

		List<TopicSuggestion> topics = [];
		foreach (TopicAccumulator candidate in candidates.Values
			.Where(static candidate => candidate.ItemCount > 1 || CountWords(candidate.Name) > 1)
			.OrderByDescending(static candidate => candidate.Score)
			.ThenByDescending(static candidate => candidate.ItemCount)
			.ThenByDescending(static candidate => CountWords(candidate.Name))
			.ThenBy(static candidate => candidate.Name, StringComparer.Ordinal))
		{
			if (ShouldSkipTopic(topics, candidate))
			{
				continue;
			}

			topics.Add(new TopicSuggestion
			{
				Name = candidate.Name,
				ItemCount = candidate.ItemCount
			});

			if (topics.Count == MaxSuggestedTopics)
			{
				break;
			}
		}

		return topics;
	}

	private static IEnumerable<string> ExtractCandidatePhrases(IReadOnlyList<string> titleTokens)
	{
		for (int phraseLength = 3; phraseLength >= 2; phraseLength--)
		{
			if (titleTokens.Count < phraseLength)
			{
				continue;
			}

			for (int index = 0; index <= titleTokens.Count - phraseLength; index++)
			{
				yield return string.Join(' ', titleTokens.Skip(index).Take(phraseLength));
			}
		}
	}

	private static bool ShouldSkipTopic(IReadOnlyList<TopicSuggestion> existingTopics, TopicAccumulator candidate)
	{
		FrozenSet<string> candidateTokens = Tokenize(candidate.Name).ToFrozenSet(StringComparer.Ordinal);
		foreach (TopicSuggestion existingTopic in existingTopics)
		{
			FrozenSet<string> existingTokens = Tokenize(existingTopic.Name).ToFrozenSet(StringComparer.Ordinal);
			int sharedTokenCount = candidateTokens.Count(token => existingTokens.Contains(token));
			if (sharedTokenCount == 0)
			{
				continue;
			}

			bool candidateContainedByExisting = sharedTokenCount == candidateTokens.Count && candidate.ItemCount <= existingTopic.ItemCount;
			bool existingContainedByCandidate = sharedTokenCount == existingTokens.Count && candidate.ItemCount == existingTopic.ItemCount;
			if (candidateContainedByExisting || existingContainedByCandidate)
			{
				return true;
			}
		}

		return false;
	}

	private static IReadOnlyList<TopicMatch> MatchItems(IReadOnlyList<AnalyzedItem> analyzedItems, string topic)
	{
		string[] topicTokens = Tokenize(topic);
		if (topicTokens.Length == 0)
		{
			return [];
		}

		List<TopicMatch> matches = [];
		foreach (AnalyzedItem item in analyzedItems)
		{
			TopicMatch? match = TryCreateMatch(item, topic, topicTokens);
			if (match is not null)
			{
				matches.Add(match);
			}
		}

		return
		[
			.. matches
				.OrderByDescending(static match => match.Score)
				.ThenByDescending(static match => match.PublishedAtUtc)
				.Take(MaxMatchingItems)
		];
	}

	private static TopicMatch? TryCreateMatch(AnalyzedItem item, string topic, IReadOnlyList<string> topicTokens)
	{
		bool titlePhraseMatch = ContainsWholePhrase(item.NormalizedTitle, topic);
		bool bodyPhraseMatch = ContainsWholePhrase(item.NormalizedBody, topic);
		int titleTokenMatches = CountMatches(topicTokens, item.TitleTokenSet);
		int bodyTokenMatches = CountMatches(topicTokens, item.BodyTokenSet);
		bool contiguousTitleMatch = ContainsOrderedTokens(item.TitleTokens, topicTokens);
		bool contiguousBodyMatch = ContainsOrderedTokens(item.BodyTokens, topicTokens);

		int score = 0;
		if (titlePhraseMatch)
		{
			score += 120;
		}

		if (bodyPhraseMatch)
		{
			score += 60;
		}

		score += titleTokenMatches * 24;
		score += bodyTokenMatches * 12;

		if (topicTokens.Count > 1 && titleTokenMatches == topicTokens.Count)
		{
			score += 36;
		}

		if (topicTokens.Count > 1 && bodyTokenMatches == topicTokens.Count)
		{
			score += 20;
		}

		if (contiguousTitleMatch)
		{
			score += 18;
		}

		if (contiguousBodyMatch)
		{
			score += 10;
		}

		bool isRelevant = titlePhraseMatch
			|| bodyPhraseMatch
			|| (topicTokens.Count == 1 ? score >= 36 : bodyTokenMatches == topicTokens.Count || score >= 72);

		return isRelevant
			? new TopicMatch(item.Item, score, item.PublishedAtUtc)
			: null;
	}

	private static int CountMatches(IReadOnlyList<string> topicTokens, FrozenSet<string> itemTokens)
	{
		int matches = 0;
		foreach (string topicToken in topicTokens)
		{
			if (itemTokens.Contains(topicToken))
			{
				matches++;
			}
		}

		return matches;
	}

	private static bool ContainsOrderedTokens(IReadOnlyList<string> itemTokens, IReadOnlyList<string> topicTokens)
	{
		if (itemTokens.Count < topicTokens.Count)
		{
			return false;
		}

		for (int index = 0; index <= itemTokens.Count - topicTokens.Count; index++)
		{
			bool matches = true;
			for (int offset = 0; offset < topicTokens.Count; offset++)
			{
				if (!string.Equals(itemTokens[index + offset], topicTokens[offset], StringComparison.Ordinal))
				{
					matches = false;
					break;
				}
			}

			if (matches)
			{
				return true;
			}
		}

		return false;
	}

	private static bool ContainsWholePhrase(string normalizedText, string normalizedPhrase)
	{
		if (string.IsNullOrWhiteSpace(normalizedText) || string.IsNullOrWhiteSpace(normalizedPhrase))
		{
			return false;
		}

		if (string.Equals(normalizedText, normalizedPhrase, StringComparison.Ordinal))
		{
			return true;
		}

		return normalizedText.StartsWith($"{normalizedPhrase} ", StringComparison.Ordinal)
			|| normalizedText.EndsWith($" {normalizedPhrase}", StringComparison.Ordinal)
			|| normalizedText.Contains($" {normalizedPhrase} ", StringComparison.Ordinal);
	}

	private static string? NormalizeTopicPhrase(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		string[] tokens = Tokenize(value);
		return tokens.Length == 0
			? null
			: string.Join(' ', tokens);
	}

	private static string[] Tokenize(string? text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return [];
		}

		List<string> tokens = [];
		foreach (Match match in TokenRegex().Matches(text))
		{
			string token = NormalizeToken(match.Value);
			if (token.Length < 3 || StopWords.Contains(token) || IsNumeric(token))
			{
				continue;
			}

			tokens.Add(token);
		}

		return [.. tokens];
	}

	private static string NormalizeToken(string value)
	{
		string token = value.Trim('\'', '-', '_').ToLowerInvariant();
		return token;
	}

	private static bool IsNumeric(string token)
	{
		return double.TryParse(token, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
	}

	private static int GetCandidateScore(string candidate)
	{
		int wordCount = CountWords(candidate);
		return wordCount switch
		{
			>= 3 => 16,
			2 => 12,
			_ => 5
		};
	}

	private static int CountWords(string value)
	{
		return string.IsNullOrWhiteSpace(value)
			? 0
			: value.Count(static character => character == ' ') + 1;
	}

	private static DateTimeOffset ParseUtc(string value)
	{
		return DateTimeOffset.TryParse(
			value,
			CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
			out DateTimeOffset parsed)
			? parsed
			: DateTimeOffset.MinValue;
	}

	private static FrozenSet<string> CreateStopWords()
	{
		string[] stopWords =
		[
			"a", "about", "after", "all", "also", "amp", "and", "are", "around", "as", "at", "back", "because",
			"been", "before", "being", "between", "but", "can", "could", "daily", "for", "from", "get", "had",
			"has", "have", "how", "into", "its", "just", "like", "more", "most", "new", "news", "not", "now",
			"off", "our", "out", "over", "podcast", "said", "says", "should", "some", "than", "that", "the",
			"their", "them", "there", "these", "they", "this", "those", "through", "today", "under", "using",
			"video", "was", "were", "what", "when", "where", "which", "while", "who", "why", "will", "with",
			"you", "your"
		];

		return stopWords.ToFrozenSet(StringComparer.Ordinal);
	}

	[GeneratedRegex("[\\p{L}\\p{N}][\\p{L}\\p{N}'_-]*", RegexOptions.Compiled, 1000)]
	private static partial Regex TokenRegex();

	private sealed record AnalyzedItem(
		RiverItemResponse Item,
		string NormalizedTitle,
		string NormalizedBody,
		string[] TitleTokens,
		FrozenSet<string> TitleTokenSet,
		string[] BodyTokens,
		FrozenSet<string> BodyTokenSet,
		DateTimeOffset PublishedAtUtc);

	private sealed record TopicAccumulator(string Name, int Score, int ItemCount);
	private sealed record TopicMatch(RiverItemResponse Item, int Score, DateTimeOffset PublishedAtUtc);
}

public sealed record CustomizePageModel
{
	public required int SourceItemCount { get; init; }
	public required IReadOnlyList<TopicSuggestion> Topics { get; init; }
	public string? SelectedTopic { get; init; }
	public required IReadOnlyList<RiverItemResponse> MatchingItems { get; init; }
}

public sealed record TopicSuggestion
{
	public required string Name { get; init; }
	public required int ItemCount { get; init; }
}
