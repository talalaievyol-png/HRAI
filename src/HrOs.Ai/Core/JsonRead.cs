using System.Text.Json.Nodes;

namespace HrOs.Ai.Core;

/// <summary>Forgiving readers for model output: wrong types are repaired, never thrown on.</summary>
internal static class JsonRead
{
    /// <summary>A value as trimmed text; null for missing, null or blank. Numbers and booleans become text.</summary>
    public static string? Text(JsonNode? node)
    {
        var text = node switch
        {
            JsonValue value when value.TryGetValue<string>(out var s) => s,
            JsonValue value => value.ToJsonString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>A list of text. A comma or semicolon string is split; anything else non-list is empty.</summary>
    public static IReadOnlyList<string> TextList(JsonNode? node)
    {
        if (node is JsonValue)
        {
            return (Text(node) ?? "")
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        return node is JsonArray array ? array.Select(Text).OfType<string>().ToList() : [];
    }

    public static bool Bool(JsonNode? node, bool fallback) =>
        node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : fallback;

    /// <summary>A whole number, or null when the value is missing, fractional or not a number.</summary>
    public static int? WholeNumber(JsonNode? node)
    {
        if (node is not JsonValue value || !value.TryGetValue<double>(out var number)) return null;
        return Math.Abs(number - Math.Round(number)) < 1e-9 ? (int)Math.Round(number) : null;
    }
}
