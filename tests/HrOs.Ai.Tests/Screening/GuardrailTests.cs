using HrOs.Ai.Core;
using HrOs.Ai.Generation;
using HrOs.Ai.Prompts;
using HrOs.Ai.Screening;
using HrOs.Ai.Tests.Support;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Tests.Screening;

/// <summary>The checks the code makes around the model: blind screening, evidence, decision words.</summary>
public class GuardrailTests
{
    private const string Cv =
        "Sana Malik\nsana@example.com  +92 300 1234567\nFather's Name: Ahmed Malik\nReligion: Islam\nDate of Birth: 04-05-1999\n" +
        "Marital Status: Single\nBS Accounting, University of Sargodha, 2018 - 2022. CGPA 3.4/4.0\n" +
        "Accounts Officer, Evyol Test Company, 2022 - 2025. Prepared monthly ledgers in Excel.";

    private static readonly DateTimeOffset Today = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    // --- blind text ---------------------------------------------------------------------

    [Fact]
    public void Personal_details_are_removed_and_study_and_work_stay()
    {
        var blind = BlindCv.Redact(Cv, "Sana Malik");

        Assert.DoesNotContain("Sana", blind);
        Assert.DoesNotContain("sana@example.com", blind);
        Assert.DoesNotContain("1234567", blind);
        Assert.DoesNotContain("Ahmed", blind);
        Assert.DoesNotContain("Islam", blind);
        Assert.DoesNotContain("04-05-1999", blind);
        Assert.DoesNotContain("Single", blind);
        Assert.Contains("BS Accounting, University of Sargodha, 2018 - 2022. CGPA 3.4/4.0", blind);
        Assert.Contains("Accounts Officer, Evyol Test Company, 2022 - 2025", blind);
    }

    [Fact]
    public void A_date_range_is_not_mistaken_for_a_phone_number()
    {
        Assert.Contains("2019 - 2023", BlindCv.Redact("Sales Officer, 2019 - 2023"));
    }

    [Fact]
    public void Only_the_name_in_the_profile_is_replaced_and_whole_words_only()
    {
        var blind = BlindCv.Redact("Ali Khan led the Alicante branch. Ali Khan sold.", "Ali Khan");

        Assert.Equal("[candidate] led the Alicante branch. [candidate] sold.", blind);
    }

    // --- evidence -----------------------------------------------------------------------

