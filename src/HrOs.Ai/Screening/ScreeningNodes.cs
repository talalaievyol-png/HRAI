using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HrOs.Ai.Core;

namespace HrOs.Ai.Screening;

// The seven screening steps. Each asks the model and, when the model is down or answers badly,
// falls back to a plain rule and notes it in the errors, which sends the case to HR.

internal static class ExtractionNode
{
    public static async Task RunAsync(NodeLlm llm, ScreeningState state, CancellationToken ct)
    {
        try
        {
            var json = await llm.JsonAsync(
                state, "screening_cv_extraction",
                $"{llm.DateHeader()}CV_TEXT:\n{NodeLlm.Cut(state.CvText, 8000)}",
                PromptVersions.CvExtraction, state.ProfileModel ?? state.Model, maxTokens: 3000, ct);
            state.Profile = ScreeningProfile.FromModel(json);
        }
        catch (Exception e) when (e is NodeException or FormatException)
        {
            state.AddError("cv_extraction: LLM failed, used deterministic fallback");
            state.Profile = ScreeningProfile.Fallback(state.CvText);
        }
    }
}

internal static class ValidationNode
{
    public static async Task RunAsync(NodeLlm llm, ScreeningState state, CancellationToken ct)
    {
        if (state.Profile is null)
        {
            state.AddError("cv_validation: missing candidate profile");
            state.Validation = Fallback(state);
            return;
        }

        try
        {
            var json = await llm.JsonAsync(
                state, "cv_validation",
                $"{llm.DateHeader()}CV_TEXT:\n{NodeLlm.Cut(state.CvText, 6000)}\n\n" +
                $"EXTRACTED_PROFILE:\n{PromptJson.WriteWithoutNulls(state.Profile)}",
                PromptVersions.CvValidation, state.Model, maxTokens: null, ct);
            state.Validation = Parse(json);
        }
        catch (Exception e) when (e is NodeException or FormatException)
        {
            state.AddError("cv_validation: LLM failed, used deterministic fallback");
            state.Validation = Fallback(state);
        }
    }

    internal static CvValidationResult Parse(JsonObject json)
    {
        var issues = new List<ValidationIssue>();
        if (json["issues"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                var type = JsonRead.Text(item["type"]) ?? "ISSUE";
                issues.Add(new ValidationIssue(type, JsonRead.Text(item["description"]) ?? type,
                    ScreeningEnums.ParseSeverity(JsonRead.Text(item["severity"]))));
            }
        }

        return new CvValidationResult(
            JsonRead.Bool(json["valid"], true),
            issues,
            JsonRead.TextList(json["uncertainties"]),
            JsonRead.Bool(json["requires_hr_review"], false));
    }

    internal static CvValidationResult Fallback(ScreeningState state)
    {
        var profile = state.Profile;
        var issues = new List<ValidationIssue>();
        if (state.CvText.Length < 100)
            issues.Add(new("INSUFFICIENT_CONTENT", "CV appears to contain too little text to be meaningful", Severity.High));
        if (string.IsNullOrWhiteSpace(profile?.Email))
            issues.Add(new("MISSING_EMAIL", "No email address found in CV", Severity.Medium));
        if (string.IsNullOrWhiteSpace(profile?.Name))
            issues.Add(new("MISSING_NAME", "Could not extract a candidate name", Severity.Medium));
        if (profile is null || profile.Skills.Count == 0)
            issues.Add(new("MISSING_SKILLS", "No skills detected in CV", Severity.Medium));
        if (profile is null || (profile.Education.Count == 0 && profile.Experience.Count == 0))
            issues.Add(new("MISSING_EDUCATION_EXPERIENCE", "No education or experience information found", Severity.Medium));

        return new CvValidationResult(issues.Count == 0, issues, [], issues.Count > 0);
    }
}

