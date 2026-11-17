using System.Text.Json.Nodes;
using HrOs.Ai.Core;

namespace HrOs.Ai.Cv;

/// <summary>
/// Turns whatever JSON the model produced into a clean <see cref="CandidateProfile"/>.
/// Models drift: a list comes back as a comma string, a number where text was asked,
/// a missing key. Every one of those is repaired here, never passed on.
/// </summary>
public static class ProfileNormalizer
{
    public static CandidateProfile FromModel(JsonObject json) => new()
    {
        Name = JsonRead.Text(json["name"]),
        Email = JsonRead.Text(json["email"]),
        Phone = JsonRead.Text(json["phone"]),
        Address = JsonRead.Text(json["address"]),
        ExpectedSalary = JsonRead.Text(json["expected_salary"]),
        Summary = JsonRead.Text(json["summary"]),
        Skills = JsonRead.TextList(json["skills"]),
        Languages = JsonRead.TextList(json["languages"]),
        Interests = JsonRead.TextList(json["interests"]),
        Links = JsonRead.TextList(json["links"]),
        Education = Entries(json["education"]),
        Experience = Entries(json["experience"]),
        Projects = Entries(json["projects"]),
        Certifications = Entries(json["certifications"]),
        Publications = Entries(json["publications"]),
    };

    private static IReadOnlyList<ProfileEntry> Entries(JsonNode? node)
    {
        if (node is not JsonArray array) return [];
        return array
            .OfType<JsonObject>()
            .Select(item => new ProfileEntry(
                item.Select(pair => KeyValuePair.Create(pair.Key, JsonRead.Text(pair.Value) ?? ""))))
            .Where(entry => entry.Values.Any(value => value.Length > 0))
            .ToList();
    }
}
