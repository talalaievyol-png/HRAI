using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HrOs.Ai.Tests.Support;

namespace HrOs.Ai.Tests.Host;

public class HostApiTests
{
    private static readonly string[] AllSteps =
    [
        RoutedAi.Extraction, RoutedAi.Validation, RoutedAi.Requirements, RoutedAi.Matching,
        RoutedAi.Evidence, RoutedAi.Uncertainty, RoutedAi.Recommendation,
    ];

    private static MultipartFormDataContent Upload(byte[] bytes, string name)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "file", name);
        return content;
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task Health_says_whether_the_model_server_is_reachable()
    {
        using var up = new HostFactory(new ScriptedAi(_ => new JsonObject()) { Available = true });
        using var down = new HostFactory(new ScriptedAi(_ => new JsonObject()) { Available = false });

        Assert.True((await Json(await up.CreateClient().GetAsync("/health")))["ai_available"]!.GetValue<bool>());
        Assert.False((await Json(await down.CreateClient().GetAsync("/health")))["ai_available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_cv_upload_returns_the_profile_and_the_text()
    {
        using var factory = new HostFactory(ScriptedAi.Answering("""{"name": "Alice Smith", "email": "alice@example.com", "skills": ["SQL"]}"""));

        var response = await factory.CreateClient().PostAsync("/cv/extract", Upload(SampleFiles.Pdf(SampleFiles.AliceCv), "cv.pdf"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("Alice Smith", body["profile"]!["name"]!.GetValue<string>());
        Assert.Equal("SQL", body["profile"]!["skills"]![0]!.GetValue<string>());
        Assert.Contains("alice@example.com", body["text"]!.GetValue<string>());
        // The careers-site form reads these exact keys, present even when empty.
        var profile = body["profile"]!.AsObject();
        foreach (var key in new[] { "expected_salary", "summary", "education", "experience", "projects", "certifications", "publications", "languages", "interests", "links" })
            Assert.True(profile.ContainsKey(key), $"profile is missing '{key}'");
    }

    [Fact]
    public async Task A_scanned_pdf_is_refused_with_the_message_that_sends_the_candidate_to_the_manual_form()
    {
        using var factory = new HostFactory(ScriptedAi.Answering("{}"));

        var response = await factory.CreateClient().PostAsync("/cv/extract", Upload(SampleFiles.EmptyPdf(), "scan.pdf"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("enter your details manually", (await Json(response))["detail"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("cv.png")]
    [InlineData("cv.rtf")]
    [InlineData("cv")]
    public async Task An_unsupported_file_type_is_refused(string name)
    {
        using var factory = new HostFactory(ScriptedAi.Answering("{}"));

        var response = await factory.CreateClient().PostAsync("/cv/extract", Upload([1, 2, 3], name));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Unsupported file type", (await Json(response))["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_legacy_doc_is_refused_with_what_to_upload_instead()
    {
        using var factory = new HostFactory(ScriptedAi.Answering("{}"));

        var response = await factory.CreateClient().PostAsync("/cv/extract", Upload([1, 2, 3], "cv.doc"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("upload .docx or PDF", (await Json(response))["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_refused()
    {
        using var factory = new HostFactory(ScriptedAi.Answering("{}"), new() { ["Service:MaxCvMegabytes"] = "0" });

        var response = await factory.CreateClient().PostAsync("/cv/extract", Upload(SampleFiles.Text(SampleFiles.AliceCv), "cv.txt"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("File too large", (await Json(response))["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Screening_returns_the_report_with_snake_case_keys()
    {
        var ai = new RoutedAi();
        foreach (var step in AllSteps) ai.Answer(step, "{}");
        ai.Answer(RoutedAi.Requirements, """{"mandatory": ["BS Accounting"]}""")
          .Answer(RoutedAi.Matching, """{"requirements": [{"requirement": "Relevance: how closely the candidate's work history (for a fresher, their degree, internships and projects) matches the duties of this role", "status": "MATCH", "evidence": "BS Accounting, University of Sargodha"}, {"requirement": "BS Accounting", "status": "MATCH", "evidence": "BS Accounting"}]}""");
        using var factory = new HostFactory(ai);

        var response = await factory.CreateClient().PostAsJsonAsync("/screening", new
        {
            cv_text = "Sana Malik. BS Accounting, University of Sargodha.",
            job_title = "Accounts Officer",
            job_description = "Prepare ledgers",
            job_requirements = new { skills = new[] { "Excel" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await Json(response);
        Assert.Equal("MATCH", report["recommendation"]!.GetValue<string>());
        Assert.Equal(100, report["score"]!.GetValue<int>());
        var bs = report["evidence"]!.AsArray().Single(item => item!["requirement"]!.GetValue<string>() == "BS Accounting")!;
        Assert.True(bs["is_mandatory"]!.GetValue<bool>());
        Assert.True(report.AsObject().ContainsKey("requires_hr_review"));
        Assert.True(report.AsObject().ContainsKey("prompt_versions"));
        // The listed requirements reach the model as part of the job text.
        Assert.Contains("- Excel", ai.Calls.First(call => call.Step == RoutedAi.Requirements).User);
    }

    [Fact]
    public async Task A_screening_that_runs_out_of_time_answers_504()
    {
        using var factory = new HostFactory(new HangingAi(), new() { ["Ai:ScreeningTimeoutSeconds"] = "1" });

        var response = await factory.CreateClient().PostAsJsonAsync("/screening", new { cv_text = "x", job_title = "t" });

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task An_email_draft_accepts_json_context_with_lists()
    {
        var ai = ScriptedAi.Answering("""{"subject": "s", "body": "Dear Ali,\n\nthanks"}""");
        using var factory = new HostFactory(ai);

        var response = await factory.CreateClient().PostAsJsonAsync("/emails/draft", new
        {
            email_type = "REJECTED",
            candidate_name = "Ali",
            context = new Dictionary<string, object?> { ["missing"] = new[] { "BS Accounting", "SAP" }, ["rank"] = null },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("s", (await Json(response))["subject"]!.GetValue<string>());
        Assert.Contains("MISSING: BS Accounting, SAP", ai.ChatCalls.Single().Messages[1].Content);
        Assert.DoesNotContain("RANK", ai.ChatCalls.Single().Messages[1].Content);
    }

    [Fact]
    public async Task The_test_and_job_drafts_answer()
    {
        using var tests = new HostFactory(ScriptedAi.Answering("""{"questions": [{"text": "q", "options": ["a","b","c","d"], "correct_index": 2}]}"""));
        var test = await Json(await tests.CreateClient().PostAsJsonAsync("/tests/draft", new { job_title = "Accounts Officer", count = 5 }));
        Assert.Equal(2, test["questions"]![0]!["correct_index"]!.GetValue<int>());

        using var jobs = new HostFactory(ScriptedAi.Answering("""{"description": "Prepare ledgers."}"""));
        var job = await Json(await jobs.CreateClient().PostAsJsonAsync("/jobs/draft", new { job_title = "Accounts Officer", user_note = "ledgers" }));
        Assert.Equal("Prepare ledgers.", job["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task Embeddings_answer_with_a_vector_or_null()
    {
        using var factory = new HostFactory(new ScriptedAi(_ => new JsonObject()) { Embedding = [0.5f, 0.25f] });
        var client = factory.CreateClient();

        var job = await Json(await client.PostAsJsonAsync("/embeddings/job", new { job_requirements = new { skills = new[] { "Excel" } } }));
        Assert.Equal(0.5f, job["vector"]![0]!.GetValue<float>());

        var none = await Json(await client.PostAsJsonAsync("/embeddings/candidate", new { name = "x" }));
        Assert.Null(none["vector"]);
    }

    [Fact]
    public async Task With_an_api_key_set_calls_without_it_are_refused_but_health_and_the_api_description_stay_open()
    {
        using var factory = new HostFactory(ScriptedAi.Answering("""{"description": "x"}"""), new() { ["Service:ApiKey"] = "secret" });
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/jobs/draft", new { job_title = "t" })).StatusCode);

        using var wrong = new HttpRequestMessage(HttpMethod.Post, "/jobs/draft") { Content = JsonContent.Create(new { job_title = "t" }) };
        wrong.Headers.Add("X-Api-Key", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrong)).StatusCode);

        using var right = new HttpRequestMessage(HttpMethod.Post, "/jobs/draft") { Content = JsonContent.Create(new { job_title = "t" }) };
        right.Headers.Add("X-Api-Key", "secret");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(right)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task The_api_description_lists_every_endpoint()
    {
        using var factory = new HostFactory(ScriptedAi.Answering("{}"));

        var document = await Json(await factory.CreateClient().GetAsync("/openapi/v1.json"));

        var paths = document["paths"]!.AsObject().Select(pair => pair.Key).ToList();
        foreach (var path in new[] { "/health", "/cv/extract", "/screening", "/embeddings/candidate", "/embeddings/job", "/tests/draft", "/emails/draft", "/jobs/draft" })
            Assert.Contains(path, paths);
    }
}
