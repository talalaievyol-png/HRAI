using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HrOs.Ai.Core;
using HrOs.Ai.Prompts;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Generation;

public sealed record EmailDraftRequest
{
    /// <summary>REJECTED, SELECTED, REPLY, INTERVIEW or anything else; unknown types get a general draft.</summary>
    public string? EmailType { get; init; }
    public string? CandidateName { get; init; }
    public string? JobTitle { get; init; }

    /// <summary>A short decision reason, written for the candidate to read.</summary>
    public string? Reason { get; init; }

    /// <summary>Extra facts for the writer, such as a list of missing requirements. Lists are joined with commas.</summary>
    public IReadOnlyDictionary<string, object?>? Context { get; init; }

    /// <summary>Free-form instructions from HR.</summary>
    public string? HrNotes { get; init; }

    /// <summary>warm, firm, professional or custom.</summary>
    public string? Tone { get; init; }
    public string? CompanyName { get; init; }
    public string? HrName { get; init; }
}

/// <summary>A draft email for a person to read and edit. This service never sends anything.</summary>
public sealed record EmailDraft(string Subject, string Body, string Model, IReadOnlyList<string> Errors);

/// <summary>
/// Drafts a recruitment email on the small email model. When the model is down a plain template
/// gives HR a usable draft to edit, and the sign-off is always finished from the real names, never
/// from a placeholder the model echoed.
/// </summary>
public sealed partial class EmailDraftService
{
    public const string PromptVersion = "email-draft-v5";

    private readonly IAiClient _ai;
    private readonly AiOptions _options;
    private readonly TimeProvider _clock;

    public EmailDraftService(IAiClient ai, IOptions<AiOptions> options, TimeProvider clock)
    {
        _ai = ai;
        _options = options.Value;
        _clock = clock;
    }

