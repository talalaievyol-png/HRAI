using HrOs.Ai.Core;
using HrOs.Ai.Screening;
using HrOs.Ai.Tests.Support;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Tests.Screening;

public class ScreeningPipelineTests
{
    private const string Cv =
        "Sana Malik\nsana@example.com\nBS Accounting, University of Sargodha, 2018-2022. CGPA 3.4\n" +
        "Accounts Officer, Evyol Test Company, 2022-2025. Prepared monthly ledgers in Excel.";

    private static readonly DateTimeOffset Today = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private static ScreeningPipeline PipelineFor(IAiClient ai, AiOptions? options = null) =>
        new(ai, new FixedClock(Today), Options.Create(options ?? new AiOptions()));

    private static ScreeningInput Input(string? description = "Prepare ledgers and reports") =>
        new(Cv, "Accounts Officer", description);

    private static RoutedAi FullAnswers() => new RoutedAi()
        .Answer(RoutedAi.Extraction, """
            {"name": "Sana Malik", "email": "sana@example.com", "phone": null, "address": null,
             "education": [{"degree": "BS Accounting", "institution": "University of Sargodha"}],
             "experience": [{"company": "Evyol Test Company", "title": "Accounts Officer", "description": "ledgers"}],
             "skills": ["Excel"], "certifications": [], "projects": []}
            """)
        .Answer(RoutedAi.Validation, """{"valid": true, "issues": [], "uncertainties": [], "requires_hr_review": false}""")
        .Answer(RoutedAi.Requirements, """
            {"mandatory": ["BS Accounting", "2 years accounting experience"], "preferred": ["SAP"],
             "education": [], "experience": [], "technical_skills": ["Excel"], "certifications": [],
             "location": ["Multan"], "salary": {"min": null, "max": null, "currency": null}}
            """)
        .Answer(RoutedAi.Matching, """
            {"requirements": [
              {"requirement": "Relevance: how closely the candidate's work history (for a fresher, their degree, internships and projects) matches the duties of this role", "status": "MATCH", "evidence": "Accounts Officer, Evyol Test Company, 2022-2025"},
              {"requirement": "BS Accounting", "status": "MATCH", "evidence": "BS Accounting, University of Sargodha"},
              {"requirement": "2 years accounting experience", "status": "PARTIAL", "evidence": "Accounts Officer 2022-2025"},
              {"requirement": "SAP", "status": "MISSING", "evidence": null},
              {"requirement": "Excel", "status": "MATCH", "evidence": "ledgers in Excel"},
              {"requirement": "Multan", "status": "banana", "evidence": null}],
             "overall_summary": "ok", "uncertainties": [], "requires_hr_review": false}
            """)
        .Answer(RoutedAi.Evidence, """
            {"claims": [{"claim": "BS Accounting", "supported": true, "evidence": "BS Accounting", "reason": "stated"}],
             "unsupported_claims": []}
            """)
        .Answer(RoutedAi.Uncertainty, """
            {"uncertainties": [{"issue": "Employment end date unclear", "impact": "LOW", "requires_hr_review": true}],
             "overall_uncertainty": "LOW"}
            """)
        .Answer(RoutedAi.Recommendation, """
            {"recommendation": "MATCH", "score": 90, "reason": "Good accounting background",
             "matched_requirements": ["BS Accounting"], "missing_requirements": ["SAP"],
             "uncertainties": [], "requires_hr_review": false}
            """);

    [Fact]
    public async Task The_score_comes_from_the_rules_and_the_reasoning_from_the_model()
    {
        var outcome = await PipelineFor(FullAnswers()).RunAsync(Input(), "qwen3:latest");

        // Weights: mandatory 2x. Relevance 200 + BS 200 + experience 100 + SAP 0 + Excel 200 + Multan 25
        // over weight 10 is 72, under the partial-mandatory cap of 80. The model's own 90 is ignored.
        Assert.Equal(72, outcome.Recommendation.Score);
        Assert.Equal(MatchStatus.Match, outcome.Recommendation.Recommendation);
        Assert.Equal("Good accounting background", outcome.Recommendation.Reason);
        Assert.Empty(outcome.Errors);
        Assert.Equal("vllm/qwen3:latest", outcome.Model);
    }

