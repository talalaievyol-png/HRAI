namespace HrOs.Ai.Cv;

public sealed record CvValidation(bool Valid, bool RequiresReview, IReadOnlyList<string> Issues);

/// <summary>Plain checks on an extracted CV. A problem asks HR to look; it never rejects anyone.</summary>
public static class CvValidator
{
    public static CvValidation Validate(string text, CandidateProfile profile)
    {
        var issues = new List<string>();
        if (text.Length < 100) issues.Add("CV appears to contain too little text to be meaningful");
        if (string.IsNullOrWhiteSpace(profile.Email)) issues.Add("No email address found in CV");
        if (string.IsNullOrWhiteSpace(profile.Name)) issues.Add("Could not extract a candidate name");
        if (profile.Skills.Count == 0) issues.Add("No skills detected in CV");
        if (profile.Education.Count == 0 && profile.Experience.Count == 0)
            issues.Add("No education or experience information found");

        return new CvValidation(issues.Count == 0, issues.Count > 0, issues);
    }
}
