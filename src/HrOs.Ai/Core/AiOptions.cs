namespace HrOs.Ai.Core;

/// <summary>
/// Where the local vLLM servers are and which model name each one serves.
/// One OpenAI-compatible server per model; each is started with
/// <c>--served-model-name</c> equal to the name below. Nothing here is a cloud address.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>Main chat server: screening, CV parsing, test drafts and (on 16 GB) emails.</summary>
    public string ChatUrl { get; set; } = "http://localhost:8101/v1";

    /// <summary>Embedding server for the talent-pool match.</summary>
    public string EmbedUrl { get; set; } = "http://localhost:8103/v1";

    /// <summary>Optional small email model server. Empty means emails use the main server.</summary>
    public string EmailUrl { get; set; } = "";

    /// <summary>
    /// Sent as a bearer token when the vLLM servers run on another machine and were started with
    /// <c>--api-key</c>. Empty for a server on this machine. Never logged.
    /// </summary>
    public string ApiKey { get; set; } = "";

    public string ChatModel { get; set; } = "qwen3:latest";

    /// <summary>Optional faster model for CV parsing. Empty means <see cref="ChatModel"/>.</summary>
    public string ProfileModel { get; set; } = "";

    public string EmailModel { get; set; } = "qwen3:1.7b";

    /// <summary>The model that screens candidates.</summary>
    public string EvaluationModel { get; set; } = "qwen3:latest";

    /// <summary>
    /// Optional second-opinion model, used only when the first screening is uncertain.
    /// Empty means no second opinion. It must be a model that is actually being served.
    /// </summary>
    public string FallbackEvaluationModel { get; set; } = "";

    public string EmbeddingModel { get; set; } = "all-minilm";

    public int ChatTimeoutSeconds { get; set; } = 60;

    public int EmbedTimeoutSeconds { get; set; } = 60;

    /// <summary>Longest text one prompt message may carry; longer text is cut.</summary>
    public int MaxInputChars { get; set; } = 48000;

    /// <summary>Whole-screening time limit, as <c>AUTO_SCREEN_TIMEOUT_SECONDS</c> in the Python app.</summary>
    public int ScreeningTimeoutSeconds { get; set; } = 360;

    /// <summary>How many recent CV extractions to remember, and for how long.</summary>
    public int ProfileCacheSize { get; set; } = 128;

    public int ProfileCacheMinutes { get; set; } = 60;

    /// <summary>The model that does CV parsing.</summary>
    public string EffectiveProfileModel => string.IsNullOrWhiteSpace(ProfileModel) ? ChatModel : ProfileModel;

    /// <summary>The server that serves a model name. Anything unlisted goes to the main one.</summary>
    public string UrlFor(string model)
    {
        string? url = null;
        if (model == EmbeddingModel) url = EmbedUrl;
        else if (model == EmailModel) url = EmailUrl;
        return (string.IsNullOrWhiteSpace(url) ? ChatUrl : url).TrimEnd('/');
    }
}
