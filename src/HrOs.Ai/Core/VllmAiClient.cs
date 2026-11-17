using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Core;

/// <summary>
/// <see cref="IAiClient"/> over vLLM's OpenAI-compatible API, one server per model.
/// Logs and traces carry model, token counts and timing, never the prompt text.
/// </summary>
public sealed class VllmAiClient : IAiClient
{
    public static readonly ActivitySource Activities = new("HrOs.Ai");

    private const int EmbedMaxChars = 8000;

    private readonly HttpClient _http;
    private readonly AiOptions _options;
    private readonly ILogger<VllmAiClient> _logger;

    public VllmAiClient(HttpClient http, IOptions<AiOptions> options, ILogger<VllmAiClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<JsonObject> ChatJsonAsync(
        IReadOnlyList<ChatMessage> messages,
        string? model = null,
        int? maxTokens = null,
        double temperature = 0.1,
        CancellationToken cancellationToken = default)
    {
        var modelName = string.IsNullOrWhiteSpace(model) ? _options.ChatModel : model;
        var safe = messages
            .Select(m => m with { Content = AiGuardrails.Sanitize(m.Content, _options.MaxInputChars) })
            .ToList();
        var url = $"{_options.UrlFor(modelName)}/chat/completions";
        var payload = LlmHttp.ChatPayload(modelName, safe, temperature, maxTokens);

        using var activity = Activities.StartActivity("vllm-chat");
        activity?.SetTag("gen_ai.request.model", modelName);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var body = await PostAsync(url, payload, _options.ChatTimeoutSeconds, cancellationToken);
            var result = LlmHttp.ParseJsonObject(LlmHttp.ChatContent(body));
            var (input, output) = LlmHttp.Usage(body);
            activity?.SetTag("gen_ai.usage.input_tokens", input);
            activity?.SetTag("gen_ai.usage.output_tokens", output);
            _logger.LogInformation(
                "llm.chat model={Model} prompt_tokens={Input} completion_tokens={Output} dur_ms={Ms}",
                modelName, input, output, ElapsedMs(started));
            return result;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TimeoutException
                                      or InvalidOperationException or FormatException)
        {
            _logger.LogWarning("llm.chat model={Model} failed err={Error} dur_ms={Ms}",
                modelName, Short(e.Message), ElapsedMs(started));
            throw new AiUnavailableException(e.Message, e);
        }
    }

    public async Task<float[]?> EmbedAsync(string text, string? model = null, CancellationToken cancellationToken = default)
    {
        var modelName = string.IsNullOrWhiteSpace(model) ? _options.EmbeddingModel : model;
        var safe = AiGuardrails.Sanitize(text, EmbedMaxChars);
        var url = $"{_options.UrlFor(modelName)}/embeddings";

        using var activity = Activities.StartActivity("vllm-embed");
        activity?.SetTag("gen_ai.request.model", modelName);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var body = await PostAsync(url, LlmHttp.EmbedPayload(modelName, safe), _options.EmbedTimeoutSeconds, cancellationToken);
            var vector = LlmHttp.Embedding(body);
            _logger.LogInformation("llm.embed model={Model} embedded={Embedded} dur_ms={Ms}",
                modelName, vector is not null, ElapsedMs(started));
            return vector;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TimeoutException
                                      or InvalidOperationException or FormatException)
        {
            _logger.LogWarning("llm.embed model={Model} failed dur_ms={Ms}", modelName, ElapsedMs(started));
            return null;
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = NewRequest(HttpMethod.Get, LlmHttp.HealthUrl(_options.ChatUrl));
            using var response = await _http.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<JsonNode> PostAsync(string url, JsonObject payload, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            using var request = NewRequest(HttpMethod.Post, url);
            request.Content = JsonContent.Create(payload);
            using var response = await _http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonNode>(timeout.Token);
            return body ?? throw new JsonException("Empty response from the model server.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The model server did not answer within {timeoutSeconds}s.");
        }
    }

    /// <summary>A request with the bearer key when one is set. The key goes in a header only, never in a body, URL or log.</summary>
    private HttpRequestMessage NewRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());
        return request;
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static string Short(string message) => message.Length <= 200 ? message : message[..200];
}
