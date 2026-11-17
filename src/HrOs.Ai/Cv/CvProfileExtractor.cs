using HrOs.Ai.Core;
using HrOs.Ai.Prompts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Cv;

/// <summary>
/// CV text to a structured profile. The model reads the CV; when it is down or answers
/// badly, the text rules in <see cref="FallbackProfileExtractor"/> take over, so the form
/// still opens with something filled in.
/// </summary>
public sealed class CvProfileExtractor
{
    private const int MaxCvChars = 30000;
    private const int MaxTokens = 3000;

    private readonly IAiClient _ai;
    private readonly AiOptions _options;
    private readonly ILogger<CvProfileExtractor> _logger;

    public CvProfileExtractor(IAiClient ai, IOptions<AiOptions> options, ILogger<CvProfileExtractor> logger)
    {
        _ai = ai;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<CandidateProfile> ExtractAsync(string cvText, CancellationToken cancellationToken = default)
    {
        var cv = cvText.Length > MaxCvChars ? cvText[..MaxCvChars] : cvText;
        try
        {
            var json = await _ai.ChatJsonAsync(
                [ChatMessage.System(PromptLibrary.Get("cv_profile_extraction")), ChatMessage.User(cv)],
                model: _options.EffectiveProfileModel,
                maxTokens: MaxTokens,
                temperature: 0.0,
                cancellationToken: cancellationToken);
            return ProfileNormalizer.FromModel(json);
        }
        catch (AiUnavailableException e)
        {
            _logger.LogWarning("CV profile extraction used the text rules instead of the model: {Reason}", e.Message);
            return FallbackProfileExtractor.Extract(cvText);
        }
    }
}