    [Theory]
    [InlineData("Prepared monthly ledgers in Excel", true)]
    [InlineData("prepared MONTHLY ledgers, in excel", true)]
    [InlineData("Led a team of twelve auditors at a Big Four firm", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Evidence_must_come_from_the_cv(string? evidence, bool expected) =>
        Assert.Equal(expected, BlindCv.IsGrounded(evidence, Cv));

    [Fact]
    public async Task A_positive_result_without_a_real_quote_drops_a_step_and_goes_to_hr()
    {
        var outcome = await RunAsync(Matching("""
            {"requirements": [
              {"requirement": "Audit experience", "status": "MATCH", "evidence": "Led a team of twelve auditors"},
              {"requirement": "Ledgers", "status": "MATCH", "evidence": "Prepared monthly ledgers in Excel"}],
             "overall_summary": "", "uncertainties": [], "requires_hr_review": false}
            """));

        Assert.Equal(MatchStatus.Partial, outcome.Matched.Single(m => m.Requirement == "Audit experience").Status);
        Assert.Equal(MatchStatus.Match, outcome.Matched.Single(m => m.Requirement == "Ledgers").Status);
        Assert.Contains(outcome.Errors, e => e.Contains("Audit experience") && e.Contains("not in the CV"));
        Assert.True(outcome.RequiresHrReview);
    }

    // --- criteria -----------------------------------------------------------------------

    [Fact]
    public void Each_requirement_is_tagged_by_the_list_it_came_from()
    {
        var requirements = new JobRequirements
        {
            Education = ["BS Accounting"],
            Experience = ["2 years in accounts"],
            TechnicalSkills = ["Excel"],
            Certifications = ["ACCA"],
            Mandatory = ["Can travel"],
        };

        Assert.Equal(Criterion.Qualification, requirements.CategoryOf("BS Accounting"));
        Assert.Equal(Criterion.Experience, requirements.CategoryOf("2 years in accounts"));
        Assert.Equal(Criterion.Skills, requirements.CategoryOf("Excel"));
        Assert.Equal(Criterion.Certifications, requirements.CategoryOf("ACCA"));
        Assert.Equal(Criterion.Other, requirements.CategoryOf("Can travel"));
        // A required degree, years of work or skill is mandatory; a certification is not yet.
        Assert.True(requirements.IsMandatoryItem("BS Accounting"));
        Assert.True(requirements.IsMandatoryItem("2 years in accounts"));
        Assert.True(requirements.IsMandatoryItem("Excel"));
        Assert.False(requirements.IsMandatoryItem("ACCA"));
    }

    [Fact]
    public async Task Relevance_is_always_judged_and_asked_first()
    {
        var ai = Matching("""
            {"requirements": [{"requirement": "Ledgers", "status": "MATCH", "evidence": "Prepared monthly ledgers in Excel"}]}
            """);

        var outcome = await RunAsync(ai);

        var sent = ai.Calls.Single(call => call.Step == RoutedAi.Matching).User;
        Assert.True(sent.IndexOf("Relevance:", StringComparison.Ordinal) < sent.IndexOf("- Ledgers", StringComparison.Ordinal));
        // The model left it out, so it is added as UNCLEAR and a person looks.
        var relevance = outcome.Matched.Single(m => m.Category == Criterion.Relevance);
        Assert.Equal(MatchStatus.Unclear, relevance.Status);
        Assert.True(relevance.IsMandatory);
        Assert.True(outcome.RequiresHrReview);
        Assert.Contains(outcome.Errors, e => e.Contains("relevance was not judged"));
    }

    // --- what the model sees ------------------------------------------------------------

    [Fact]
    public async Task The_fit_check_steps_never_see_who_the_candidate_is()
    {
        var ai = Matching("""
            {"requirements": [{"requirement": "Ledgers", "status": "MATCH", "evidence": "Prepared monthly ledgers in Excel"}],
             "overall_summary": "", "uncertainties": [], "requires_hr_review": false}
            """);

        await RunAsync(ai);

        foreach (var step in new[] { RoutedAi.Matching, RoutedAi.Evidence, RoutedAi.Uncertainty, RoutedAi.Recommendation })
        {
            var seen = ai.Calls.Single(call => call.Step == step).User;
            Assert.DoesNotContain("Sana", seen);
            Assert.DoesNotContain("sana@example.com", seen);
            Assert.DoesNotContain("Ahmed", seen);
            Assert.DoesNotContain("Islam", seen);
            Assert.DoesNotContain("04-05-1999", seen);
        }
    }

    [Fact]
    public async Task A_reason_that_makes_a_decision_is_replaced_by_the_plain_one()
    {
        var ai = Matching("""
            {"requirements": [{"requirement": "Ledgers", "status": "MATCH", "evidence": "Prepared monthly ledgers in Excel"}],
             "overall_summary": "", "uncertainties": [], "requires_hr_review": false}
            """, reason: "Strong profile, REJECTED for lack of SAP");

        var outcome = await RunAsync(ai);

        Assert.DoesNotContain("REJECTED", outcome.Recommendation.Reason);
        Assert.Equal("Deterministic recommendation from requirement matching", outcome.Recommendation.Reason);
    }

    // --- email --------------------------------------------------------------------------

    [Theory]
    [InlineData("Your score was 85/100.")]
    [InlineData("You were ranked 3rd among applicants.")]
    [InlineData("We found an 80% match.")]
    [InlineData("Your ID is [CNIC removed].")]
    public async Task An_email_that_shows_a_score_a_rank_or_a_removed_id_is_not_used(string leak)
    {
        var ai = ScriptedAi.Answering($$"""{"subject": "Your application", "body": "Dear Ali,\n{{leak}}"}""");
        var service = new EmailDraftService(ai, Options.Create(new AiOptions()), new FixedClock(Today));

        var draft = await service.DraftAsync(new EmailDraftRequest { CandidateName = "Ali" });

        Assert.DoesNotContain(leak, draft.Body);
        Assert.Contains(draft.Errors, e => e.Contains("fallback"));
    }

    // --- prompts ------------------------------------------------------------------------

    [Theory]
    [InlineData("requirement_matching")]
    [InlineData("screening_recommendation")]
    [InlineData("screening_cv_extraction")]
    [InlineData("evidence_verification")]
    [InlineData("uncertainty")]
    [InlineData("cv_validation")]
    [InlineData("job_requirements")]
    [InlineData("email_draft")]
    [InlineData("test_generator")]
    [InlineData("job_assistant")]
    [InlineData("cv_profile_extraction")]
    public void Every_prompt_ends_with_the_shared_safety_rules(string name)
    {
        var prompt = PromptLibrary.Get(name);

        Assert.Contains("Rules that apply to every task:", prompt);
        Assert.Contains("Never decide to hire, reject, shortlist or rank a person", prompt);
        Assert.EndsWith("the JSON in the schema and nothing else.", prompt);
    }

    [Fact]
    public void The_matching_prompt_says_silence_is_unclear_and_not_missing()
    {
        var prompt = PromptLibrary.Get("requirement_matching");

        Assert.Contains("Silence is not failure", prompt);
        Assert.DoesNotContain("choose MISSING", prompt);
    }

    // --- helpers ------------------------------------------------------------------------

    private static RoutedAi Matching(string matchingJson, string reason = "Fine") => new RoutedAi()
        .Answer(RoutedAi.Extraction, """
            {"name": "Sana Malik", "email": "sana@example.com", "phone": null, "address": null,
             "education": [{"degree": "BS Accounting", "institution": "University of Sargodha", "grade": "3.4/4.0"}],
             "experience": [{"company": "Evyol Test Company", "title": "Accounts Officer", "description": "ledgers"}],
             "skills": ["Excel"], "certifications": [], "projects": []}
            """)
        .Answer(RoutedAi.Validation, """{"valid": true, "issues": [], "uncertainties": [], "requires_hr_review": false}""")
        .Answer(RoutedAi.Requirements, """
            {"mandatory": ["Ledgers"], "preferred": [], "education": [], "experience": [], "technical_skills": [],
             "certifications": [], "location": [], "salary": {"min": null, "max": null, "currency": null}}
            """)
        .Answer(RoutedAi.Matching, matchingJson)
        .Answer(RoutedAi.Evidence, """{"claims": [], "unsupported_claims": []}""")
        .Answer(RoutedAi.Uncertainty, """{"uncertainties": [], "overall_uncertainty": "LOW"}""")
        .Answer(RoutedAi.Recommendation, $$"""
            {"recommendation": "MATCH", "score": 90, "reason": "{{reason}}", "matched_requirements": [],
             "missing_requirements": [], "uncertainties": [], "requires_hr_review": false}
            """);

    private static Task<ScreeningOutcome> RunAsync(IAiClient ai) =>
        new ScreeningPipeline(ai, new FixedClock(Today), Options.Create(new AiOptions()))
            .RunAsync(new ScreeningInput(Cv, "Accounts Officer", "Prepare ledgers"), "qwen3:latest");
}