internal static class RequirementsNode
{
    public static async Task RunAsync(NodeLlm llm, ScreeningState state, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state.JobDescription))
        {
            state.Requirements = JobRequirements.FromTitle(state.JobTitle);
            return;
        }

        try
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(state.JobTitle)) parts.Add($"JOB_TITLE:\n{state.JobTitle}");
            parts.Add($"JOB_DESCRIPTION:\n{state.JobDescription}");
            var json = await llm.JsonAsync(
                state, "job_requirements", string.Join("\n\n", parts),
                PromptVersions.JobRequirements, state.Model, maxTokens: null, ct);
            state.Requirements = JobRequirements.FromModel(json);
        }
        catch (Exception e) when (e is NodeException or FormatException)
        {
            state.AddError("job_requirements: LLM failed, used title fallback");
            state.Requirements = JobRequirements.FromTitle(state.JobTitle);
        }
    }
}

internal static partial class MatchingNode
{
    private static readonly HashSet<string> Stopwords =
    [
        "a", "an", "the", "of", "and", "or", "for", "in", "on", "with",
        "to", "at", "by", "years", "year", "experience", "experience.",
        "using", "knowledge", "plus", "minimum", "strong", "good",
    ];

    [GeneratedRegex(@"[a-z0-9+#.]+")]
    private static partial Regex Tokens();

    public static async Task RunAsync(NodeLlm llm, ScreeningState state, CancellationToken ct)
    {
        if (state.Requirements is null)
        {
            state.AddError("requirement_matching: no job requirements available");
            state.Matched = [];
            return;
        }

        var requirements = new List<string> { Criterion.RelevanceRequirement };
        requirements.AddRange(state.Requirements.AsFlatList());
        if (requirements.Count == 1)
            requirements.Add($"Relevant skills and qualifications for {state.JobTitle ?? "the role"}");

        try
        {
            var lines = string.Join("\n", requirements.Select(requirement => $"- {requirement}"));
            var json = await llm.JsonAsync(
                state, "requirement_matching",
                $"{llm.DateHeader()}JOB_TITLE:\n{state.JobTitle ?? "N/A"}\n\n" +
                $"REQUIREMENTS:\n{lines}\n\n" +
                $"CANDIDATE_CV:\n{NodeLlm.Cut(state.BlindCvText, 6000)}",
                PromptVersions.RequirementMatching, state.Model, maxTokens: null, ct);

            if (json["requirements"] is not JsonArray { Count: > 0 } items)
                throw new NodeException("requirement matching returned no items");

            var matched = new List<MatchedRequirement>();
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is not JsonObject item) continue;
                var requirement = JsonRead.Text(item["requirement"]) ?? requirements.ElementAtOrDefault(i);
                if (string.IsNullOrEmpty(requirement)) continue;

                var status = ScreeningEnums.ParseMatchStatus(JsonRead.Text(item["status"])) ?? MatchStatus.Unclear;
                var evidence = JsonRead.Text(item["evidence"]);

                // A positive result must quote the CV. If it cannot, it drops a step and HR looks.
                if (status is MatchStatus.Match or MatchStatus.Partial && !BlindCv.IsGrounded(evidence, state.CvText))
                {
                    status = BlindCv.Lower(status);
                    state.AddError($"requirement_matching: evidence for '{Short(requirement, 80)}' is not in the CV; lowered to {status.Wire()}");
                }

                matched.Add(new MatchedRequirement(requirement, status, evidence, IsMandatory(requirement, state), CategoryOf(requirement, state)));
            }

            if (matched.Count == 0) throw new NodeException("requirement matching returned no usable items");

            // Relevance is always judged. If the model left it out, a person has to look.
            if (!matched.Any(m => Criterion.IsRelevance(m.Requirement)))
            {
                state.AddError("requirement_matching: relevance was not judged; marked UNCLEAR");
                matched.Insert(0, new MatchedRequirement(Criterion.RelevanceRequirement, MatchStatus.Unclear, null, true, Criterion.Relevance));
            }

