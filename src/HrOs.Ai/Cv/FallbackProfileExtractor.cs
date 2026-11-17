using System.Text.RegularExpressions;
using HrOs.Ai.Core;

namespace HrOs.Ai.Cv;

/// <summary>
/// The no-AI path: what plain text rules can still read when the model is down. It finds the
/// name, email, phone and a few skill keywords. It does not guess education or experience;
/// the candidate fills those in on the form.
/// </summary>
public static partial class FallbackProfileExtractor
{
    private static readonly string[] SkillKeywords =
    [
        "python", "java", "javascript", "typescript", "c++", "c#", "sql", "postgresql",
        "mongodb", "redis", "fastapi", "django", "flask", "react", "nextjs", "node",
        "docker", "kubernetes", "aws", "azure", "gcp", "git", "pandas", "numpy",
        "machine learning", "deep learning", "llm", "nlp", "devops", "ci/cd",
        "microservices", "rest api", "graphql", "html", "css", "tailwind",
    ];

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.]+")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\+?\d[\d\s().-]{7,}\d")]
    private static partial Regex PhonePattern();

    public static CandidateProfile Extract(string text)
    {
        var lines = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

        var email = lines
            .Select(line => EmailPattern().Match(line))
            .FirstOrDefault(match => match.Success)?.Value;

        // A CNIC looks like a long phone number; it is never a phone number.
        var phone = lines
            .SelectMany(line => PhonePattern().Matches(line))
            .Select(match => match.Value.Trim())
            .FirstOrDefault(value => !AiGuardrails.ContainsCnic(value));

        var name = lines
            .Take(6)
            .FirstOrDefault(line => (email is null || !line.Contains(email))
                                    && line.Length < 60
                                    && !line.Any(char.IsDigit));

        var lower = text.ToLowerInvariant();
        var skills = SkillKeywords.Where(keyword => lower.Contains(keyword)).ToList();

        return new CandidateProfile { Name = name, Email = email, Phone = phone, Skills = skills };
    }
}