    [Fact]
    public async Task Each_requirement_keeps_its_evidence_and_whether_it_is_mandatory()
    {
        var outcome = await PipelineFor(FullAnswers()).RunAsync(Input(), "qwen3:latest");

        Assert.Equal(6, outcome.Matched.Count);
        Assert.Equal(Criterion.Relevance, outcome.Matched[0].Category);
        var bs = outcome.Matched.Single(m => m.Requirement == "BS Accounting");
        Assert.Equal(MatchStatus.Match, bs.Status);
        Assert.Equal("BS Accounting, University of Sargodha", bs.Evidence);
        Assert.True(bs.IsMandatory);
        Assert.True(outcome.Matched.Single(m => m.Requirement == "Excel").IsMandatory);
        Assert.False(outcome.Matched.Single(m => m.Requirement == "SAP").IsMandatory);
        Assert.Equal(MatchStatus.Unclear, outcome.Matched.Single(m => m.Requirement == "Multan").Status);
    }

    [Fact]
    public async Task An_open_uncertainty_or_an_unclear_requirement_sends_the_case_to_hr()
    {
        var outcome = await PipelineFor(FullAnswers()).RunAsync(Input(), "qwen3:latest");

        Assert.True(outcome.RequiresHrReview);
        Assert.Equal("Employment end date unclear", outcome.Uncertainties.Single().Issue);
        Assert.Equal(Severity.Low, outcome.Uncertainties.Single().Impact);
        Assert.Contains("Multan", outcome.Recommendation.Uncertainties);
    }

    [Fact]
    public async Task A_clean_match_needs_no_review()
    {
        var ai = FullAnswers()
            .Answer(RoutedAi.Requirements, """{"mandatory": ["BS Accounting"]}""")
            .Answer(RoutedAi.Matching, """{"requirements": [{"requirement": "Relevance: how closely the candidate's work history (for a fresher, their degree, internships and projects) matches the duties of this role", "status": "MATCH", "evidence": "Accounts Officer, Evyol Test Company, 2022-2025"}, {"requirement": "BS Accounting", "status": "MATCH", "evidence": "BS Accounting"}]}""")
            .Answer(RoutedAi.Uncertainty, """{"uncertainties": [], "overall_uncertainty": "LOW"}""");

        var outcome = await PipelineFor(ai).RunAsync(Input(), "qwen3:latest");

        Assert.Equal(100, outcome.Recommendation.Score);
        Assert.Equal(MatchStatus.Match, outcome.Recommendation.Recommendation);
        Assert.False(outcome.RequiresHrReview);
        Assert.Empty(outcome.Errors);
    }

    [Fact]
    public async Task Every_prompt_version_is_recorded_under_its_own_name()
    {
        var outcome = await PipelineFor(FullAnswers()).RunAsync(Input(), "qwen3:latest");

        Assert.Equal(7, outcome.PromptVersions.Count);
        Assert.Equal("cv-extraction-v3", outcome.PromptVersions["cv-extraction"]);
        Assert.Equal("cv-validation-v3", outcome.PromptVersions["cv-validation"]);
        Assert.Equal("screening-recommendation-v4", outcome.PromptVersions["screening-recommendation"]);
    }

    [Fact]
    public async Task The_models_are_told_todays_date_and_the_cv_is_read_by_the_lighter_profile_model()
    {
        var ai = FullAnswers();

        await PipelineFor(ai, new AiOptions { ChatModel = "main", ProfileModel = "fast" }).RunAsync(Input(), "main");

        Assert.All(ai.Calls.Where(c => c.Step != RoutedAi.Requirements),
            call => Assert.StartsWith("CURRENT_DATE: Tuesday, 10 November 2026\n", call.User));
        Assert.Equal("fast", ai.Calls.Single(c => c.Step == RoutedAi.Extraction).Model);
        Assert.Equal("main", ai.Calls.Single(c => c.Step == RoutedAi.Matching).Model);
    }

