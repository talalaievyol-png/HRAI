using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using HrOs.Ai.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Tests.Support;

/// <summary>A stand-in for the vLLM servers: records every request, answers from a queue.</summary>
public sealed class FakeModelServer : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _replies = new();

    public List<(string Url, JsonNode? Body)> Calls { get; } = new();

    /// <summary>The Authorization header of each call, in order; null when none was sent.</summary>
    public List<string?> Authorizations { get; } = new();

    public AiOptions Options { get; } = new()
    {
        ChatUrl = "http://chat:8000/v1",
        EmbedUrl = "http://embed:8000/v1",
        EmailUrl = "",
        ChatModel = "qwen3:latest",
        EmailModel = "qwen3:1.7b",
        EmbeddingModel = "all-minilm",
    };

    public VllmAiClient Client => new(new HttpClient(this), Microsoft.Extensions.Options.Options.Create(Options),
        NullLogger<VllmAiClient>.Instance);

    /// <summary>The next answer: a chat completion whose content is the given text.</summary>
    public FakeModelServer ReplyChat(string content, int? promptTokens = null, int? completionTokens = null)
    {
        var body = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["content"] = content },
            }),
        };
        if (promptTokens is not null)
            body["usage"] = new JsonObject { ["prompt_tokens"] = promptTokens, ["completion_tokens"] = completionTokens };
        return ReplyJson(body);
    }

    public FakeModelServer ReplyJson(JsonNode body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _replies.Enqueue(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        });
        return this;
    }

    public FakeModelServer ReplyStatus(HttpStatusCode status)
    {
        _replies.Enqueue(() => new HttpResponseMessage(status));
        return this;
    }

    public FakeModelServer ReplyDown()
    {
        _replies.Enqueue(() => throw new HttpRequestException("connection refused"));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        JsonNode? body = null;
        if (request.Content is not null)
        {
            var text = await request.Content.ReadAsStringAsync(cancellationToken);
            if (text.Length > 0) body = JsonNode.Parse(text);
        }

        Calls.Add((request.RequestUri!.ToString(), body));
        Authorizations.Add(request.Headers.Authorization?.ToString());
        if (_replies.Count == 0) throw new InvalidOperationException("The test did not queue a reply for this call.");
        return _replies.Dequeue()();
    }
}
