using System.Text.RegularExpressions;
using HrOs.Ai.Core;
using HrOs.Ai.Prompts;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Generation;

public sealed record JobAssistRequest(string? JobTitle, string? UserNote);

/// <summary>A draft job description for HR to edit. Salary is not part of it; the hiring manager sets that.</summary>
public sealed record JobDraft(string Description, IReadOnlyList<string> Errors, string Model, string PromptVersion);

/// <summary>
/// Turns a job title and a few lines from HR into a draft description. Without the model it keeps
/// what HR wrote, so nothing is lost. The draft is never saved by this service.
/// </summary>
public sealed partial class JobAssistantService
{
    public const string PromptVersion = "job-assistant-v7";

    private readonly IAiClient _ai;
    private readonly AiOptions _options;

    public JobAssistantService(IAiClient ai, IOptions<AiOptions> options)
    {
        _ai = ai;
        _options = options.Value;
    }

    [GeneratedRegex(@"^#{1,6}\s*")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*(?:[*\-+]|\d+[.)])\s+")]
    private static partial Regex ListMarker();

    public async Task<JobDraft> DraftAsync(JobAssistRequest request, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        string description;
        try
        {
            var json = await _ai.ChatJsonAsync(
                [ChatMessage.System(PromptLibrary.Get("job_assistant")), ChatMessage.User(UserMessage(request))],
                model: _options.ChatModel,
                cancellationToken: cancellationToken);
            description = ToPlainText(JsonRead.Text(json["description"]) ?? "");
        }
        catch (AiUnavailableException)
        {
            errors.Add("job_assistant: LLM failed, used deterministic fallback");
            description = Fallback(request);
        }

        return new JobDraft(description, errors, $"vllm/{_options.ChatModel}", PromptVersion);
    }

    /// <summary>Strips markdown headings, bullets, numbering and bold, which the model sometimes adds despite the prompt.</summary>
    internal static string ToPlainText(string value)
    {
        var lines = new List<string>();
        foreach (var line in value.Split('\n'))
        {
            var text = line.Trim();
            text = Heading().Replace(text, "");
            text = ListMarker().Replace(text, "");
            text = text.Replace("**", "").Trim();
            if (text.Length > 0) lines.Add(text);
        }

        return string.Join("\n", lines);
    }

    private static string Fallback(JobAssistRequest request) =>
        !string.IsNullOrEmpty(request.UserNote) ? request.UserNote
        : !string.IsNullOrEmpty(request.JobTitle) ? $"Role for {request.JobTitle}"
        : "";

    private static string UserMessage(JobAssistRequest request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(request.JobTitle)) parts.Add($"JOB_TITLE:\n{request.JobTitle}");
        parts.Add("USER_NOTE / PROMPT:");
        parts.Add(string.IsNullOrEmpty(request.UserNote) ? "(not provided)" : request.UserNote);
        return string.Join("\n\n", parts);
    }
}
