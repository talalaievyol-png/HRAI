using System.Text.RegularExpressions;

namespace HrOs.Ai.Screening;

/// <summary>
/// Checks the code makes around the model, so a prompt mistake cannot cause harm.
/// <list type="bullet">
/// <item>Blind screening: the steps that judge fit never see who the person is (name, contact,
/// family, religion, date of birth and similar), only education, work and skills.</item>
/// <item>Evidence check: a MATCH or PARTIAL must quote text that is really in the CV.</item>
/// <item>Decision words: the model describes evidence, it never decides.</item>
/// </list>
/// The Python side has the same rules in <c>ai/guardrails.py</c>.
/// </summary>
public static partial class BlindCv
{
    public const string Candidate = "[candidate]";
    public const string Contact = "[contact removed]";

    // A line that starts with one of these labels holds a personal detail, not work or study.
    [GeneratedRegex(
        @"^[ \t]*[-*•]?[ \t]*(?:father(?:'s)?[ \t]*name|father|husband(?:'s)?[ \t]*name|spouse|s/o|d/o|w/o|" +
        @"religion|sect|marital[ \t]*status|date[ \t]*of[ \t]*birth|d\.?[ \t]?o\.?[ \t]?b\.?|born|age|nationality|" +
        @"gender|sex|domicile|cnic|nic|passport|blood[ \t]*group|height|weight|photo)\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex PersonalLine();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.]+")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?:https?://|www\.)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"(?<!\w)\+?\d[\d\s().-]{7,}\d(?!\w)")]
    private static partial Regex Phone();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();

    [GeneratedRegex(@"[a-z0-9]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"\b(?:selected|rejected|hired|reject|shortlisted)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DecisionWords();

    /// <summary>The CV text without the personal details the fit check must not use.</summary>
    public static string Redact(string text, string? name = null)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var cleaned = PersonalLine().Replace(text, "");
        cleaned = Email().Replace(cleaned, Contact);
        cleaned = Url().Replace(cleaned, Contact);
        // At least 9 digits: a date range such as "2019 - 2023" is not a phone number.
        cleaned = Phone().Replace(cleaned, m => m.Value.Count(char.IsDigit) >= 9 ? Contact : m.Value);
        foreach (var part in NameParts(name))
            cleaned = Regex.Replace(cleaned, $@"\b{Regex.Escape(part)}\b", Candidate, RegexOptions.IgnoreCase);

        return BlankRuns().Replace(cleaned, "\n\n").Trim();
    }

    private static IEnumerable<string> NameParts(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) yield break;
        var whole = name.Trim();
        // The whole name first, so "Ali Khan" becomes one placeholder, not two.
        if (whole.Length >= 3) yield return whole;
        foreach (var part in whole.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (part.Length >= 3) yield return part;
    }

    /// <summary>True when the quoted evidence really comes from the CV (loose, not exact).</summary>
    public static bool IsGrounded(string? evidence, string cvText)
    {
        if (string.IsNullOrWhiteSpace(evidence)) return false;
        var quoted = Words(evidence);
        if (quoted.Count == 0) return false;

        var cvWords = Words(cvText);
        if (string.Join(' ', cvWords).Contains(string.Join(' ', quoted), StringComparison.Ordinal)) return true;

        var known = cvWords.ToHashSet();
        return (double)quoted.Count(known.Contains) / quoted.Count >= 0.8;
    }

    /// <summary>One step down when the evidence cannot be shown: MATCH to PARTIAL to UNCLEAR.</summary>
    public static MatchStatus Lower(MatchStatus status) => status switch
    {
        MatchStatus.Match => MatchStatus.Partial,
        MatchStatus.Partial => MatchStatus.Unclear,
        _ => status,
    };

    public static bool HasDecisionWords(string? text) => !string.IsNullOrEmpty(text) && DecisionWords().IsMatch(text);

    private static List<string> Words(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();
}