            state.Matched = matched;
        }
        catch (NodeException)
        {
            state.AddError("requirement_matching: LLM failed, used deterministic fallback");
            state.Matched = requirements
                .Select(requirement => Deterministic(requirement, state.BlindCvText) with
                {
                    IsMandatory = IsMandatory(requirement, state),
                    Category = CategoryOf(requirement, state),
                })
                .ToList();
        }
    }

    private static string Short(string text, int max) => text.Length <= max ? text : text[..max];

    private static bool IsMandatory(string requirement, ScreeningState state) =>
        Criterion.IsRelevance(requirement)
        || (state.Requirements is { } requirements && requirements.IsMandatoryItem(requirement));

    private static string CategoryOf(string requirement, ScreeningState state) =>
        Criterion.IsRelevance(requirement) ? Criterion.Relevance
        : state.Requirements?.CategoryOf(requirement) ?? Criterion.Other;

    /// <summary>
    /// Word overlap between the requirement and the CV: all words present is a match, at least
    /// half is partial. It is crude, and every use of it is flagged for HR.
    /// </summary>
    internal static MatchedRequirement Deterministic(string requirement, string cvText)
    {
        var requirementWords = Words(requirement).Where(word => !Stopwords.Contains(word)).ToHashSet();
        var cvWords = Words(cvText).ToHashSet();
        if (requirementWords.Count == 0) return new MatchedRequirement(requirement, MatchStatus.Unclear, null, false);

        var hits = requirementWords.Where(cvWords.Contains).ToHashSet();
        var ratio = (double)hits.Count / requirementWords.Count;
        var status = ratio >= 1.0 ? MatchStatus.Match : ratio >= 0.5 ? MatchStatus.Partial : MatchStatus.Missing;

        string? evidence = null;
        foreach (var sentence in cvText.Split('.', '\n'))
        {
            var lower = sentence.ToLowerInvariant();
            if (!hits.Any(hit => lower.Contains(hit))) continue;
            evidence = sentence.Trim();
            if (evidence.Length > 20) break;
        }

        if (evidence is { Length: > 300 }) evidence = evidence[..300];
        return new MatchedRequirement(requirement, status, evidence, false);
    }

    private static IEnumerable<string> Words(string text) =>
        Tokens().Matches(text.ToLowerInvariant()).Select(match => match.Value);
}

internal static class EvidenceNode
{
    public static async Task RunAsync(NodeLlm llm, ScreeningState state, CancellationToken ct)
    {
        var claims = state.Matched
            .Where(m => m.Status is MatchStatus.Match or MatchStatus.Partial && !string.IsNullOrEmpty(m.Evidence))
            .Select(m => $"{m.Requirement} (labelled {m.Status.Wire()}) - evidence: {m.Evidence}")
            .ToList();

        if (claims.Count == 0)
        {
            state.Evidence = [new EvidenceItem("No positive match claims", true, null, "None claimed")];
            return;
        }

        try
        {
            var json = await llm.JsonAsync(
                state, "evidence_verification",
                $"{llm.DateHeader()}CLAIMS:\n{string.Join("\n", claims.Select(claim => $"- {claim}"))}\n\n" +
                $"CANDIDATE_CV:\n{NodeLlm.Cut(state.BlindCvText, 6000)}",
                PromptVersions.EvidenceVerification, state.Model, maxTokens: null, ct);

            if (json["claims"] is not JsonArray array) throw new NodeException("evidence verification returned no claims");
            state.Evidence = array.OfType<JsonObject>().Select(Item).ToList();
        }
        catch (Exception e) when (e is NodeException or FormatException)
        {
            state.AddError("evidence_verification: LLM failed, used fallback");
            state.Evidence = Fallback(state);
        }
    }

    private static EvidenceItem Item(JsonObject claim) => new(
        JsonRead.Text(claim["claim"]) ?? throw new FormatException("A claim has no text."),
        JsonRead.Bool(claim["supported"], true),
        JsonRead.Text(claim["evidence"]),
        JsonRead.Text(claim["reason"]) ?? "");

    /// <summary>Without the model, a claim counts as supported only if the CV gave evidence for it.</summary>
    private static IReadOnlyList<EvidenceItem> Fallback(ScreeningState state) =>
        state.Matched
            .Where(m => m.Status is MatchStatus.Match or MatchStatus.Partial)
            .Select(m => new EvidenceItem(
                m.Requirement,
                !string.IsNullOrEmpty(m.Evidence),
                m.Evidence,
                !string.IsNullOrEmpty(m.Evidence) ? "Evidence provided" : "Keyword present but no supporting evidence found"))
            .ToList();
}

