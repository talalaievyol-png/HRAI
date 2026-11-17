using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HrOs.Ai.Core;
using HrOs.Ai.Cv;

namespace HrOs.Ai.Embeddings;

/// <summary>
/// Turns a CV and a job into vectors for the talent-pool match. What is embedded is plain,
/// ordered text, so the same candidate or job always gives the same vector.
/// </summary>
public sealed partial class EmbeddingService
{
    private readonly IAiClient _ai;

    public EmbeddingService(IAiClient ai) => _ai = ai;

    [GeneratedRegex(@"[a-z0-9+#.]+")]
    private static partial Regex TermPattern();

    /// <summary>A profile with neither skills nor experience has nothing worth embedding.</summary>
    public static bool ShouldEmbed(CandidateProfile profile) => profile.Skills.Count > 0 || profile.Experience.Count > 0;

    /// <summary>The name, skills, jobs and education, one per line.</summary>
    public static string CandidateSource(CandidateProfile profile)
    {
        var lines = new List<string?> { profile.Name };
        lines.AddRange(profile.Skills);
        lines.AddRange(profile.Experience.Select(Describe));
        lines.AddRange(profile.Education.Select(Describe));
        return string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    /// <summary>The words a job asks for: its skills, experience, education and listed requirements.</summary>
    public static IReadOnlyList<string> JobTerms(JsonNode? requirements)
    {
        var terms = new List<string>();
        if (requirements is not JsonObject map) return terms;

        foreach (var key in new[] { "skills", "experience", "education", "requirements" })
        {
            var text = map[key] switch
            {
                JsonArray list => string.Join(" ", list.Select(item => JsonRead.Text(item) ?? "")),
                JsonValue value => JsonRead.Text(value) ?? "",
                _ => "",
            };
            terms.AddRange(Terms(text));
        }

        return terms.Distinct().ToList();
    }

    /// <summary>Null when the profile has nothing to embed or the embedding server cannot answer.</summary>
    public Task<float[]?> EmbedCandidateAsync(CandidateProfile profile, CancellationToken cancellationToken = default) =>
        ShouldEmbed(profile)
            ? _ai.EmbedAsync(CandidateSource(profile), cancellationToken: cancellationToken)
            : Task.FromResult<float[]?>(null);

    public Task<float[]?> EmbedJobAsync(JsonNode? requirements, CancellationToken cancellationToken = default)
    {
        var terms = JobTerms(requirements);
        return terms.Count == 0
            ? Task.FromResult<float[]?>(null)
            : _ai.EmbedAsync(string.Join(" ", terms), cancellationToken: cancellationToken);
    }

    internal static IReadOnlyList<string> Terms(string text) =>
        TermPattern().Matches(text.ToLowerInvariant()).Select(match => match.Value).ToList();

    private static string Describe(ProfileEntry entry) =>
        string.Join(" ", entry.Values.Where(value => !string.IsNullOrWhiteSpace(value)));
}
