namespace HrOs.Ai.Host;

/// <summary>How the host protects itself. It listens on this machine only unless the URL is changed.</summary>
public sealed class ServiceOptions
{
    public const string SectionName = "Service";

    /// <summary>
    /// When set, every call except <c>/health</c> and the API description must send it as
    /// <c>X-Api-Key</c>. Set it as soon as anything other than the HRMS can reach the host.
    /// </summary>
    public string ApiKey { get; set; } = "";

    public int MaxCvMegabytes { get; set; } = 10;
}
