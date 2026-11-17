using System.Globalization;
using System.Text.Json.Nodes;
using HrOs.Ai.Core;
using HrOs.Ai.Prompts;

namespace HrOs.Ai.Screening;

/// <summary>A step could not produce a usable answer; the step falls back to a rule.</summary>
internal sealed class NodeException : Exception
{
    public NodeException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>What the screening steps share. Steps in one phase run together and write different fields.</summary>
internal sealed class ScreeningState
{
    private readonly object _gate = new();
    private readonly List<string> _errors = new();
    private readonly Dictionary<string, string> _promptVersions = new();

    public required string CvText { get; init; }
    public string? JobTitle { get; init; }
    public string? JobDescription { get; init; }

    /// <summary>The model for the screening steps, and an optional lighter one for reading the CV.</summary>
    public required string Model { get; init; }
    public string? ProfileModel { get; init; }

    public ScreeningProfile? Profile { get; set; }

    /// <summary>The CV text the fit-check steps may see: no name, contact or personal details.</summary>
    public string BlindCvText => BlindCv.Redact(CvText, Profile?.Name);
    public CvValidationResult? Validation { get; set; }
    public JobRequirements? Requirements { get; set; }
    public IReadOnlyList<MatchedRequirement> Matched { get; set; } = [];
    public IReadOnlyList<EvidenceItem> Evidence { get; set; } = [];
    public IReadOnlyList<UncertaintyItem> Uncertainties { get; set; } = [];
    public ScreeningRecommendation? Recommendation { get; set; }
    public bool RequiresHrReview { get; set; }

    public IReadOnlyList<string> Errors
    {
        get { lock (_gate) return _errors.ToList(); }
    }

    public IReadOnlyDictionary<string, string> PromptVersionsUsed
    {
        get { lock (_gate) return new Dictionary<string, string>(_promptVersions); }
    }

    public void AddError(string error)
    {
        lock (_gate)
        {
            if (!_errors.Contains(error)) _errors.Add(error);
        }
    }

    public void RecordPrompt(string version)
    {
        lock (_gate) _promptVersions[PromptVersions.Key(version)] = version;
    }
}

/// <summary>One JSON question to the model, shared by every screening step.</summary>
internal sealed class NodeLlm
{
    private readonly IAiClient _ai;
    private readonly TimeProvider _clock;

    public NodeLlm(IAiClient ai, TimeProvider clock)
    {
        _ai = ai;
        _clock = clock;
    }

    /// <summary>
    /// Anchors the model to today's real date, so a past date in a CV is never read as upcoming.
    /// </summary>
    public string DateHeader() =>
        $"CURRENT_DATE: {_clock.GetUtcNow().ToString("dddd, dd MMMM yyyy", CultureInfo.InvariantCulture)}\n";

    /// <exception cref="NodeException">The model is unavailable or did not answer with a JSON object.</exception>
    public async Task<JsonObject> JsonAsync(
        ScreeningState state,
        string promptName,
        string user,
        string version,
        string model,
        int? maxTokens,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _ai.ChatJsonAsync(
                [ChatMessage.System(PromptLibrary.Get(promptName)), ChatMessage.User(user)],
                model: model,
                maxTokens: maxTokens,
                cancellationToken: cancellationToken);
            state.RecordPrompt(version);
            return result;
        }
        catch (AiUnavailableException e)
        {
            throw new NodeException($"AI unavailable: {e.Message}", e);
        }
    }

    public static string Cut(string text, int max) => text.Length > max ? text[..max] : text;
}
