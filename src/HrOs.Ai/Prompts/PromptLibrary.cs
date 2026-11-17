using System.Collections.Concurrent;

namespace HrOs.Ai.Prompts;

/// <summary>
/// The prompts, kept as text files and embedded in the assembly, so a prompt is edited without
/// touching code and reviewed as plain text. They live in <c>Prompts/text/</c>; the Python
/// reference reads the same files, so the two cannot drift. The safety rules in <c>_guardrails.txt</c> are appended to
/// every prompt.
/// </summary>
public static class PromptLibrary
{
    private const string GuardrailsName = "_guardrails";
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>The prompt <c>{name}.txt</c> followed by the shared safety rules.</summary>
    public static string Get(string name) => Cache.GetOrAdd(name, key => $"{Read(key)}\n\n{Read(GuardrailsName)}");

    private static string Read(string name)
    {
        var assembly = typeof(PromptLibrary).Assembly;
        var resource = $"HrOs.Ai.Prompts.{name}.txt";
        using var stream = assembly.GetManifestResourceStream(resource)
                           ?? throw new InvalidOperationException($"Prompt '{name}' is not embedded ({resource}).");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
