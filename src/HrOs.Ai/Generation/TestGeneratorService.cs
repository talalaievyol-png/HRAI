using System.Text.Json.Nodes;
using HrOs.Ai.Core;
using HrOs.Ai.Prompts;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Generation;

public sealed record TestRequest(string JobTitle, string? Description = null, string? Requirements = null, int Count = 10);

public sealed record TestQuestionDraft(string Text, IReadOnlyList<string> Options, int CorrectIndex);

/// <summary>
/// A draft test. It is saved unapproved: HR must approve or edit it before any candidate can sit it.
/// When the model could not help, <see cref="Questions"/> is empty and <see cref="Errors"/> says so.
/// </summary>
public sealed record TestDraft(
    IReadOnlyList<TestQuestionDraft> Questions,
    IReadOnlyList<string> Errors,
    string Model,
    string PromptVersion);

/// <summary>
/// Asks the model for multiple-choice questions and keeps only well-formed ones: four distinct
/// options and one valid correct answer. Without the model it returns no questions and says so, so
/// HR writes the test by hand. It never invents filler questions.
/// </summary>
public sealed class TestGeneratorService
{
    public const string PromptVersion = "test-generator-v2";
    public const int OptionsPerQuestion = 4;

    private readonly IAiClient _ai;
    private readonly AiOptions _options;

    public TestGeneratorService(IAiClient ai, IOptions<AiOptions> options)
    {
        _ai = ai;
        _options = options.Value;
    }

    public async Task<TestDraft> GenerateAsync(TestRequest request, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        IReadOnlyList<TestQuestionDraft> questions = [];
        try
        {
            var json = await _ai.ChatJsonAsync(
                [ChatMessage.System(PromptLibrary.Get("test_generator")), ChatMessage.User(UserMessage(request))],
                model: _options.ChatModel,
                maxTokens: 3000,
                cancellationToken: cancellationToken);

            var asked = json["questions"] is JsonArray raw ? raw.Count : 0;
            questions = CleanQuestions(json["questions"], request.Count);
            var dropped = asked - questions.Count;
            if (dropped > 0) errors.Add($"test_generator: dropped {dropped} malformed or repeated question(s)");
            if (questions.Count == 0) errors.Add("test_generator: no usable questions, write the test by hand");
        }
        catch (AiUnavailableException)
        {
            errors.Add("test_generator: LLM unavailable, write the test by hand");
        }

        return new TestDraft(questions, errors, $"vllm/{_options.ChatModel}", PromptVersion);
    }

    /// <summary>Well-formed questions only, in order, up to <paramref name="limit"/>.</summary>
    internal static IReadOnlyList<TestQuestionDraft> CleanQuestions(JsonNode? raw, int limit)
    {
        var kept = new List<TestQuestionDraft>();
        if (raw is not JsonArray array) return kept;

        var seen = new HashSet<string>();
        foreach (var item in array.OfType<JsonObject>())
        {
            var text = JsonRead.Text(item["text"]);
            if (text is null) continue;

            if (item["options"] is not JsonArray { Count: OptionsPerQuestion } rawOptions) continue;
            var options = rawOptions.Select(option => JsonRead.Text(option) ?? "").ToList();
            if (options.Any(option => option.Length == 0)) continue;
            if (options.Select(option => option.ToLowerInvariant()).Distinct().Count() != options.Count) continue;

            if (JsonRead.WholeNumber(item["correct_index"]) is not { } index || index is < 0 or >= OptionsPerQuestion) continue;
            if (!seen.Add(text.ToLowerInvariant())) continue;

            kept.Add(new TestQuestionDraft(text, options, index));
            if (kept.Count >= limit) break;
        }

        return kept;
    }

    private static string UserMessage(TestRequest request) => string.Join("\n\n",
        $"JOB_TITLE:\n{request.JobTitle}",
        $"DESCRIPTION:\n{(string.IsNullOrEmpty(request.Description) ? "(not provided)" : request.Description)}",
        $"REQUIREMENTS:\n{(string.IsNullOrEmpty(request.Requirements) ? "(not provided)" : request.Requirements)}",
        $"NUMBER_OF_QUESTIONS:\n{request.Count}");
}
