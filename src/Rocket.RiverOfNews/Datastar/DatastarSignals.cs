using System.Text.Json;

namespace Rocket.RiverOfNews.Datastar;

internal static class DatastarSignals
{
public static string GetString(IReadOnlyDictionary<string, JsonElement> signals, string name)
{
return signals.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
? value.GetString() ?? string.Empty
: string.Empty;
}

public static int GetInt(IReadOnlyDictionary<string, JsonElement> signals, string name)
{
return signals.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
? value.GetInt32()
: 0;
}

public static string[] GetCsvValues(IReadOnlyDictionary<string, JsonElement> signals, string name)
{
return ParseCsv(GetString(signals, name));
}

public static string[] ParseCsv(string value)
{
if (string.IsNullOrWhiteSpace(value))
{
return [];
}

return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static string ToCsv(IEnumerable<string> values)
{
return string.Join(",", values.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));
}

public static Dictionary<string, JsonElement> WithString(
IReadOnlyDictionary<string, JsonElement> source,
string name,
string value)
{
Dictionary<string, JsonElement> updated = new(source);
using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(value));
updated[name] = document.RootElement.Clone();
return updated;
}
}
