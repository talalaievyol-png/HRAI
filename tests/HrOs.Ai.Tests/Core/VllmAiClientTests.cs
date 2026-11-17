using System.Net;
using System.Text.Json.Nodes;
using HrOs.Ai.Core;
using HrOs.Ai.Tests.Support;

namespace HrOs.Ai.Tests.Core;

public class VllmAiClientTests
{
    private static readonly ChatMessage[] Hi = { ChatMessage.User("hi") };

    [Fact]
    public async Task Chat_goes_to_the_openai_endpoint_with_json_mode_and_no_thinking()
    {
        var server = new FakeModelServer().ReplyChat("""{"ok": true}""", promptTokens: 9, completionTokens: 3);

        var result = await server.Client.ChatJsonAsync(Hi, maxTokens: 50);

        Assert.True(result["ok"]!.GetValue<bool>());
        var (url, body) = server.Calls.Single();
        Assert.Equal("http://chat:8000/v1/chat/completions", url);
        Assert.Equal("qwen3:latest", body!["model"]!.GetValue<string>());
        Assert.Equal("json_object", body["response_format"]!["type"]!.GetValue<string>());
        Assert.False(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(50, body["max_tokens"]!.GetValue<int>());
        Assert.False(body["stream"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_model_without_its_own_server_goes_to_the_main_one()
    {
        var server = new FakeModelServer().ReplyChat("{}");

        await server.Client.ChatJsonAsync(Hi, model: server.Options.EmailModel);

        Assert.Equal("http://chat:8000/v1/chat/completions", server.Calls.Single().Url);
    }

    [Fact]
    public async Task The_email_model_uses_its_own_server_when_one_is_set()
    {
        var server = new FakeModelServer().ReplyChat("{}");
        server.Options.EmailUrl = "http://email:8000/v1/";

        await server.Client.ChatJsonAsync(Hi, model: server.Options.EmailModel);

        Assert.Equal("http://email:8000/v1/chat/completions", server.Calls.Single().Url);
    }

    [Fact]
    public async Task A_fenced_json_answer_is_still_read()
    {
        var server = new FakeModelServer().ReplyChat("```json\n{\"score\": 7}\n```");

        var result = await server.Client.ChatJsonAsync(Hi);

        Assert.Equal(7, result["score"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("sorry")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public async Task An_answer_that_is_not_a_json_object_is_ai_unavailable(string answer)
    {
        var server = new FakeModelServer().ReplyChat(answer);

        await Assert.ThrowsAsync<AiUnavailableException>(() => server.Client.ChatJsonAsync(Hi));
    }

    [Fact]
    public async Task A_server_error_or_a_down_server_is_ai_unavailable()
    {
        var error = new FakeModelServer().ReplyStatus(HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<AiUnavailableException>(() => error.Client.ChatJsonAsync(Hi));

        var down = new FakeModelServer().ReplyDown();
        await Assert.ThrowsAsync<AiUnavailableException>(() => down.Client.ChatJsonAsync(Hi));
    }

    [Fact]
    public async Task A_cnic_in_the_text_never_reaches_the_model()
    {
        var server = new FakeModelServer().ReplyChat("{}");
        var cv = ChatMessage.User("Sana Malik, CNIC 35202-1234567-1, also written 3520212345671 and 35202 1234567 1.");

        await server.Client.ChatJsonAsync(new[] { cv });

        var sent = server.Calls.Single().Body!.ToJsonString();
        Assert.DoesNotContain("1234567", sent);
        Assert.Contains(AiGuardrails.CnicPlaceholder, sent);
    }

    [Fact]
    public async Task A_cnic_never_reaches_the_embedding_server()
    {
        var server = new FakeModelServer().ReplyJson(Embedding(0.1f));

        await server.Client.EmbedAsync("accounts officer 3520212345671");

        Assert.DoesNotContain("3520212345671", server.Calls.Single().Body!.ToJsonString());
    }

    [Fact]
    public async Task Embeddings_use_the_embedding_server()
    {
        var server = new FakeModelServer().ReplyJson(Embedding(0.1f, 0.2f, 0.3f));

        var vector = await server.Client.EmbedAsync("accounts officer");

        Assert.Equal(new[] { 0.1f, 0.2f, 0.3f }, vector);
        var (url, body) = server.Calls.Single();
        Assert.Equal("http://embed:8000/v1/embeddings", url);
        Assert.Equal("all-minilm", body!["model"]!.GetValue<string>());
        Assert.Equal("accounts officer", body["input"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_embedding_failure_is_null_not_an_error()
    {
        Assert.Null(await new FakeModelServer().ReplyDown().Client.EmbedAsync("x"));
        Assert.Null(await new FakeModelServer().ReplyJson(new JsonObject { ["data"] = new JsonArray() }).Client.EmbedAsync("x"));
    }

    [Fact]
    public async Task A_chat_answer_with_no_choices_is_ai_unavailable()
    {
        var server = new FakeModelServer().ReplyJson(new JsonObject { ["choices"] = new JsonArray() });

        await Assert.ThrowsAsync<AiUnavailableException>(() => server.Client.ChatJsonAsync(Hi));
    }

    [Fact]
    public async Task The_health_check_asks_the_main_server_for_its_models()
    {
        var server = new FakeModelServer().ReplyJson(new JsonObject { ["data"] = new JsonArray() });

        Assert.True(await server.Client.IsAvailableAsync());
        Assert.Equal("http://chat:8000/v1/models", server.Calls.Single().Url);
        Assert.False(await new FakeModelServer().ReplyDown().Client.IsAvailableAsync());
    }

    [Fact]
    public async Task Chat_embeddings_and_the_health_check_all_send_the_api_key_as_a_bearer_token()
    {
        var server = new FakeModelServer()
            .ReplyChat("{}")
            .ReplyJson(Embedding(0.1f))
            .ReplyJson(new JsonObject { ["data"] = new JsonArray() });
        server.Options.ApiKey = "s3cret";
        var client = server.Client;

        await client.ChatJsonAsync(Hi);
        await client.EmbedAsync("accounts officer");
        await client.IsAvailableAsync();

        Assert.Equal(["Bearer s3cret", "Bearer s3cret", "Bearer s3cret"], server.Authorizations);
    }

    [Fact]
    public async Task Without_a_key_no_authorization_header_is_sent_and_the_key_never_enters_a_body()
    {
        var server = new FakeModelServer().ReplyChat("{}").ReplyChat("{}");
        await server.Client.ChatJsonAsync(Hi);
        Assert.Equal([null], server.Authorizations);

        server.Options.ApiKey = "s3cret";
        await server.Client.ChatJsonAsync(Hi);
        Assert.DoesNotContain("s3cret", server.Calls.Last().Body!.ToJsonString());
        Assert.DoesNotContain("s3cret", server.Calls.Last().Url);
    }

    [Fact]
    public void Usage_reads_openai_token_counts()
    {
        var body = new JsonObject { ["usage"] = new JsonObject { ["prompt_tokens"] = 12, ["completion_tokens"] = 4 } };

        Assert.Equal((12, 4), LlmHttp.Usage(body));
        Assert.Equal((null, null), LlmHttp.Usage(new JsonObject()));
    }

    [Fact]
    public void The_profile_model_falls_back_to_the_chat_model()
    {
        var options = new AiOptions { ChatModel = "main" };
        Assert.Equal("main", options.EffectiveProfileModel);
        options.ProfileModel = "fast";
        Assert.Equal("fast", options.EffectiveProfileModel);
    }

    private static JsonObject Embedding(params float[] values) => new()
    {
        ["data"] = new JsonArray(new JsonObject
        {
            ["embedding"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
        }),
    };
}
