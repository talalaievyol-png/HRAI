using System.Text.Json.Nodes;
using HrOs.Ai.Core;
using HrOs.Ai.Cv;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Screening;

/// <summary>A requirement HR should look at, with the evidence or the lack of it.</summary>
public sealed record RequirementFinding(string Requirement, MatchStatus Status, string? Evidence);

/// <summary>
/// A screening, shaped for storing and showing: the evidence list, what is missing, what is
/// uncertain, and the flags. Advice for HR; the application status is not changed by this.
/// </summary>
public sealed record ScreeningReport(
    MatchStatus Recommendation,
    int Score,
    string Reason,
    IReadOnlyList<MatchedRequirement> Evidence,
    IReadOnlyList<RequirementFinding> Missing,
    IReadOnlyList<RequirementFinding> Uncertain,
    bool RequiresHrReview,
    string Model,
    bool FallbackUsed,
    string? FallbackModel,
    IReadOnlyList<string> Errors,
    IReadOnlyDictionary<string, string> PromptVersions);

/// <summary>Screening took longer than the time limit. The caller falls back to a simpler screening.</summary>
public sealed class ScreeningTimeoutException : Exception
{
    public ScreeningTimeoutException(string model, int seconds)
        : base($"Screening exceeded {seconds}s on model {model}") { }
}

public sealed class ScreeningService
{
    private readonly ScreeningPipeline _pipeline;
    private readonly AiOptions _options;

    public ScreeningService(ScreeningPipeline pipeline, IOptions<AiOptions> options)
    {
        _pipeline = pipeline;
        _options = options.Value;
    }

    /// <summary>
    /// Screen one CV against one job under a hard time limit. If the result is uncertain and a
    /// second-opinion model is set, it is run again on that model and its answer is used.
    /// </summary>
    /// <exception cref="ScreeningTimeoutException">A run went past the time limit.</exception>
    public async Task<ScreeningReport> ScreenAsync(ScreeningInput input, CancellationToken cancellationToken = default)
    {
        var outcome = await RunCappedAsync(input, _options.EvaluationModel, cancellationToken);

        var fallbackModel = _options.FallbackEvaluationModel.Trim();
        var fallbackUsed = false;
        if (IsUncertain(outcome) && fallbackModel.Length > 0)
        {
            fallbackUsed = true;
            outcome = await RunCappedAsync(input, fallbackModel, cancellationToken);
        }

        return ToReport(outcome, fallbackUsed, fallbackUsed ? $"vllm/{fallbackModel}" : null);
    }

    private async Task<ScreeningOutcome> RunCappedAsync(ScreeningInput input, string model, CancellationToken cancellationToken)
    {
        var seconds = _options.ScreeningTimeoutSeconds;
        if (seconds <= 0) return await _pipeline.RunAsync(input, model, cancellationToken);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            return await _pipeline.RunAsync(input, model, limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ScreeningTimeoutException(model, seconds);
        }
    }

    /// <summary>Ambiguous when the label is UNCLEAR or any uncertainty is left open.</summary>
    internal static bool IsUncertain(ScreeningOutcome outcome) =>
        outcome.Recommendation.Recommendation == MatchStatus.Unclear || outcome.Uncertainties.Count > 0;

    internal static ScreeningReport ToReport(ScreeningOutcome outcome, bool fallbackUsed, string? fallbackModel)
    {
        var recommendation = outcome.Recommendation;
        return new ScreeningReport(
            recommendation.Recommendation,
            recommendation.Score,
            recommendation.Reason,
            outcome.Matched,
            outcome.Matched
                .Where(m => m.Status == MatchStatus.Missing)
                .Select(m => new RequirementFinding(m.Requirement, MatchStatus.Missing, m.Evidence))
                .ToList(),
            outcome.Uncertainties
                .Select(u => new RequirementFinding(u.Issue, MatchStatus.Unclear, null))
                .ToList(),
            // The pipeline's full flag: the model's own, open uncertainties, a CV problem or a step
            // that fell back to a rule. The recommendation's flag alone would miss those.
            outcome.RequiresHrReview,
            outcome.Model,
            fallbackUsed,
            fallbackModel,
            outcome.Errors,
            outcome.PromptVersions);
    }
}

/// <summary>The job text the screening reads: title, description and the listed requirements.</summary>
public static class JobDescriptionBuilder
{
    public static string Build(string? title, string? description, JsonNode? requirements)
    {
        var parts = new List<string> { title ?? "" };
        if (!string.IsNullOrEmpty(description)) parts.Add(description);

        var listed = RequirementLines(requirements);
        if (listed.Count > 0)
            parts.Add("Requirements:\n" + string.Join("\n", listed.Select(line => $"- {line}")));

        return string.Join("\n\n", parts.Where(part => part.Length > 0));
    }

    /// <summary>The requirement lines stored on a job: skills, must-haves and nice-to-haves, then experience and education.</summary>
    public static IReadOnlyList<string> RequirementLines(JsonNode? requirements)
    {
        var lines = new List<string>();
        switch (requirements)
        {
            case JsonObject map:
                foreach (var key in new[] { "skills", "requirements", "must_have", "nice_to_have" })
                {
                    switch (map[key])
                    {
                        case JsonArray list:
                            lines.AddRange(list.Select(item => JsonRead.Text(item) ?? item?.ToJsonString() ?? "").Where(item => item.Length > 0));
                            break;
                        case JsonValue value when JsonRead.Text(value) is { } text:
                            lines.Add(text);
                            break;
                    }
                }

                foreach (var key in new[] { "experience", "education" })
                {
                    if (JsonRead.Text(map[key]) is { } text) lines.Add(text);
                }

                break;
            case JsonArray list:
                lines.AddRange(list.Select(item => JsonRead.Text(item) ?? "").Where(item => item.Length > 0));
                break;
        }

        return lines;
    }
}
