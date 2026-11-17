using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HrOs.Ai;
using HrOs.Ai.Core;
using HrOs.Ai.Cv;
using HrOs.Ai.Embeddings;
using HrOs.Ai.Generation;
using HrOs.Ai.Host;
using HrOs.Ai.Screening;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHrOsAi(builder.Configuration);
builder.Services.Configure<ServiceOptions>(builder.Configuration.GetSection(ServiceOptions.SectionName));
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();

// The key guards everything except the health check and the API description.
app.Use(async (context, next) =>
{
    var key = context.RequestServices.GetRequiredService<IOptions<ServiceOptions>>().Value.ApiKey;
    var open = context.Request.Path.StartsWithSegments("/health") || context.Request.Path.StartsWithSegments("/openapi");
    if (key.Length > 0 && !open)
    {
        var sent = context.Request.Headers["X-Api-Key"].ToString();
        var same = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(key));
        if (!same)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { detail = "A valid X-Api-Key header is required." });
            return;
        }
    }

    await next();
});

app.MapGet("/health", async (IAiClient ai, CancellationToken ct) =>
    Results.Ok(new { status = "ok", ai_available = await ai.IsAvailableAsync(ct) }));

// The careers-site "read my CV" step. A CV is read for text only: no OCR, so a scanned PDF is refused
// with a message that sends the candidate to the manual form.
app.MapPost("/cv/extract", async (
        IFormFile file, CvExtractionService service, IOptions<ServiceOptions> options, CancellationToken ct) =>
    {
        var allowed = new[] { ".pdf", ".doc", ".docx", ".txt" };
        if (!allowed.Contains(Path.GetExtension(file.FileName).ToLowerInvariant()))
            return Problem(400, $"Unsupported file type. Allowed: {string.Join(", ", allowed)}");

        var maxBytes = options.Value.MaxCvMegabytes * 1024L * 1024L;
        if (file.Length > maxBytes) return Problem(400, $"File too large. Maximum size is {options.Value.MaxCvMegabytes} MB");

        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);

        try
        {
            var result = await service.ExtractAsync(memory.ToArray(), file.FileName, ct);
            return Results.Ok(new { profile = result.Profile, text = result.Text });
        }
        catch (CvReadException e)
        {
            return Problem(400, e.Message);
        }
    })
    .DisableAntiforgery();

app.MapPost("/screening", async (ScreeningBody body, ScreeningService service, CancellationToken ct) =>
{
    var description = JobDescriptionBuilder.Build(body.JobTitle, body.JobDescription, body.JobRequirements);
    try
    {
        return Results.Ok(await service.ScreenAsync(
            new ScreeningInput(body.CvText, body.JobTitle, description, body.ApplicationId, body.CandidateId), ct));
    }
    catch (ScreeningTimeoutException e)
    {
        return Problem(504, e.Message);
    }
});

app.MapPost("/embeddings/candidate", async (CandidateProfile profile, EmbeddingService service, CancellationToken ct) =>
    Results.Ok(new { vector = await service.EmbedCandidateAsync(profile, ct) }));

app.MapPost("/embeddings/job", async (JobEmbeddingBody body, EmbeddingService service, CancellationToken ct) =>
    Results.Ok(new { vector = await service.EmbedJobAsync(body.JobRequirements, ct) }));

app.MapPost("/tests/draft", async (TestRequest request, TestGeneratorService service, CancellationToken ct) =>
    Results.Ok(await service.GenerateAsync(request, ct)));

app.MapPost("/emails/draft", async (EmailDraftRequest request, EmailDraftService service, CancellationToken ct) =>
    Results.Ok(await service.DraftAsync(request, ct)));

app.MapPost("/jobs/draft", async (JobAssistRequest request, JobAssistantService service, CancellationToken ct) =>
    Results.Ok(await service.DraftAsync(request, ct)));

app.Run();

static IResult Problem(int status, string detail) => Results.Json(new { detail }, statusCode: status);

namespace HrOs.Ai.Host
{
    /// <summary>One CV against one job. Requirements are the job's stored requirement lists, if any.</summary>
    public sealed record ScreeningBody(
        string CvText,
        string? JobTitle,
        string? JobDescription,
        JsonNode? JobRequirements = null,
        string? ApplicationId = null,
        string? CandidateId = null);

    public sealed record JobEmbeddingBody(JsonNode? JobRequirements);

    public partial class Program;
}
