using HrOs.Ai.Core;
using HrOs.Ai.Cv;
using HrOs.Ai.Embeddings;
using HrOs.Ai.Generation;
using HrOs.Ai.Screening;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HrOs.Ai;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the AI services. Settings come from the "Ai" section, for example
    /// <c>Ai:ChatUrl</c> and <c>Ai:ChatModel</c>.
    /// </summary>
    public static IServiceCollection AddHrOsAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AiOptions>(configuration.GetSection(AiOptions.SectionName));
        services.AddHttpClient<IAiClient, VllmAiClient>();

        // CV reading. The cache is a singleton; the services that use it are per call.
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AiOptions>>().Value;
            return new CvExtractionCache(options.ProfileCacheSize, options.ProfileCacheMinutes);
        });
        services.AddTransient<CvProfileExtractor>();
        services.AddTransient<CvExtractionService>();

        services.AddTransient<EmbeddingService>();

        // Drafts for HR to edit.
        services.AddTransient<TestGeneratorService>();
        services.AddTransient<EmailDraftService>();
        services.AddTransient<JobAssistantService>();

        // Screening.
        services.TryAddSingleton(TimeProvider.System);
        services.AddTransient<ScreeningPipeline>();
        services.AddTransient<ScreeningService>();
        return services;
    }
}