internal static class UncertaintyNode
{
    public static async Task RunAsync(NodeLlm llm, ScreeningState state, CancellationToken ct)
    {
        try
        {
            var json = await llm.JsonAsync(
                state, "uncertainty",
                $"{llm.DateHeader()}CANDIDATE_CV:\n{NodeLlm.Cut(state.BlindCvText, 5000)}\n\n" +
                $"CANDIDATE_PROFILE:\n{PromptJson.WriteWithoutNulls(state.Profile?.Blind())}\n\n" +
                $"MATCHING_RESULTS:\n{PromptJson.Write(state.Matched)}",
                PromptVersions.Uncertainty, state.Model, maxTokens: null, ct);

            var items = new List<UncertaintyItem>();
            if (json["uncertainties"] is JsonArray array)
            {
                foreach (var item in array.OfType<JsonObject>())
                {
                    var issue = JsonRead.Text(item["issue"]) ?? throw new FormatException("An uncertainty has no issue.");
                    items.Add(new UncertaintyItem(issue, ScreeningEnums.ParseSeverity(JsonRead.Text(item["impact"])),
                        JsonRead.Bool(item["requires_hr_review"], true)));
                }
            }

            state.Uncertainties = items;
        }
        catch (Exception e) when (e is NodeException or FormatException)
        {
            state.AddError("uncertainty: LLM failed, used fallback");
            state.Uncertainties = Fallback(state);
        }
    }

    private static IReadOnlyList<UncertaintyItem> Fallback(ScreeningState state)
    {
        var items = state.Matched
            .Where(m => m.Status == MatchStatus.Unclear)
            .Select(m => new UncertaintyItem($"Cannot reliably determine: {m.Requirement}", Severity.Medium, true))
            .ToList();
        if (state.Validation is { } validation)
            items.AddRange(validation.Uncertainties.Select(u => new UncertaintyItem(u, Severity.Medium, true)));
        return items;
    }
}

internal static class RecommendationNode
{
    private const double MandatoryWeight = 2.0;

    public static async Task RunAsync(NodeLlm llm, ScreeningState state, CancellationToken ct)
    {
        ScreeningRecommendation result;
        try
        {
            var json = await llm.JsonAsync(
                state, "screening_recommendation",
                $"{llm.DateHeader()}MATCHING_RESULTS:\n{PromptJson.Write(state.Matched)}\n\n" +
                $"EVIDENCE_VERIFICATION:\n{PromptJson.Write(state.Evidence)}\n\n" +
                $"UNCERTAINTIES:\n{PromptJson.Write(state.Uncertainties)}\n\n" +
                $"CANDIDATE_CV (for reference):\n{NodeLlm.Cut(state.BlindCvText, 3000)}",
                PromptVersions.Recommendation, state.Model, maxTokens: null, ct);

            var model = Parse(json);

            // The score and label come from the rules over the matched evidence, so the number is
            // stable across models. The model keeps its written reasoning and its flags.
            var rules = Deterministic(state);
            result = new ScreeningRecommendation(
                rules.Recommendation,
                rules.Score,
                // The model describes evidence; a reason that decides is replaced by the plain one.
                string.IsNullOrEmpty(model.Reason) || BlindCv.HasDecisionWords(model.Reason) ? rules.Reason : model.Reason,
                model.MatchedRequirements.Count > 0 ? model.MatchedRequirements : rules.MatchedRequirements,
                model.MissingRequirements.Count > 0 ? model.MissingRequirements : rules.MissingRequirements,
                model.Uncertainties.Count > 0 ? model.Uncertainties : rules.Uncertainties,
                model.RequiresHrReview || rules.RequiresHrReview || model.Uncertainties.Count > 0);
        }
        catch (Exception e) when (e is NodeException or FormatException)
        {
            state.AddError($"recommendation: LLM failed, used deterministic fallback ({e.GetType().Name}: {Short(e.Message)})");
            result = Deterministic(state);
        }

        state.Recommendation = result;
        state.RequiresHrReview =
            result.RequiresHrReview
            || state.Validation?.RequiresHrReview == true
            || state.Uncertainties.Count > 0
            || state.Errors.Count > 0;
    }

