using HrOs.Ai.Core;
using HrOs.Ai.Generation;
using HrOs.Ai.Tests.Support;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Tests.Generation;

public class JobAssistantTests
{
    private static JobAssistantService ServiceFor(IAiClient ai) => new(ai, Options.Create(new AiOptions()));

    [Fact]
    public async Task The_models_description_is_returned_as_plain_text()
    {
        var ai = ScriptedAi.Answering("""{"description": "## About the role\n**You will** prepare ledgers.\n- Excel\n1. Tally\n2) SAP"}""");

        var draft = await ServiceFor(ai).DraftAsync(new JobAssistRequest("Accounts Officer", "ledgers"));

        Assert.Equal("About the role\nYou will prepare ledgers.\nExcel\nTally\nSAP", draft.Description);
        Assert.Empty(draft.Errors);
        Assert.Equal("job-assistant-v7", draft.PromptVersion);
    }

    [Fact]
    public async Task With_the_model_down_what_HR_wrote_is_kept()
    {
        var draft = await ServiceFor(ScriptedAi.Down()).DraftAsync(new JobAssistRequest("Accounts Officer", "Prepare ledgers."));

        Assert.Equal("Prepare ledgers.", draft.Description);
        Assert.Equal("job_assistant: LLM failed, used deterministic fallback", draft.Errors.Single());
    }

    [Theory]
    [InlineData("Accounts Officer", null, "Role for Accounts Officer")]
    [InlineData(null, null, "")]
    public async Task With_no_note_the_fallback_uses_the_title_or_nothing(string? title, string? note, string expected)
    {
        var draft = await ServiceFor(ScriptedAi.Down()).DraftAsync(new JobAssistRequest(title, note));

        Assert.Equal(expected, draft.Description);
    }

    [Fact]
    public async Task The_model_is_given_the_title_and_the_note()
    {
        var ai = ScriptedAi.Answering("""{"description": "x"}""");

        await ServiceFor(ai).DraftAsync(new JobAssistRequest("Accounts Officer", "Prepare ledgers."));

        Assert.Equal("JOB_TITLE:\nAccounts Officer\n\nUSER_NOTE / PROMPT:\n\nPrepare ledgers.", ai.ChatCalls.Single().Messages[1].Content);
    }

    [Fact]
    public async Task A_missing_note_is_said_to_be_missing()
    {
        var ai = ScriptedAi.Answering("""{"description": "x"}""");

        await ServiceFor(ai).DraftAsync(new JobAssistRequest(null, null));

        Assert.Equal("USER_NOTE / PROMPT:\n\n(not provided)", ai.ChatCalls.Single().Messages[1].Content);
    }
}
