using System.Text.Json.Serialization;

namespace HrOs.Ai.Cv;

/// <summary>
/// What the CV says, in the exact JSON shape the careers site form already reads
/// (snake_case keys). Entries are text dictionaries such as
/// <c>{degree, institution, years}</c> so a model that adds a key loses nothing.
/// </summary>
public sealed record CandidateProfile
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("phone")] public string? Phone { get; init; }
    [JsonPropertyName("address")] public string? Address { get; init; }
    [JsonPropertyName("expected_salary")] public string? ExpectedSalary { get; init; }
    [JsonPropertyName("summary")] public string? Summary { get; init; }

    [JsonPropertyName("skills")] public IReadOnlyList<string> Skills { get; init; } = [];
    [JsonPropertyName("languages")] public IReadOnlyList<string> Languages { get; init; } = [];
    [JsonPropertyName("interests")] public IReadOnlyList<string> Interests { get; init; } = [];
    [JsonPropertyName("links")] public IReadOnlyList<string> Links { get; init; } = [];

    /// <summary>degree, institution, years</summary>
    [JsonPropertyName("education")] public IReadOnlyList<ProfileEntry> Education { get; init; } = [];

    /// <summary>position, company, years, description</summary>
    [JsonPropertyName("experience")] public IReadOnlyList<ProfileEntry> Experience { get; init; } = [];

    /// <summary>name, link, description</summary>
    [JsonPropertyName("projects")] public IReadOnlyList<ProfileEntry> Projects { get; init; } = [];

    /// <summary>name, issuer, year</summary>
    [JsonPropertyName("certifications")] public IReadOnlyList<ProfileEntry> Certifications { get; init; } = [];

    /// <summary>title, publisher, year</summary>
    [JsonPropertyName("publications")] public IReadOnlyList<ProfileEntry> Publications { get; init; } = [];
}

/// <summary>One education, job, project, certificate or publication entry.</summary>
public sealed class ProfileEntry : Dictionary<string, string>
{
    public ProfileEntry() { }

    public ProfileEntry(IEnumerable<KeyValuePair<string, string>> values) : base(values) { }

    public string Get(string key) => TryGetValue(key, out var value) ? value : "";
}
