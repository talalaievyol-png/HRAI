using HrOs.Ai.Core;
using HrOs.Ai.Cv;
using HrOs.Ai.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HrOs.Ai.Tests.Cv;

public class CvExtractionServiceTests
{
    private const string AliceJson = """
        {
          "name": "Alice Smith", "email": "alice@example.com", "phone": "03001234567",
          "address": "Multan", "expected_salary": null, "summary": null,
          "skills": ["Python", "SQL", "FastAPI"], "languages": [], "interests": [], "links": [],
          "education": [{"degree": "B.S. Computer Science", "institution": "COMSATS", "years": "2016-2020"}],
          "experience": [{"position": "Developer", "company": "XY Corp", "years": "2021-present", "description": "built stuff"}],
          "projects": [], "certifications": [], "publications": []
        }
        """;

    private static CvExtractionService ServiceFor(IAiClient ai, AiOptions? options = null) =>
        new(new CvProfileExtractor(ai, Options.Create(options ?? new AiOptions()), NullLogger<CvProfileExtractor>.Instance),
            new CvExtractionCache());

    [Fact]
    public async Task A_text_pdf_is_read_and_parsed_by_the_model()
    {
        var ai = ScriptedAi.Answering(AliceJson);

        var result = await ServiceFor(ai).ExtractAsync(SampleFiles.Pdf(SampleFiles.AliceCv), "cv.pdf");

        Assert.Equal("Alice Smith", result.Profile.Name);
        Assert.Equal("alice@example.com", result.Profile.Email);
        Assert.Contains("Python", result.Profile.Skills);
        Assert.Equal("COMSATS", result.Profile.Education.Single().Get("institution"));
        Assert.Equal("XY Corp", result.Profile.Experience.Single().Get("company"));
        Assert.Contains("Alice Smith", result.Text);
    }

    [Fact]
    public async Task The_model_gets_the_extraction_prompt_with_the_cv_and_a_cold_temperature()
    {
        var ai = ScriptedAi.Answering(AliceJson);

        await ServiceFor(ai, new AiOptions { ChatModel = "main", ProfileModel = "fast" })
            .ExtractAsync(SampleFiles.Text(SampleFiles.AliceCv), "cv.txt");

        var call = ai.ChatCalls.Single();
        Assert.Equal("fast", call.Model);
        Assert.Equal(0.0, call.Temperature);
        Assert.Equal(3000, call.MaxTokens);
        Assert.Equal("system", call.Messages[0].Role);
        Assert.Contains("education (array of objects with degree, institution, years)", call.Messages[0].Content);
        Assert.Contains("Alice Smith", call.Messages[1].Content);
    }

    [Fact]
    public async Task A_scanned_pdf_is_refused_without_asking_the_model()
    {
        var ai = ScriptedAi.Answering(AliceJson);

        var error = await Assert.ThrowsAsync<CvReadException>(
            () => ServiceFor(ai).ExtractAsync(SampleFiles.EmptyPdf(), "scan.pdf"));

        Assert.Contains("scanned or image-based PDF", error.Message);
        Assert.Contains("enter your details manually", error.Message);
        Assert.Empty(ai.ChatCalls);
    }

    [Fact]
    public async Task When_the_model_is_down_the_text_rules_still_fill_the_basics()
    {
        var result = await ServiceFor(ScriptedAi.Down())
            .ExtractAsync(SampleFiles.Pdf(SampleFiles.AliceCv), "cv.pdf");

        Assert.Equal("Alice Smith", result.Profile.Name);
        Assert.Equal("alice@example.com", result.Profile.Email);
        Assert.Equal("03001234567", result.Profile.Phone);
        Assert.Contains("python", result.Profile.Skills);
        Assert.Empty(result.Profile.Education);
    }

    [Fact]
    public async Task A_model_that_answers_in_the_wrong_shape_is_repaired_not_trusted()
    {
        var ai = ScriptedAi.Answering("""
            {"name": " Sana Malik ", "email": null, "expected_salary": 55000,
             "skills": "Excel, Tally; Taxation", "languages": null,
             "education": "BS Accounting", "experience": [{"position": "Officer", "years": 2022}, "free text", {}]}
            """);

        var profile = (await ServiceFor(ai).ExtractAsync(SampleFiles.Text(SampleFiles.AliceCv), "cv.txt")).Profile;

        Assert.Equal("Sana Malik", profile.Name);
        Assert.Null(profile.Email);
        Assert.Equal("55000", profile.ExpectedSalary);
        Assert.Equal(["Excel", "Tally", "Taxation"], profile.Skills);
        Assert.Empty(profile.Languages);
        Assert.Empty(profile.Education);
        var job = Assert.Single(profile.Experience);
        Assert.Equal("Officer", job.Get("position"));
        Assert.Equal("2022", job.Get("years"));
    }

    [Fact]
    public async Task The_same_file_is_not_sent_to_the_model_twice()
    {
        var ai = ScriptedAi.Answering(AliceJson);
        var service = ServiceFor(ai);
        var file = SampleFiles.Pdf(SampleFiles.AliceCv);

        var first = await service.ExtractAsync(file, "cv.pdf");
        var second = await service.ExtractAsync(file, "cv.pdf");

        Assert.Single(ai.ChatCalls);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task A_cnic_printed_on_the_cv_never_reaches_the_model_server()
    {
        var server = new FakeModelServer().ReplyChat(AliceJson);
        var cv = SampleFiles.AliceCv + "CNIC: 35202-1234567-1\n";
        var extractor = new CvProfileExtractor(server.Client, Options.Create(server.Options), NullLogger<CvProfileExtractor>.Instance);
        var service = new CvExtractionService(extractor, new CvExtractionCache());

        var result = await service.ExtractAsync(SampleFiles.Pdf(cv), "cv.pdf");

        Assert.Equal("Alice Smith", result.Profile.Name);
        var sent = server.Calls.Single().Body!.ToJsonString();
        Assert.DoesNotContain("1234567-1", sent);
        Assert.Contains("Alice Smith", sent);
    }

    [Fact]
    public void The_text_rules_do_not_mistake_a_cnic_for_a_phone_number()
    {
        var profile = FallbackProfileExtractor.Extract("Sana Malik\nCNIC 35202-1234567-1\nsana@example.com\n0300-1234567");

        Assert.Equal("0300-1234567", profile.Phone);
    }

    [Fact]
    public void The_text_rules_skip_the_email_line_and_lines_with_digits_when_finding_the_name()
    {
        var profile = FallbackProfileExtractor.Extract("alice@example.com\nPhone 0300 1234567\nAlice Smith\nSkills: sql");

        Assert.Equal("Alice Smith", profile.Name);
    }

    [Fact]
    public void Validation_lists_what_is_missing_and_asks_for_review()
    {
        var result = CvValidator.Validate("short", new CandidateProfile());

        Assert.False(result.Valid);
        Assert.True(result.RequiresReview);
        Assert.Equal(5, result.Issues.Count);
    }

    [Fact]
    public void A_complete_profile_is_valid()
    {
        var profile = new CandidateProfile
        {
            Name = "Alice", Email = "a@b.c", Skills = ["sql"],
            Education = [new ProfileEntry { ["degree"] = "BS" }],
        };

        var result = CvValidator.Validate(new string('x', 120), profile);

        Assert.True(result.Valid);
        Assert.False(result.RequiresReview);
        Assert.Empty(result.Issues);
    }
}
