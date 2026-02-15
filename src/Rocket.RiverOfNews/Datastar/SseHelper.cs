using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Rocket.RiverOfNews.Datastar;

public sealed class SseHelper(HttpResponse response)
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	public async Task StartAsync(CancellationToken cancellationToken = default)
	{
		response.ContentType = "text/event-stream";
		response.Headers.CacheControl = "no-cache";
		response.Headers.Connection = "keep-alive";
		await response.StartAsync(cancellationToken);
	}

	public async Task PatchElementsAsync(string elements, string? selector = null, string mode = "outer", CancellationToken cancellationToken = default)
	{
		await using StreamWriter writer = new(response.Body, new UTF8Encoding(false), leaveOpen: true);
		await writer.WriteLineAsync("event: datastar-patch-elements");
		await writer.WriteLineAsync($"data: mode {mode}");
		if (selector is not null)
		{
			await writer.WriteLineAsync($"data: selector {selector}");
		}

		await WriteMultilineDataAsync(writer, "elements", elements, cancellationToken);
		await writer.WriteLineAsync();
		await writer.FlushAsync(cancellationToken);
	}

	public async Task PatchSignalsAsync(object signals, CancellationToken cancellationToken = default)
	{
		await using StreamWriter writer = new(response.Body, new UTF8Encoding(false), leaveOpen: true);
		await writer.WriteLineAsync("event: datastar-patch-signals");
		string json = JsonSerializer.Serialize(signals, JsonOptions);
		await WriteMultilineDataAsync(writer, "signals", json, cancellationToken);
		await writer.WriteLineAsync();
		await writer.FlushAsync(cancellationToken);
	}

	private static async Task WriteMultilineDataAsync(StreamWriter writer, string prefix, string content, CancellationToken cancellationToken)
	{
		string[] lines = content.Split('\n');
		for (int i = 0; i < lines.Length; i++)
		{
			string line = lines[i];
			await writer.WriteLineAsync($"data: {prefix} {line}".AsMemory(), cancellationToken);
		}
	}
}

public static class SseHelperExtensions
{
	public static SseHelper CreateSseHelper(this HttpResponse response) => new(response);

	public static async Task<Dictionary<string, JsonElement>> ReadSignalsAsync(this HttpRequest request, CancellationToken cancellationToken = default)
	{
		using StreamReader reader = new(request.Body, Encoding.UTF8, leaveOpen: true);
		string body = await reader.ReadToEndAsync(cancellationToken);

		if (string.IsNullOrWhiteSpace(body))
		{
			return [];
		}

		Dictionary<string, JsonElement>? signals = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body, new JsonSerializerOptions
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		});

		return signals ?? [];
	}
}
