using System.Text.Json.Nodes;
using HrOs.Ai.Core;

namespace HrOs.Ai.Tests.Support;

/// <summary>
/// Answers each screening step by recognising its system prompt, so a test scripts the model per
/// step without caring about call order (steps in one phase run together).
/// </summary>
public sealed class RoutedAi : IAiClient
{
    public const string Extraction = "CV information extraction component";
    public const string Validation = "CV validation component";
    public const string Requirements = "job requirement extraction component";
    public const string Matching = "requirement matching component";
    public const string Evidence = "check whether claims about a candidate";
    public const string Uncertainty = "list the points in a screening";
    public const string Recommendation = "recruitment screening recommendation component";

    private readonly Dictionary<string, Func<JsonObject>> _answers = new();
    private readonly object _gate = new();

    public List<(string Step, string? Model, string User)> Calls { get; } = new();

    public RoutedAi Answer(string step, string json)
    {
        _answers[step] = () => JsonNode.Parse(json)!.AsObject();
        return this;
    }

    public RoutedAi Fail(string step)
    {
        _answers[step] = () => throw new AiUnavailableException("model down");
        return this;
    }

    public Task<JsonObject> ChatJsonAsync(
        IReadOnlyList<ChatMessage> messages, string? model = null, int? maxTokens = null,
        double temperature = 0.1, CancellationToken cancellationToken = default)
    {
        var system = messages[0].Content;
        var step = _answers.Keys.FirstOrDefault(marker => system.Contains(marker))
                   ?? throw new AiUnavailableException("no scripted answer for this step");
        lock (_gate) Calls.Add((step, model, messages[1].Content));
        return Task.FromResult(_answers[step]());
    }

    public Task<float[]?> EmbedAsync(string text, string? model = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<float[]?>(null);

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public int CallsFor(string step) => Calls.Count(call => call.Step == step);
}

/// <summary>A model that never answers, to prove the time limit.</summary>
public sealed class HangingAi : IAiClient
{
    public async Task<JsonObject> ChatJsonAsync(
        IReadOnlyList<ChatMessage> messages, string? model = null, int? maxTokens = null,
        double temperature = 0.1, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new JsonObject();
    }

    public Task<float[]?> EmbedAsync(string text, string? model = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<float[]?>(null);

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
}

public sealed class FixedClock : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedClock(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
