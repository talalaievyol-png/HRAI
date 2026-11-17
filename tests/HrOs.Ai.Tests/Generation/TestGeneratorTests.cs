using HrOs.Ai.Core;
using HrOs.Ai.Generation;
using HrOs.Ai.Tests.Support;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Tests.Generation;

public class TestGeneratorTests
{
    private static TestGeneratorService ServiceFor(IAiClient ai) => new(ai, Options.Create(new AiOptions()));

    private static string Question(string text, string options = """["A", "B", "C", "D"]""", string index = "1") =>
        $$"""{"text": "{{text}}", "options": {{options}}, "correct_index": {{index}}}""";

    private static ScriptedAi Answering(params string[] questions) =>
        ScriptedAi.Answering($$"""{"questions": [{{string.Join(",", questions)}}]}""");

    [Fact]
    public async Task Well_formed_questions_are_kept_in_order()
    {
        var ai = Answering(Question("What is a ledger?"), Question("What is a journal entry?", index: "3"));

        var draft = await ServiceFor(ai).GenerateAsync(new TestRequest("Accounts Officer"));

        Assert.Equal(["What is a ledger?", "What is a journal entry?"], draft.Questions.Select(q => q.Text));
        Assert.Equal(3, draft.Questions[1].CorrectIndex);
        Assert.Equal(["A", "B", "C", "D"], draft.Questions[0].Options);
        Assert.Empty(draft.Errors);
        Assert.Equal("test-generator-v2", draft.PromptVersion);
    }

    [Theory]
    [InlineData("""["A", "B", "C"]""", "1")]                  // three options
    [InlineData("""["A", "B", "C", "D", "E"]""", "1")]        // five options
    [InlineData("""["A", "a", "C", "D"]""", "1")]             // repeated option, any case
    [InlineData("""["A", "B", "", "D"]""", "1")]              // blank option
    [InlineData("""["A", "B", "C", "D"]""", "4")]             // index past the end
    [InlineData("""["A", "B", "C", "D"]""", "-1")]            // negative index
    [InlineData("""["A", "B", "C", "D"]""", "true")]          // a boolean is not an index
    [InlineData("""["A", "B", "C", "D"]""", "\"1\"")]         // text is not an index
    public async Task A_malformed_question_is_dropped_and_the_drop_is_reported(string options, string index)
    {
        var ai = Answering(Question("Bad one", options, index), Question("Good one"));

        var draft = await ServiceFor(ai).GenerateAsync(new TestRequest("Accounts Officer"));

        Assert.Equal(["Good one"], draft.Questions.Select(q => q.Text));
        Assert.Contains("test_generator: dropped 1 malformed or repeated question(s)", draft.Errors);
    }

    [Fact]
    public async Task A_repeated_question_is_kept_once()
    {
        var ai = Answering(Question("Same question?"), Question("same QUESTION?"));

        var draft = await ServiceFor(ai).GenerateAsync(new TestRequest("Accounts Officer"));

        Assert.Single(draft.Questions);
    }

    [Fact]
    public async Task No_more_than_the_asked_number_are_kept()
    {
        var ai = Answering(Question("One"), Question("Two"), Question("Three"));

        var draft = await ServiceFor(ai).GenerateAsync(new TestRequest("Accounts Officer", Count: 2));

        Assert.Equal(["One", "Two"], draft.Questions.Select(q => q.Text));
    }

    [Fact]
    public async Task When_no_question_is_usable_HR_is_told_to_write_the_test_by_hand()
    {
        var draft = await ServiceFor(Answering()).GenerateAsync(new TestRequest("Accounts Officer"));

        Assert.Empty(draft.Questions);
        Assert.Contains("test_generator: no usable questions, write the test by hand", draft.Errors);
    }

    [Fact]
    public async Task With_the_model_down_there_are_no_questions_and_never_filler()
    {
        var draft = await ServiceFor(ScriptedAi.Down()).GenerateAsync(new TestRequest("Accounts Officer"));

        Assert.Empty(draft.Questions);
        Assert.Equal(["test_generator: LLM unavailable, write the test by hand"], draft.Errors);
    }

    [Fact]
    public async Task The_model_is_given_the_job_and_the_number_of_questions()
    {
        var ai = Answering(Question("q"));

        await ServiceFor(ai).GenerateAsync(new TestRequest("Accounts Officer", "Prepare ledgers", null, 7));

        var user = ai.ChatCalls.Single().Messages[1].Content;
        Assert.Equal(
            "JOB_TITLE:\nAccounts Officer\n\nDESCRIPTION:\nPrepare ledgers\n\nREQUIREMENTS:\n(not provided)\n\nNUMBER_OF_QUESTIONS:\n7",
            user);
        Assert.Equal(3000, ai.ChatCalls.Single().MaxTokens);
    }
}
