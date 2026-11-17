using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HrOs.Ai.Core;
using HrOs.Ai.Cv;

namespace HrOs.Ai.Screening;

public sealed record EducationEntry(
    [property: JsonPropertyName("degree")] string Degree,
    [property: JsonPropertyName("field")] string? Field = null,
    [property: JsonPropertyName("institution")] string? Institution = null,
    [property: JsonPropertyName("start_date")] string? StartDate = null,
    [property: JsonPropertyName("end_date")] string? EndDate = null,
    [property: JsonPropertyName("grade")] string? Grade = null);

public sealed record ExperienceEntry(
    [property: JsonPropertyName("company")] string Company,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("start_date")] string? StartDate = null,
    [property: JsonPropertyName("end_date")] string? EndDate = null,
    [property: JsonPropertyName("description")] string Description = "");

/// <summary>
/// The candidate as the screening prompts see them. It is read from the CV by the screening
/// extraction prompt, which asks for dated entries, and is separate from the form-oriented
/// <see cref="CandidateProfile"/> the careers site uses.
/// </summary>
public sealed record ScreeningProfile
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("email")] public string? Email { get; init; }
    [JsonPropertyName("phone")] public string? Phone { get; init; }
    [JsonPropertyName("address")] public string? Address { get; init; }
    [JsonPropertyName("education")] public IReadOnlyList<EducationEntry> Education { get; init; } = [];
    [JsonPropertyName("experience")] public IReadOnlyList<ExperienceEntry> Experience { get; init; } = [];
    [JsonPropertyName("skills")] public IReadOnlyList<string> Skills { get; init; } = [];
    [JsonPropertyName("certifications")] public IReadOnlyList<string> Certifications { get; init; } = [];
    [JsonPropertyName("projects")] public IReadOnlyList<string> Projects { get; init; } = [];

    /// <summary>The profile without name, email, phone or address, for the steps that judge fit.</summary>
    public ScreeningProfile Blind() => this with { Name = null, Email = null, Phone = null, Address = null };

    private static readonly string[] EducationKeywords =
        ["bachelor", "master", "phd", "bs ", "ms ", "b.s.", "m.s.", "degree", "university", "college"];

    /// <summary>Reads the model's answer. A bare string where an entry was asked is kept as the entry's main text.</summary>
    /// <exception cref="FormatException">An education entry with no degree, or an experience entry with no company.</exception>
    public static ScreeningProfile FromModel(JsonObject json) => new()
    {
        Name = JsonRead.Text(json["name"]),
        Email = JsonRead.Text(json["email"]),
        Phone = JsonRead.Text(json["phone"]),
        Address = JsonRead.Text(json["address"]),
        Education = Items(json["education"], EducationFrom),
        Experience = Items(json["experience"], ExperienceFrom),
        Skills = JsonRead.TextList(json["skills"]),
        Certifications = JsonRead.TextList(json["certifications"]),
        Projects = JsonRead.TextList(json["projects"]),
    };

    /// <summary>What plain text rules can read when the model cannot: contact details, skill keywords, degree words.</summary>
    public static ScreeningProfile Fallback(string cvText)
    {
        var basics = FallbackProfileExtractor.Extract(cvText);
        var lower = cvText.ToLowerInvariant();
        var education = EducationKeywords
            .Where(keyword => lower.Contains(keyword))
            .Take(5)
            .Select(keyword => new EducationEntry(ToTitle(keyword.Trim())))
            .ToList();

        return new ScreeningProfile
        {
            Name = basics.Name,
            Email = basics.Email,
            Phone = basics.Phone,
            Skills = basics.Skills,
            Education = education,
        };
    }

    private static string ToTitle(string text) =>
        string.Join(' ', text.Split(' ').Select(word => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..]));

    private static IReadOnlyList<T> Items<T>(JsonNode? node, Func<JsonNode, T?> read) where T : class =>
        node is JsonArray array ? array.Where(item => item is not null).Select(item => read(item!)).OfType<T>().ToList() : [];

    private static EducationEntry? EducationFrom(JsonNode item)
    {
        if (item is JsonObject obj)
        {
            var degree = JsonRead.Text(obj["degree"])
                         ?? throw new FormatException("An education entry has no degree.");
            return new EducationEntry(degree, JsonRead.Text(obj["field"]), JsonRead.Text(obj["institution"]),
                JsonRead.Text(obj["start_date"]), JsonRead.Text(obj["end_date"]), JsonRead.Text(obj["grade"]));
        }

        var text = JsonRead.Text(item);
        return text is null ? null : new EducationEntry(text);
    }

    private static ExperienceEntry? ExperienceFrom(JsonNode item)
    {
        if (item is JsonObject obj)
        {
            var company = JsonRead.Text(obj["company"])
                          ?? throw new FormatException("An experience entry has no company.");
            return new ExperienceEntry(company, JsonRead.Text(obj["title"]), JsonRead.Text(obj["start_date"]),
                JsonRead.Text(obj["end_date"]), JsonRead.Text(obj["description"]) ?? "");
        }

        var text = JsonRead.Text(item);
        return text is null ? null : new ExperienceEntry(text);
    }
}
