using System.Text.Json.Nodes;
using HrOs.Ai.Core;

namespace HrOs.Ai.Screening;

/// <summary>What a job asks for, split the way the matching step needs it.</summary>
public sealed record JobRequirements
{
    public IReadOnlyList<string> Mandatory { get; init; } = [];
    public IReadOnlyList<string> Preferred { get; init; } = [];
    public IReadOnlyList<string> Education { get; init; } = [];
    public IReadOnlyList<string> Experience { get; init; } = [];
    public IReadOnlyList<string> TechnicalSkills { get; init; } = [];
    public IReadOnlyList<string> Certifications { get; init; } = [];
    public IReadOnlyList<string> Location { get; init; } = [];
    public decimal? SalaryMin { get; init; }
    public decimal? SalaryMax { get; init; }
    public string? SalaryCurrency { get; init; }

    /// <summary>The single requirement used when a job has no description to read.</summary>
    public static JobRequirements FromTitle(string? title) =>
        string.IsNullOrWhiteSpace(title)
            ? new JobRequirements()
            : new JobRequirements { Mandatory = [$"Relevant skills and qualifications for {title}"] };

    /// <summary>
    /// Reads the model's answer. A structured entry where text was asked is written out as text,
    /// and a requirement repeated across fields is kept once, in the order the fields appear:
    /// duplicates inflate the number of requirements and distort the final score.
    /// </summary>
    public static JobRequirements FromModel(JsonObject json)
    {
        var seen = new HashSet<string>();
        IReadOnlyList<string> Clean(IEnumerable<string> items)
        {
            var kept = new List<string>();
            foreach (var item in items)
            {
                var text = item.Trim();
                if (text.Length > 0 && seen.Add(text.ToLowerInvariant())) kept.Add(text);
            }

            return kept;
        }

        // Fields are cleaned in this order, which decides which copy of a duplicate survives.
        var mandatory = Clean(Texts(json["mandatory"]));
        var preferred = Clean(Texts(json["preferred"]));
        var technical = Clean(Texts(json["technical_skills"]));
        var education = Clean(Texts(json["education"]));
        var experience = Clean(Texts(json["experience"]));
        var certifications = Clean(Texts(json["certifications"]));
        var location = Clean(Texts(json["location"]));

        var salary = json["salary"] as JsonObject;
        return new JobRequirements
        {
            Mandatory = mandatory,
            Preferred = preferred,
            TechnicalSkills = technical,
            Education = education,
            Experience = experience,
            Certifications = certifications,
            Location = location,
            SalaryMin = Number(salary?["min"]),
            SalaryMax = Number(salary?["max"]),
            SalaryCurrency = JsonRead.Text(salary?["currency"]),
        };
    }

    /// <summary>qualification, experience, skills, certifications or other, from the list it came from.</summary>
    public string CategoryOf(string requirement) =>
        Education.Contains(requirement) ? Criterion.Qualification
        : Experience.Contains(requirement) ? Criterion.Experience
        : TechnicalSkills.Contains(requirement) ? Criterion.Skills
        : Certifications.Contains(requirement) ? Criterion.Certifications
        : Criterion.Other;

    /// <summary>
    /// A required degree, years of work or skill counts as mandatory. Certifications do not yet:
    /// their weight is set later.
    /// </summary>
    public bool IsMandatoryItem(string requirement) =>
        Mandatory.Contains(requirement) || Education.Contains(requirement)
        || Experience.Contains(requirement) || TechnicalSkills.Contains(requirement);

    /// <summary>Every requirement, mandatory first, as one list for the matching step.</summary>
    public IReadOnlyList<string> AsFlatList() =>
        new[] { Mandatory, Preferred, TechnicalSkills, Education, Experience, Certifications, Location }
            .SelectMany(list => list)
            .Where(item => item.Length > 0)
            .ToList();

    private static decimal? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<decimal>(out var number) ? number : null;

    private static IEnumerable<string> Texts(JsonNode? node)
    {
        if (node is JsonValue) return JsonRead.TextList(node);
        if (node is not JsonArray array) return [];
        return array.Select(EntryText).OfType<string>().ToList();
    }

    /// <summary>A requirement entry as text; structured entries are written as a readable phrase.</summary>
    private static string? EntryText(JsonNode? node)
    {
        if (node is not JsonObject entry) return JsonRead.Text(node);

        if (entry.ContainsKey("degree"))
        {
            var parts = new List<string?> { JsonRead.Text(entry["degree"]) };
            if (JsonRead.Text(entry["field"]) is { } field) parts.Add($"in {field}");
            if (JsonRead.Text(entry["institution"]) is { } institution) parts.Add($"({institution})");
            return string.Join(' ', parts.Where(part => !string.IsNullOrEmpty(part)));
        }

        if (entry.ContainsKey("years"))
        {
            var text = JsonRead.Text(entry["years"]) ?? "";
            if (!text.EndsWith('+') && !text.EndsWith("years", StringComparison.Ordinal)) text += " years";
            if (JsonRead.Text(entry["type"]) is { } type) text += $" ({type})";
            return text;
        }

        if (entry.ContainsKey("title"))
        {
            var parts = new List<string?> { JsonRead.Text(entry["title"]) };
            if (JsonRead.Text(entry["level"]) is { } level) parts.Add(level);
            return string.Join(' ', parts.Where(part => !string.IsNullOrEmpty(part)));
        }

        return entry.ToJsonString();
    }
}