    private static string Short(string message) => message.Length <= 120 ? message : message[..120];

    private static int ItemScore(MatchStatus status) => status switch
    {
        MatchStatus.Match => 100,
        MatchStatus.Partial => 50,
        MatchStatus.Unclear => 25,
        _ => 0,
    };

    /// <summary>
    /// Mandatory-weighted average, capped when a core requirement is unresolved: any mandatory
    /// requirement MISSING caps the score at 35, UNCLEAR at 55, PARTIAL at 80, so a candidate who
    /// fails the core can never look like a strong fit.
    /// </summary>
    internal static int ComputeScore(IReadOnlyList<MatchedRequirement> matched)
    {
        if (matched.Count == 0) return 0;

        double totalWeight = 0, total = 0;
        foreach (var m in matched)
        {
            var weight = m.IsMandatory ? MandatoryWeight : 1.0;
            totalWeight += weight;
            total += ItemScore(m.Status) * weight;
        }

        var score = total / totalWeight;
        int? cap = null;
        foreach (var m in matched.Where(m => m.IsMandatory))
        {
            cap = m.Status switch
            {
                MatchStatus.Missing => Math.Min(cap ?? 35, 35),
                MatchStatus.Unclear => Math.Min(cap ?? 55, 55),
                MatchStatus.Partial => Math.Min(cap ?? 80, 80),
                _ => cap,
            };
        }

        if (cap is { } limit) score = Math.Min(score, limit);
        return (int)score;
    }

    internal static ScreeningRecommendation Deterministic(ScreeningState state)
    {
        var matched = state.Matched;
        var score = ComputeScore(matched);
        var recommendation = score >= 70 ? MatchStatus.Match
            : score >= 40 ? MatchStatus.Partial
            : score > 0 ? MatchStatus.Missing
            : MatchStatus.Unclear;

        // A missing mandatory requirement is never overridden by overall strength.
        if (recommendation == MatchStatus.Match && matched.Any(m => m.IsMandatory && m.Status == MatchStatus.Missing))
            recommendation = MatchStatus.Partial;

        var unclear = matched.Where(m => m.Status == MatchStatus.Unclear).Select(m => m.Requirement).ToList();
        return new ScreeningRecommendation(
            recommendation,
            score,
            "Deterministic recommendation from requirement matching",
            matched.Where(m => m.Status == MatchStatus.Match).Select(m => m.Requirement).ToList(),
            matched.Where(m => m.Status == MatchStatus.Missing).Select(m => m.Requirement).ToList(),
            unclear,
            state.RequiresHrReview || unclear.Count > 0);
    }

    /// <exception cref="FormatException">No valid recommendation label, or a score outside 0 to 100.</exception>
    internal static ScreeningRecommendation Parse(JsonObject json)
    {
        var label = ScreeningEnums.ParseMatchStatus(JsonRead.Text(json["recommendation"]))
                    ?? throw new FormatException("The recommendation is not MATCH, PARTIAL, MISSING or UNCLEAR.");

        var score = 0;
        if (json["score"] is not null)
        {
            score = JsonRead.WholeNumber(json["score"]) ?? throw new FormatException("The score is not a whole number.");
            if (score is < 0 or > 100) throw new FormatException("The score is outside 0 to 100.");
        }

        return new ScreeningRecommendation(
            label, score, JsonRead.Text(json["reason"]) ?? "",
            Strings(json["matched_requirements"]), Strings(json["missing_requirements"]),
            Strings(json["uncertainties"]), JsonRead.Bool(json["requires_hr_review"], false));
    }

    /// <summary>A list of text, where an entry may also be an object that names its text.</summary>
    private static IReadOnlyList<string> Strings(JsonNode? node)
    {
        if (node is not JsonArray array) return [];
        var items = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonObject obj)
            {
                var text = new[] { "issue", "requirement", "uncertainty" }
                    .Select(key => obj[key] is JsonValue ? JsonRead.Text(obj[key]) : null)
                    .FirstOrDefault(value => value is not null);
                if (text is not null) items.Add(text);
            }
            else if (JsonRead.Text(item) is { } plain)
            {
                items.Add(plain);
            }
        }

        return items;
    }
}
