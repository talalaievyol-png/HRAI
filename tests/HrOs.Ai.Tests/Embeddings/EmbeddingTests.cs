using System.Text.Json.Nodes;
using HrOs.Ai.Cv;
using HrOs.Ai.Embeddings;
using HrOs.Ai.Tests.Support;

namespace HrOs.Ai.Tests.Embeddings;

public class EmbeddingTests
{
    private static CandidateProfile Profile() => new()
    {
        Name = "Sana Malik",
        Skills = ["Excel", "Tally"],
        Experience = [new ProfileEntry { ["position"] = "Accounts Officer", ["company"] = "Evyol", ["years"] = "2022-2025" }],
        Education = [new ProfileEntry { ["degree"] = "BS Accounting", ["institution"] = "UoS" }],
    };

    [Fact]
    public void The_candidate_text_is_name_skills_jobs_and_education_one_per_line()
    {
        Assert.Equal(
            "Sana Malik\nExcel\nTally\nAccounts Officer Evyol 2022-2025\nBS Accounting UoS",
            EmbeddingService.CandidateSource(Profile()));
    }

    [Fact]
    public void A_profile_with_no_skills_and_no_jobs_is_not_embedded()
    {
        Assert.False(EmbeddingService.ShouldEmbed(new CandidateProfile { Name = "x" }));
        Assert.True(EmbeddingService.ShouldEmbed(new CandidateProfile { Skills = ["sql"] }));
    }

    [Fact]
    public async Task Nothing_is_sent_to_the_model_for_a_profile_with_nothing_to_embed()
    {
        var ai = new ScriptedAi(_ => new JsonObject()) { Embedding = [1f] };

        var nothing = await new EmbeddingService(ai).EmbedCandidateAsync(new CandidateProfile { Name = "x" });
        var vector = await new EmbeddingService(ai).EmbedCandidateAsync(Profile());

        Assert.Null(nothing);
        Assert.Equal(new[] { 1f }, vector);
    }

    [Fact]
    public void Job_terms_are_the_lowercase_words_of_its_requirements_without_repeats_in_a_stable_order()
    {
        var terms = EmbeddingService.JobTerms(JsonNode.Parse("""
            {"skills": ["Excel", "Tally ERP"], "experience": "2 years in Excel", "education": ["BS Accounting"], "other": ["ignored"]}
            """));

        Assert.Equal(["excel", "tally", "erp", "2", "years", "in", "bs", "accounting"], terms);
    }

    [Fact]
    public void A_job_without_requirements_has_no_terms()
    {
        Assert.Empty(EmbeddingService.JobTerms(null));
        Assert.Empty(EmbeddingService.JobTerms(JsonNode.Parse("[1,2]")));
    }

    [Fact]
    public void Cosine_is_one_for_the_same_direction_zero_for_unrelated_and_safe_on_bad_input()
    {
        Assert.Equal(1.0, PoolSimilarity.Cosine([1f, 2f], [2f, 4f]), 6);
        Assert.Equal(0.0, PoolSimilarity.Cosine([1f, 0f], [0f, 1f]), 6);
        Assert.Equal(0.0, PoolSimilarity.Cosine([], []));
        Assert.Equal(0.0, PoolSimilarity.Cosine([1f], [1f, 2f]));
        Assert.Equal(0.0, PoolSimilarity.Cosine([0f, 0f], [1f, 1f]));
    }

    [Fact]
    public void Jaccard_is_shared_words_over_all_words()
    {
        Assert.Equal(1.0 / 3, PoolSimilarity.Jaccard(new HashSet<string> { "a", "b" }, new HashSet<string> { "b", "c" }), 6);
        Assert.Equal(0.0, PoolSimilarity.Jaccard(new HashSet<string>(), new HashSet<string> { "a" }));
    }

    [Fact]
    public void Vector_similarity_wins_and_shared_words_are_the_fallback()
    {
        var words = new HashSet<string> { "excel", "tally" };
        var jobWords = new HashSet<string> { "excel", "sap" };

        Assert.Equal(1.0, PoolSimilarity.Score([1f, 0f], [1f, 0f], words, jobWords));
        Assert.Equal(0.333, PoolSimilarity.Score(null, null, words, jobWords));
        Assert.Equal(0.333, PoolSimilarity.Score([1f, 0f], [0f, 1f], words, jobWords));
        Assert.Equal(0.0, PoolSimilarity.Score(null, null, new HashSet<string>(), jobWords));
    }
}
