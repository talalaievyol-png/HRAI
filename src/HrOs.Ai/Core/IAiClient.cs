using System.Text.Json.Nodes;

namespace HrOs.Ai.Core;

public sealed record ChatMessage(string Role, string Content)
{
    public static ChatMessage System(string content) => new("system", content);

    public static ChatMessage User(string content) => new("user", content);
}

/// <summary>
/// The one door to the local model runtime. Every AI service goes through it, so every
/// prompt is sanitised and CNIC-free no matter which service sent it.
/// </summary>
public interface IAiClient
{
    /// <summary>One structured (JSON object) chat call.</summary>
    /// <exception cref="AiUnavailableException">Server down, timeout, or a non-JSON answer.</exception>
    Task<JsonObject> ChatJsonAsync(
        IReadOnlyList<ChatMessage> messages,
        string? model = null,
        int? maxTokens = null,
        double temperature = 0.1,
        CancellationToken cancellationToken = default);

    /// <summary>The embedding of a text, or null when the embedding server cannot give one.</summary>
    Task<float[]?> EmbedAsync(string text, string? model = null, CancellationToken cancellationToken = default);

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