    [Fact]
    public async Task With_the_model_down_every_step_falls_back_and_the_case_goes_to_hr()
    {
        var ai = new RoutedAi()
            .Fail(RoutedAi.Extraction).Fail(RoutedAi.Validation).Fail(RoutedAi.Requirements)
            .Fail(RoutedAi.Matching).Fail(RoutedAi.Evidence).Fail(RoutedAi.Uncertainty).Fail(RoutedAi.Recommendation);

        var outcome = await PipelineFor(ai).RunAsync(Input(), "qwen3:latest");

        Assert.True(outcome.RequiresHrReview);
        Assert.Contains("cv_extraction: LLM failed, used deterministic fallback", outcome.Errors);
        Assert.Contains("job_requirements: LLM failed, used title fallback", outcome.Errors);
        Assert.Contains("requirement_matching: LLM failed, used deterministic fallback", outcome.Errors);
        Assert.Equal("Sana Malik", outcome.Profile.Name);
        Assert.Equal("Relevant skills and qualifications for Accounts Officer", outcome.Requirements.Mandatory.Single());
        Assert.Equal("Deterministic recommendation from requirement matching", outcome.Recommendation.Reason);
        Assert.Empty(outcome.PromptVersions);
    }

    [Fact]
    public async Task A_job_without_a_description_uses_its_title_and_never_asks_the_model_for_requirements()
    {
        var ai = FullAnswers();

        var outcome = await PipelineFor(ai).RunAsync(Input(description: null), "qwen3:latest");

        Assert.Equal(0, ai.CallsFor(RoutedAi.Requirements));
        Assert.Equal("Relevant skills and qualifications for Accounts Officer", outcome.Requirements.Mandatory.Single());
    }

    [Fact]
    public async Task A_recommendation_with_a_score_outside_the_range_falls_back_to_the_rules()
    {
        var ai = FullAnswers().Answer(RoutedAi.Recommendation,
            """{"recommendation": "MATCH", "score": 250, "reason": "great"}""");

        var outcome = await PipelineFor(ai).RunAsync(Input(), "qwen3:latest");

        Assert.Equal(72, outcome.Recommendation.Score);
        Assert.Equal("Deterministic recommendation from requirement matching", outcome.Recommendation.Reason);
        Assert.Contains(outcome.Errors, error => error.StartsWith("recommendation: LLM failed"));
    }

    [Fact]
    public async Task A_recommendation_may_list_uncertainties_as_objects()
    {
        var ai = FullAnswers().Answer(RoutedAi.Recommendation, """
            {"recommendation": "PARTIAL", "score": 50, "reason": "r",
             "uncertainties": [{"issue": "Dates conflict"}, {"requirement": "SAP"}, "plain"]}
            """);

        var outcome = await PipelineFor(ai).RunAsync(Input(), "qwen3:latest");

        Assert.Equal(["Dates conflict", "SAP", "plain"], outcome.Recommendation.Uncertainties);
    }

    [Fact]
    public async Task The_extraction_prompt_never_carries_a_cnic()
    {
        var server = new FakeModelServer();
        for (var i = 0; i < 7; i++) server.ReplyChat("{}");

        // Whatever the answers, every request body that leaves the process must be CNIC-free.
        await PipelineFor(server.Client).RunAsync(
            new ScreeningInput(Cv + "\nCNIC 35202-1234567-1", "Accounts Officer", "Prepare ledgers"), "qwen3:latest");

        Assert.NotEmpty(server.Calls);
        Assert.All(server.Calls, call => Assert.DoesNotContain("1234567-1", call.Body!.ToJsonString()));
    }
}