    [GeneratedRegex(@"^[ \t]*CURRENT_DATE[:：]?[ \t]*.*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DateLine();

    [GeneratedRegex(@"[ \t]*on behalf of[ \t]*\.?[ \t]*", RegexOptions.IgnoreCase)]
    private static partial Regex OnBehalfOf();

    [GeneratedRegex(@"Best regards,[ \t]*$", RegexOptions.IgnoreCase)]
    private static partial Regex BareRegards();

    [GeneratedRegex(@"[\[\{\(][A-Z][A-Z0-9_ ]*[\]\}\)]")]
    private static partial Regex LeftoverToken();

    // What a candidate must never read: an internal score, a rank, or a removed ID number.
    [GeneratedRegex(@"\b(?:score[sd]?|ranked|ranking|percentile)\b|\b\d{1,3}\s*(?:/\s*100|%\s*(?:match|fit))|\[CNIC removed\]", RegexOptions.IgnoreCase)]
    private static partial Regex InternalDetail();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpaceRun();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    public async Task<EmailDraft> DraftAsync(EmailDraftRequest request, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        string subject, body;
        try
        {
            var json = await _ai.ChatJsonAsync(
                [ChatMessage.System(PromptLibrary.Get("email_draft")), ChatMessage.User(UserMessage(request))],
                model: _options.EmailModel,
                cancellationToken: cancellationToken);

            subject = json["subject"] is System.Text.Json.Nodes.JsonValue s && s.TryGetValue<string>(out var sv) ? sv : throw new FormatException();
            body = json["body"] is System.Text.Json.Nodes.JsonValue b && b.TryGetValue<string>(out var bv) ? bv : throw new FormatException();
            if (LeaksInternals(subject) || LeaksInternals(body))
                throw new FormatException("The draft mentions a score, a rank or a removed ID.");
        }
        catch (Exception e) when (e is AiUnavailableException or FormatException)
        {
            errors.Add("email: LLM failed, used deterministic fallback");
            (subject, body) = Fallback(request);
        }

        return new EmailDraft(
            CleanPlaceholders(subject, request.CompanyName, request.HrName),
            CleanPlaceholders(body, request.CompanyName, request.HrName),
            $"vllm/{_options.EmailModel}",
            errors);
    }

    /// <summary>True when a draft shows the candidate something internal: a score, a rank, a removed ID.</summary>
    internal static bool LeaksInternals(string text) => InternalDetail().IsMatch(text);

    internal string UserMessage(EmailDraftRequest request)
    {
        var date = _clock.GetUtcNow().ToString("dddd, dd MMMM yyyy", CultureInfo.InvariantCulture);
        var parts = new List<string>
        {
            $"CURRENT_DATE: {date}",
            $"EMAIL_TYPE: {(string.IsNullOrEmpty(request.EmailType) ? "general" : request.EmailType)}",
        };
        if (!string.IsNullOrEmpty(request.CandidateName)) parts.Add($"CANDIDATE_NAME: {request.CandidateName}");
        if (!string.IsNullOrEmpty(request.JobTitle)) parts.Add($"JOB_TITLE: {request.JobTitle}");
        if (!string.IsNullOrEmpty(request.Tone)) parts.Add($"TONE: {request.Tone}");
        if (!string.IsNullOrEmpty(request.CompanyName)) parts.Add($"COMPANY_NAME: {request.CompanyName}");
        if (!string.IsNullOrEmpty(request.HrName)) parts.Add($"HR_NAME: {request.HrName}");

        var context = RenderContext(request.Context, request.Reason);
        if (context.Length > 0) parts.Add("CONTEXT:\n" + context);

        parts.Add("HR_NOTES / INSTRUCTIONS:");
        parts.Add(string.IsNullOrEmpty(request.HrNotes)
            ? "(no additional instructions — write a standard email)"
            : request.HrNotes);
        return string.Join("\n\n", parts);
    }

    private static string RenderContext(IReadOnlyDictionary<string, object?>? context, string? reason)
    {
        var lines = new List<string>();
        if (!string.IsNullOrEmpty(reason)) lines.Add($"REASON: {reason}");
        foreach (var (key, value) in context ?? new Dictionary<string, object?>())
        {
            var text = value switch
            {
                null => "",
                string s => s,
                JsonElement element => RenderElement(element),
                IEnumerable items => string.Join(", ", items.Cast<object?>().Select(item => item?.ToString())),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
            };
            if (text.Length > 0) lines.Add($"{key.ToUpperInvariant()}: {text}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>A JSON value from a request: lists are joined with commas, null and empty are skipped.</summary>
    private static string RenderElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Array => string.Join(", ", element.EnumerateArray().Select(RenderElement).Where(item => item.Length > 0)),
        _ => element.GetRawText(),
    };

    private static string SignOff(EmailDraftRequest request)
    {
        var hr = string.IsNullOrEmpty(request.HrName) ? "Human Resources" : request.HrName;
        return string.IsNullOrEmpty(request.CompanyName)
            ? $"Best regards,\n{hr}"
            : $"Best regards,\n{hr}\n{request.CompanyName}";
    }

    /// <summary>The plain-template email used when the model cannot write one.</summary>
    internal static (string Subject, string Body) Fallback(EmailDraftRequest request)
    {
        var name = string.IsNullOrEmpty(request.CandidateName) ? "there" : request.CandidateName;
        var title = string.IsNullOrEmpty(request.JobTitle) ? "the position" : request.JobTitle;

        switch (request.EmailType)
        {
            case "REJECTED":
            {
                var body = $"Dear {name},\n\nThank you for applying for {title}. After careful review we regret " +
                           "to inform you that we will not be moving forward with your application.";
                if (!string.IsNullOrEmpty(request.Reason)) body += $"\n\n{request.Reason}";
                body += "\n\nWe appreciate the time and effort you invested in your application.\n\n" + SignOff(request);
                return ($"Application Update on {title}", body);
            }
            case "SELECTED":
                return ($"Application Update: You have been selected for {title}",
                    $"Dear {name},\n\nCongratulations! We are pleased to move your application for {title} " +
                    "forward in the recruitment process. Our HR team will contact you with the next steps.\n\n" +
                    SignOff(request));
            case "REPLY":
                return ("Re: your query",
                    $"Dear {name},\n\nThank you for your email regarding {title}. We have received your " +
                    "query and will get back to you shortly with an answer.\n\n" + SignOff(request));
            default:
            {
                var body = $"Dear {name},\n\nThank you for your application for {title}. " +
                           "Your application is now under review.";
                if (!string.IsNullOrEmpty(request.HrNotes)) body += $"\n\n{request.HrNotes}";
                body += "\n\n" + SignOff(request);
                return ($"Update regarding your application for {title}", body);
            }
        }
    }

    /// <summary>
    /// Finishes the text from the real names. Small models sometimes echo "{HR_NAME}" or
    /// "[COMPANY_NAME]" from the prompt; those become the real name, or disappear when no name is set.
    /// </summary>
    internal static string CleanPlaceholders(string text, string? companyName, string? hrName)
    {
        if (string.IsNullOrEmpty(text)) return text;

        text = DateLine().Replace(text, "");
        var company = companyName ?? "";
        var hr = hrName ?? "";
        text = text.Replace("{COMPANY_NAME}", company).Replace("[COMPANY_NAME]", company)
                   .Replace("{HR_NAME}", hr).Replace("[HR_NAME]", hr);
        if (string.IsNullOrEmpty(companyName)) text = OnBehalfOf().Replace(text, "");
        if (string.IsNullOrEmpty(hrName)) text = BareRegards().Replace(text, "Best regards");
        text = LeftoverToken().Replace(text, "");
        text = SpaceRun().Replace(text, " ");
        text = BlankLines().Replace(text, "\n\n");
        return text.Trim();
    }
}
