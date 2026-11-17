using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HrOs.Ai.Screening;

/// <summary>How well the CV meets one requirement. Advice for HR, never a decision.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MatchStatus>))]
public enum MatchStatus
{
    [JsonStringEnumMemberName("MATCH")] Match,
    [JsonStringEnumMemberName("PARTIAL")] Partial,
    [JsonStringEnumMemberName("MISSING")] Missing,
    [JsonStringEnumMemberName("UNCLEAR")] Unclear,
}

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    [JsonStringEnumMemberName("LOW")] Low,
    [JsonStringEnumMemberName("MEDIUM")] Medium,
    [JsonStringEnumMemberName("HIGH")] High,
}

public static class ScreeningEnums
{
    public static string Wire(this MatchStatus status) => status switch
    {
        MatchStatus.Match => "MATCH",
        MatchStatus.Partial => "PARTIAL",
        MatchStatus.Missing => "MISSING",
        _ => "UNCLEAR",
    };

    public static MatchStatus? ParseMatchStatus(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "MATCH" => MatchStatus.Match,
        "PARTIAL" => MatchStatus.Partial,
        "MISSING" => MatchStatus.Missing,
        "UNCLEAR" => MatchStatus.Unclear,
        _ => null,
    };

    public static Severity ParseSeverity(string? text, Severity fallback = Severity.Medium) =>
        text?.Trim().ToUpperInvariant() switch
        {
            "LOW" => Severity.Low,
            "MEDIUM" => Severity.Medium,
            "HIGH" => Severity.High,
            _ => fallback,
        };
}

public sealed record MatchedRequirement(
    [property: JsonPropertyName("requirement")] string Requirement,
    [property: JsonPropertyName("status")] MatchStatus Status,
    [property: JsonPropertyName("evidence")] string? Evidence,
    [property: JsonPropertyName("is_mandatory")] bool IsMandatory,
    [property: JsonPropertyName("category")] string Category = Criterion.Other);

/// <summary>What a requirement is about, so HR can read a result by criterion.</summary>
public static class Criterion
{
    public const string Qualification = "qualification";
    public const string Experience = "experience";
    public const string Relevance = "relevance";
    public const string Skills = "skills";
    public const string Certifications = "certifications";
    public const string Other = "other";

    /// <summary>
    /// The fit of the CV's work to the duties of the job. Always judged, whatever the JD lists.
    /// </summary>
    public const string RelevanceRequirement =
        "Relevance: how closely the candidate's work history (for a fresher, their degree, " +
        "internships and projects) matches the duties of this role";

    public static bool IsRelevance(string requirement) =>
        requirement.Trim().StartsWith("relevance", StringComparison.OrdinalIgnoreCase);
}

public sealed record EvidenceItem(
    [property: JsonPropertyName("claim")] string Claim,
    [property: JsonPropertyName("supported")] bool Supported,
    [property: JsonPropertyName("evidence")] string? Evidence,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record UncertaintyItem(
    [property: JsonPropertyName("issue")] string Issue,
    [property: JsonPropertyName("impact")] Severity Impact,
    [property: JsonPropertyName("requires_hr_review")] bool RequiresHrReview);

public sealed record ValidationIssue(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("severity")] Severity Severity);

public sealed record CvValidationResult(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("issues")] IReadOnlyList<ValidationIssue> Issues,
    [property: JsonPropertyName("uncertainties")] IReadOnlyList<string> Uncertainties,
    [property: JsonPropertyName("requires_hr_review")] bool RequiresHrReview);

public sealed record ScreeningRecommendation(
    [property: JsonPropertyName("recommendation")] MatchStatus Recommendation,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("matched_requirements")] IReadOnlyList<string> MatchedRequirements,
    [property: JsonPropertyName("missing_requirements")] IReadOnlyList<string> MissingRequirements,
    [property: JsonPropertyName("uncertainties")] IReadOnlyList<string> Uncertainties,
    [property: JsonPropertyName("requires_hr_review")] bool RequiresHrReview);

/// <summary>Version tags of the prompts, recorded with every result so a score can be traced to its wording.</summary>
public static class PromptVersions
{
    public const string CvExtraction = "cv-extraction-v3";
    public const string CvValidation = "cv-validation-v3";
    public const string JobRequirements = "job-requirements-v2";
    public const string RequirementMatching = "requirement-matching-v4";
    public const string EvidenceVerification = "evidence-verification-v3";
    public const string Uncertainty = "uncertainty-v3";
    public const string Recommendation = "screening-recommendation-v4";

    /// <summary>"cv-extraction-v3" becomes "cv-extraction", so two prompts never share a key.</summary>
    public static string Key(string version)
    {
        var cut = version.LastIndexOf("-v", StringComparison.Ordinal);
        return cut > 0 ? version[..cut] : version;
    }
}

/// <summary>How structured data is written into a prompt: readable, indented JSON.</summary>
internal static class PromptJson
{
    private static readonly JsonSerializerOptions WithNulls = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions WithoutNulls = new(WithNulls)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, WithNulls);

    public static string WriteWithoutNulls<T>(T value) => JsonSerializer.Serialize(value, WithoutNulls);
}
