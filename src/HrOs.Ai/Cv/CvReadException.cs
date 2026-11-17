namespace HrOs.Ai.Cv;

public enum CvReadProblem
{
    /// <summary>A file type the reader does not handle (for example .rtf or .png).</summary>
    Unsupported,

    /// <summary>Corrupt, password-protected, or a scanned PDF with no text layer.</summary>
    Unreadable,
}

/// <summary>
/// The CV could not be turned into text. The message is written for the candidate and
/// ends with the way forward, so a host can show it as is (HTTP 400).
/// </summary>
public sealed class CvReadException : Exception
{
    public CvReadException(CvReadProblem problem, string message) : base(message) => Problem = problem;

    public CvReadProblem Problem { get; }

    public static CvReadException Corrupt() => new(CvReadProblem.Unreadable,
        "Could not read any text from this CV. The file may be corrupt or password-protected. " +
        "Please upload a valid CV file.");

    public static CvReadException NoTextLayer() => new(CvReadProblem.Unreadable,
        "Could not read any text from this CV. It may be a scanned or image-based PDF without a " +
        "readable text layer. Please enter your details manually instead.");

    public static CvReadException Legacy() => new(CvReadProblem.Unsupported,
        "Legacy .doc files are not supported; please upload .docx or PDF");

    public static CvReadException UnsupportedType() => new(CvReadProblem.Unsupported, "Unsupported CV file type");
}
