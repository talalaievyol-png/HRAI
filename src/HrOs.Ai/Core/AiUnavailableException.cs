namespace HrOs.Ai.Core;

/// <summary>
/// The local model could not answer: server down, timeout, or an answer that is not JSON.
/// Callers fall back to a rule or flag the case for HR; the AI never blocks a person.
/// </summary>
public sealed class AiUnavailableException : Exception
{
    public AiUnavailableException(string message) : base(message) { }

    public AiUnavailableException(string message, Exception inner) : base(message, inner) { }
}
