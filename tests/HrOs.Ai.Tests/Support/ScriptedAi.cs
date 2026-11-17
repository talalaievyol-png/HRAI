using System.Text.Json.Nodes;
using HrOs.Ai.Core;

namespace HrOs.Ai.Tests.Support;

/// <summary>An <see cref="IAiClient"/> that answers from a script and records what it was asked.</summary>
public sealed class ScriptedAi : IAiClient
{
    private readonly Func<IReadOnlyList<ChatMessage>, JsonObject> _chat;

    public ScriptedAi(Func<IReadOnlyList<ChatMessage>, JsonObject> chat) => _chat = chat;

    public static ScriptedAi Answering(string json) => new(_ => JsonNode.Parse(json)!.AsObject());

    public static ScriptedAi Down() => new(_ => throw new AiUnavailableException("model down"));

    public List<(IReadOnlyList<ChatMessage> Messages, string? Model, int? MaxTokens, double Temperature)> ChatCalls { get; } = new();

    public float[]? Embedding { get; set; }

    public bool Available { get; set; } = true;

    public Task<JsonObject> ChatJsonAsync(
        IReadOnlyList<ChatMessage> messages, string? model = null, int? maxTokens = null,
        double temperature = 0.1, CancellationToken cancellationToken = default)
    {
        ChatCalls.Add((messages, model, maxTokens, temperature));
        return Task.FromResult(_chat(messages));
    }

    public Task<float[]?> EmbedAsync(string text, string? model = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Embedding);

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(Available);
}
