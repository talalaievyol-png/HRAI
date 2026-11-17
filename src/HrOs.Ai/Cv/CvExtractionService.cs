using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace HrOs.Ai.Cv;

public sealed record CvExtraction(string Text, CandidateProfile Profile);

/// <summary>
/// Remembers recent CV extractions by file hash so the same file is never sent to the model
/// twice. Its own cache, so it never takes space in the host application's shared cache.
/// </summary>
public sealed class CvExtractionCache : IDisposable
{
    private readonly MemoryCache _cache;
    private readonly TimeSpan _lifetime;

    public CvExtractionCache(int size = 128, int minutes = 60)
    {
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = size });
        _lifetime = TimeSpan.FromMinutes(minutes);
    }

    public CvExtraction? Get(string key) => _cache.TryGetValue(key, out CvExtraction? hit) ? hit : null;

    public void Put(string key, CvExtraction value) =>
        _cache.Set(key, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = _lifetime });

    public void Dispose() => _cache.Dispose();
}

/// <summary>The careers-site "read my CV" step: file bytes in, text and profile out.</summary>
public sealed class CvExtractionService
{
    /// <summary>Below this many characters a file has no usable text layer (a scan or an image).</summary>
    public const int MinTextChars = 60;

    private readonly CvProfileExtractor _extractor;
    private readonly CvExtractionCache _cache;

    public CvExtractionService(CvProfileExtractor extractor, CvExtractionCache cache)
    {
        _extractor = extractor;
        _cache = cache;
    }

    /// <exception cref="CvReadException">
    /// Unsupported type, corrupt file, or no text layer. The message is for the candidate.
    /// </exception>
    public async Task<CvExtraction> ExtractAsync(byte[] contents, string fileName, CancellationToken cancellationToken = default)
    {
        var key = Convert.ToHexString(SHA256.HashData(contents));
        if (_cache.Get(key) is { } cached) return cached;

        var text = CvTextReader.Read(contents, fileName);
        if (text.Length < MinTextChars) throw CvReadException.NoTextLayer();

        var result = new CvExtraction(text, await _extractor.ExtractAsync(text, cancellationToken));
        _cache.Put(key, result);
        return result;
    }
}
