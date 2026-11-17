using HrOs.Ai.Core;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Screening;

public sealed record ScreeningInput(
    string CvText,
    string? JobTitle,
    string? JobDescription,
    string? ApplicationId = null,
    string? CandidateId = null);

/// <summary>Everything the seven steps produced, kept so HR can see why a score is what it is.</summary>
public sealed record ScreeningOutcome(
    ScreeningProfile Profile,
    CvValidationResult Validation,
    JobRequirements Requirements,
    IReadOnlyList<MatchedRequirement> Matched,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<UncertaintyItem> Uncertainties,
    ScreeningRecommendation Recommendation,
    IReadOnlyList<string> Errors,
    IReadOnlyDictionary<string, string> PromptVersions,
    string Model,
    bool RequiresHrReview);

/// <summary>
/// The screening pipeline: read the CV and the job in parallel, check the CV and match it to the
/// requirements, verify the evidence and list what is uncertain, then recommend. The steps that
/// can run side by side do. The result is advice for HR; nothing here decides about a person.
/// </summary>
public sealed class ScreeningPipeline
{
    private readonly NodeLlm _llm;
    private readonly AiOptions _options;

    public ScreeningPipeline(IAiClient ai, TimeProvider clock, IOptions<AiOptions> options)
    {
        _llm = new NodeLlm(ai, clock);
        _options = options.Value;
    }

    public async Task<ScreeningOutcome> RunAsync(ScreeningInput input, string model, CancellationToken cancellationToken = default)
    {
        var state = new ScreeningState
        {
            CvText = input.CvText,
            JobTitle = input.JobTitle,
            JobDescription = input.JobDescription,
            Model = model,
            ProfileModel = _options.EffectiveProfileModel,
        };

        // Same order as the LangGraph it replaces: the second step needs the first's output.
        await Task.WhenAll(
            ExtractionNode.RunAsync(_llm, state, cancellationToken),
            RequirementsNode.RunAsync(_llm, state, cancellationToken));
        await Task.WhenAll(
            ValidationNode.RunAsync(_llm, state, cancellationToken),
            MatchingNode.RunAsync(_llm, state, cancellationToken));
        await Task.WhenAll(
            EvidenceNode.RunAsync(_llm, state, cancellationToken),
            UncertaintyNode.RunAsync(_llm, state, cancellationToken));
        await RecommendationNode.RunAsync(_llm, state, cancellationToken);

        return new ScreeningOutcome(
            state.Profile!,
            state.Validation!,
            state.Requirements!,
            state.Matched,
            state.Evidence,
            state.Uncertainties,
            state.Recommendation!,
            state.Errors,
            state.PromptVersionsUsed,
            $"vllm/{model}",
            state.RequiresHrReview);
    }
}
