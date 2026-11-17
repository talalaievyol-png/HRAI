namespace HrOs.Ai.Embeddings;

/// <summary>
/// How close a pool candidate is to a job: vector similarity when both have a vector, shared
/// words when they do not. It ranks candidates for HR; it never invites or rejects anyone.
/// </summary>
public static class PoolSimilarity
{
    /// <summary>1 for the same direction, 0 for unrelated. Zero when either vector is empty or they differ in length.</summary>
    public static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count == 0 || a.Count != b.Count) return 0.0;

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 0.0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    /// <summary>Shared words over all words: 1 when identical, 0 when nothing is shared or either is empty.</summary>
    public static double Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;
        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    /// <summary>
    /// Vector similarity first. If that gives nothing (no vector, or nothing in common), shared
    /// words are used instead. Rounded to three places.
    /// </summary>
    public static double Score(
        IReadOnlyList<float>? candidateVector,
        IReadOnlyList<float>? jobVector,
        IReadOnlySet<string> candidateTerms,
        IReadOnlySet<string> jobTerms)
    {
        var similarity = candidateVector is not null && jobVector is not null
            ? Cosine(candidateVector, jobVector)
            : 0.0;

        if (similarity == 0.0 && candidateTerms.Count > 0 && jobTerms.Count > 0)
            similarity = Jaccard(candidateTerms, jobTerms);

        return Math.Round(similarity, 3);
    }
}
