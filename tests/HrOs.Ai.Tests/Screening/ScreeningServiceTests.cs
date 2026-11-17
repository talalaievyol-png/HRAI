using HrOs.Ai.Core;
using HrOs.Ai.Screening;
using HrOs.Ai.Tests.Support;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Tests.Screening;

public class ScreeningServiceTests
{
    private static readonly ScreeningInput Input =
        new("Sana Malik\nBS Accounting, University of Sargodha. Accounts Officer 2022-2025.", "Accounts Officer", "Prepare ledgers");

    private static ScreeningService ServiceFor(IAiClient ai, AiOptions options) =>
        new(new ScreeningPipeline(ai, new FixedClock(DateTimeOffset.UnixEpoch), Options.Create(options)),
            Options.Create(options));

    private static RoutedAi Answers(string matchStatus, string uncertainties)
    {
        return new RoutedAi()
            .Answer(RoutedAi.Extraction, """{"name": "Sana Malik"}""")
            .Answer(RoutedAi.Validation, """{"valid": true}""")
            .Answer(RoutedAi.Requirements, """{"mandatory": ["BS Accounting"]}""")
            .Answer(RoutedAi.Matching, $$"""{"requirements": [{"requirement": "Relevance: how closely the candidate's work history (for a fresher, their degree, internships and projects) matches the duties of this role", "status": "{{matchStatus}}", "evidence": "Accounts Officer, Evyol Test Company, 2022-2025"}, {"requirement": "BS Accounting", "status": "{{matchStatus}}", "evidence": "BS Accounting"}]}""")
            .Answer(RoutedAi.Evidence, """{"claims": []}""")
            .Answer(RoutedAi.Uncertainty, $$"""{"uncertainties": {{uncertainties}}}""")
            .Answer(RoutedAi.Recommendation, """{"recommendation": "MATCH", "reason": "fine"}""");
    }

    [Fact]
    public async Task The_report_carries_the_evidence_what_is_missing_and_what_is_uncertain()
    {
        var ai = new RoutedAi()
            .Answer(RoutedAi.Extraction, """{"name": "Sana Malik"}""")
            .Answer(RoutedAi.Validation, """{"valid": true}""")
            .Answer(RoutedAi.Requirements, """{"mandatory": ["BS Accounting"], "preferred": ["SAP"]}""")
            .Answer(RoutedAi.Matching, """
                {"requirements": [
                  {"requirement": "Relevance: how closely the candidate's work history (for a fresher, their degree, internships and projects) matches the duties of this role", "status": "MATCH", "evidence": "Accounts Officer, Evyol Test Company, 2022-2025"},
                  {"requirement": "BS Accounting", "status": "MATCH", "evidence": "BS Accounting"},
                  {"requirement": "SAP", "status": "MISSING", "evidence": null}]}
                """)
            .Answer(RoutedAi.Evidence, """{"claims": []}""")
            .Answer(RoutedAi.Uncertainty, """{"uncertainties": [{"issue": "End date unclear"}]}""")
            .Answer(RoutedAi.Recommendation, """{"recommendation": "PARTIAL", "reason": "mostly"}""");

        var report = await ServiceFor(ai, new AiOptions()).ScreenAsync(Input);

        Assert.Equal(MatchStatus.Partial, report.Recommendation);
        Assert.Equal(3, report.Evidence.Count);
        Assert.Equal("SAP", report.Missing.Single().Requirement);
        Assert.Equal(MatchStatus.Missing, report.Missing.Single().Status);
        var uncertain = report.Uncertain.Single();
        Assert.Equal("End date unclear", uncertain.Requirement);
        Assert.Equal(MatchStatus.Unclear, uncertain.Status);
        Assert.Null(uncertain.Evidence);
        Assert.True(report.RequiresHrReview);
        Assert.Equal("vllm/qwen3:latest", report.Model);
        Assert.False(report.FallbackUsed);
    }

    [Fact]
    public async Task A_slow_model_stops_at_the_time_limit()
    {
        var service = ServiceFor(new HangingAi(), new AiOptions { ScreeningTimeoutSeconds = 1 });

        var error = await Assert.ThrowsAsync<ScreeningTimeoutException>(() => service.ScreenAsync(Input));

        Assert.Contains("1s", error.Message);
    }

    [Fact]
    public async Task Cancelling_is_not_mistaken_for_a_timeout()
    {
        var service = ServiceFor(new HangingAi(), new AiOptions { ScreeningTimeoutSeconds = 60 });
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScreenAsync(Input, cancel.Token));
    }

    [Fact]
    public async Task An_uncertain_result_is_run_again_on_the_second_opinion_model_when_one_is_set()
    {
        var ai = Answers("MATCH", """[{"issue": "dates conflict"}]""");
        var options = new AiOptions { EvaluationModel = "first", FallbackEvaluationModel = "second" };

        var report = await ServiceFor(ai, options).ScreenAsync(Input);

        Assert.True(report.FallbackUsed);
        Assert.Equal("vllm/second", report.FallbackModel);
        Assert.Equal("vllm/second", report.Model);
        Assert.Contains(ai.Calls, call => call.Model == "first");
        Assert.Contains(ai.Calls, call => call.Model == "second");
    }

    [Fact]
    public async Task A_score_of_zero_is_labelled_unclear_which_counts_as_uncertain()
    {
        // Every requirement MISSING scores 0, and 0 carries the UNCLEAR label (the Python rule).
        var ai = Answers("MISSING", "[]");
        var options = new AiOptions { EvaluationModel = "first", FallbackEvaluationModel = "second" };

        var report = await ServiceFor(ai, options).ScreenAsync(Input);

        Assert.True(report.FallbackUsed);
    }

    [Fact]
    public async Task A_confident_result_is_not_run_again()
    {
        var ai = Answers("MATCH", "[]");
        var options = new AiOptions { EvaluationModel = "first", FallbackEvaluationModel = "second" };

        var report = await ServiceFor(ai, options).ScreenAsync(Input);

        Assert.False(report.FallbackUsed);
        Assert.DoesNotContain(ai.Calls, call => call.Model == "second");
    }

    [Fact]
    public async Task Without_a_second_opinion_model_an_uncertain_result_stands()
    {
        var ai = Answers("UNCLEAR", """[{"issue": "x"}]""");

        var report = await ServiceFor(ai, new AiOptions { FallbackEvaluationModel = "  " }).ScreenAsync(Input);

        Assert.False(report.FallbackUsed);
        Assert.Null(report.FallbackModel);
    }
}
