using HrOs.Ai.Core;
using HrOs.Ai.Host;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HrOs.Ai.Tests.Host;

/// <summary>The real host in memory, with the model replaced by a script.</summary>
public sealed class HostFactory : WebApplicationFactory<Program>
{
    private readonly IAiClient _ai;
    private readonly Dictionary<string, string?> _settings;

    public HostFactory(IAiClient ai, Dictionary<string, string?>? settings = null)
    {
        _ai = ai;
        _settings = settings ?? new();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(_settings));
        builder.ConfigureServices(services => services.AddSingleton(_ai));
    }
}
