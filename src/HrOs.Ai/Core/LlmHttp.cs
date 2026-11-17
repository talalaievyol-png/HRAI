using System.Text.Json;
using System.Text.Json.Nodes;

namespace HrOs.Ai.Core;

/// <summary>
/// Request and response shapes for vLLM's OpenAI-compatible API. Everything here is pure:
/// it builds payloads and reads bodies. The client does the HTTP, tracing and errors.
/// Same rules as <c>ai/llm_http.py</c> in the Python app.
/// </summary>
public static class LlmHttp
{
    public static JsonObject ChatPayload(
        string model,
        IReadOnlyList<ChatMessage> messages,
        double temperature,
        int? maxTokens,
        bool jsonMode = true)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(messages
                .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content })
                .ToArray()),
            ["temperature"] = temperature,
            ["stream"] = false,
            // Qwen3 reasons by default; structured HR work does not need the trace.
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
        };
        if (maxTokens is > 0) payload["max_tokens"] = maxTokens;
        if (jsonMode) payload["response_format"] = new JsonObject { ["type"] = "json_object" };
        return payload;
    }

    public static string ChatContent(JsonNode body) =>
        First(body["choices"])?["message"]?["content"]?.GetValue<string>() ?? "";

    /// <summary>The first item of a JSON array, or null when it is missing or empty.</summary>
    private static JsonNode? First(JsonNode? array) => array is JsonArray { Count: > 0 } items ? items[0] : null;

    /// <summary>The model's JSON answer. Tolerates a ```json fence around it.</summary>
    public static JsonObject ParseJsonObject(string content)
    {
        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = text.Trim('`');
            if (text.StartsWith("json", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        }

        var node = JsonNode.Parse(text);
        return node as JsonObject
               ?? throw new JsonException("The model answered with JSON that is not an object.");
    }

    /// <summary>Token counts in input/output form, or null when the server sent none.</summary>
    public static (int? Input, int? Output) Usage(JsonNode body)
    {
        var usage = body["usage"];
        return (usage?["prompt_tokens"]?.GetValue<int>(), usage?["completion_tokens"]?.GetValue<int>());
    }

    public static JsonObject EmbedPayload(string model, string text) =>
        new() { ["model"] = model, ["input"] = text };

    public static float[]? Embedding(JsonNode body)
    {
        if (First(body["data"])?["embedding"] is not JsonArray vector || vector.Count == 0) return null;
        return vector.Select(v => v!.GetValue<float>()).ToArray();
    }

    public static string HealthUrl(string baseUrl) => $"{baseUrl.TrimEnd('/')}/models";
}
