using HrOs.Ai.Core;
using HrOs.Ai.Generation;
using HrOs.Ai.Tests.Support;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Tests.Generation;

public class EmailDraftTests
{
    private static readonly DateTimeOffset Today = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private static EmailDraftService ServiceFor(IAiClient ai, AiOptions? options = null) =>
        new(ai, Options.Create(options ?? new AiOptions()), new FixedClock(Today));

    [Fact]
    public async Task The_models_draft_is_returned_for_HR_to_edit()
    {
        var ai = ScriptedAi.Answering("""{"subject": "Your application", "body": "Dear Ali,\n\nThanks for applying."}""");

        var draft = await ServiceFor(ai).DraftAsync(new EmailDraftRequest { CandidateName = "Ali" });

        Assert.Equal("Your application", draft.Subject);
        Assert.Equal("Dear Ali,\n\nThanks for applying.", draft.Body);
        Assert.Empty(draft.Errors);
        Assert.Equal("vllm/qwen3:1.7b", draft.Model);
        Assert.Equal("qwen3:1.7b", ai.ChatCalls.Single().Model);
    }

    [Fact]
    public async Task A_placeholder_the_model_echoed_becomes_the_real_name()
    {
        var ai = ScriptedAi.Answering("""{"subject": "Hello", "body": "Dear Ali,\n\nBest regards,\n{HR_NAME}\n[COMPANY_NAME]"}""");

        var draft = await ServiceFor(ai).DraftAsync(
            new EmailDraftRequest { HrName = "Alex Morgan", CompanyName = "Evyol Group" });

        Assert.Equal("Dear Ali,\n\nBest regards,\nAlex Morgan\nEvyol Group", draft.Body);
    }

    [Fact]
    public async Task Commas_at_the_end_of_a_line_are_left_alone()
    {
        // The Python version stripped them, turning "Dear Ali," into "Dear Ali". That is not kept.
        var ai = ScriptedAi.Answering("""{"subject": "Hi,", "body": "Dear Ali,\nThanks,\nBest regards,\nAlex"}""");

        var draft = await ServiceFor(ai).DraftAsync(new EmailDraftRequest { HrName = "Alex" });

        Assert.Equal("Hi,", draft.Subject);
        Assert.Equal("Dear Ali,\nThanks,\nBest regards,\nAlex", draft.Body);
    }

    [Fact]
    public async Task A_name_that_is_not_set_disappears_instead_of_leaving_a_placeholder()
    {
        var ai = ScriptedAi.Answering("""{"subject": "Hello", "body": "Dear Ali,\n\nWe write on behalf of {COMPANY_NAME}.\n\nBest regards,\n{HR_NAME}"}""");

        var draft = await ServiceFor(ai).DraftAsync(new EmailDraftRequest());

        Assert.Equal("Dear Ali,\n\nWe write\n\nBest regards", draft.Body);
        Assert.DoesNotContain("{", draft.Body);
    }

    [Fact]
    public async Task The_date_helper_line_never_reaches_the_candidate()
    {
        var ai = ScriptedAi.Answering("""{"subject": "Hello", "body": "CURRENT_DATE: Tuesday, 10 November 2026\nDear Ali,\nThanks."}""");

        var draft = await ServiceFor(ai).DraftAsync(new EmailDraftRequest());

        Assert.DoesNotContain("CURRENT_DATE", draft.Body);
        Assert.StartsWith("Dear Ali", draft.Body);
    }

    [Theory]
    [InlineData("""{"subject": "Only a subject"}""")]
    [InlineData("""{"subject": 5, "body": "x"}""")]
    public async Task A_draft_missing_its_subject_or_body_falls_back_to_the_template(string answer)
    {
        var draft = await ServiceFor(ScriptedAi.Answering(answer)).DraftAsync(
            new EmailDraftRequest { EmailType = "SELECTED", CandidateName = "Ali", JobTitle = "Accounts Officer" });

        Assert.Equal("email: LLM failed, used deterministic fallback", draft.Errors.Single());
        Assert.StartsWith("Dear Ali,\n\nCongratulations!", draft.Body);
    }

    [Fact]
    public async Task A_rejection_template_carries_the_reason_and_the_real_sign_off()
    {
        var draft = await ServiceFor(ScriptedAi.Down()).DraftAsync(new EmailDraftRequest
        {
            EmailType = "REJECTED", CandidateName = "Ali", JobTitle = "Accounts Officer",
            Reason = "The role needs a BS Accounting degree.", HrName = "Alex Morgan", CompanyName = "Evyol Group",
        });

        Assert.Equal("Application Update on Accounts Officer", draft.Subject);
        Assert.Contains("we will not be moving forward with your application.", draft.Body);
        Assert.Contains("The role needs a BS Accounting degree.", draft.Body);
        Assert.EndsWith("Best regards,\nAlex Morgan\nEvyol Group", draft.Body);
    }

    [Fact]
    public void Each_email_type_has_its_own_fallback_and_unknown_types_get_the_general_one()
    {
        Assert.Equal("Re: your query", EmailDraftService.Fallback(new EmailDraftRequest { EmailType = "REPLY" }).Subject);
        Assert.Equal("Update regarding your application for the position",
            EmailDraftService.Fallback(new EmailDraftRequest { EmailType = "INTERVIEW" }).Subject);

        var general = EmailDraftService.Fallback(new EmailDraftRequest { HrNotes = "Please bring your original degree." });
        Assert.StartsWith("Dear there,", general.Body);
        Assert.Contains("Please bring your original degree.", general.Body);
        Assert.EndsWith("Best regards,\nHuman Resources", general.Body);
    }

    [Fact]
    public async Task The_request_to_the_model_is_dated_and_carries_the_reason_the_context_and_the_notes()
    {
        var ai = ScriptedAi.Answering("""{"subject": "s", "body": "b"}""");

        await ServiceFor(ai).DraftAsync(new EmailDraftRequest
        {
            EmailType = "REJECTED", CandidateName = "Ali", JobTitle = "Accounts Officer", Tone = "warm",
            Reason = "Needs a BS Accounting degree.",
            Context = new Dictionary<string, object?>
            {
                ["missing"] = new[] { "BS Accounting", "SAP" }, ["rank"] = null, ["empty"] = "", ["total"] = 12,
            },
            HrNotes = "Keep it short.",
        });

        var user = ai.ChatCalls.Single().Messages[1].Content;
        Assert.Equal(
            "CURRENT_DATE: Tuesday, 10 November 2026\n\nEMAIL_TYPE: REJECTED\n\nCANDIDATE_NAME: Ali\n\n" +
            "JOB_TITLE: Accounts Officer\n\nTONE: warm\n\n" +
            "CONTEXT:\nREASON: Needs a BS Accounting degree.\nMISSING: BS Accounting, SAP\nTOTAL: 12\n\n" +
            "HR_NOTES / INSTRUCTIONS:\n\nKeep it short.",
            user);
    }

    [Fact]
    public async Task A_request_with_no_notes_asks_for_a_standard_email()
    {
        var ai = ScriptedAi.Answering("""{"subject": "s", "body": "b"}""");

        await ServiceFor(ai).DraftAsync(new EmailDraftRequest());

        var user = ai.ChatCalls.Single().Messages[1].Content;
        Assert.Contains("EMAIL_TYPE: general", user);
        Assert.EndsWith("(no additional instructions — write a standard email)", user);
    }
}
