using System.Text.Json.Nodes;
using HrOs.Ai.Screening;

namespace HrOs.Ai.Tests.Screening;

public class ScreeningRulesTests
{
    private static MatchedRequirement R(MatchStatus status, bool mandatory = false) =>
        new("req", status, null, mandatory);

    [Fact]
    public void An_empty_list_scores_zero()
    {
        Assert.Equal(0, RecommendationNode.ComputeScore([]));
    }

    [Fact]
    public void Match_is_100_partial_50_unclear_25_missing_0()
    {
        Assert.Equal(100, RecommendationNode.ComputeScore([R(MatchStatus.Match)]));
        Assert.Equal(50, RecommendationNode.ComputeScore([R(MatchStatus.Partial)]));
        Assert.Equal(25, RecommendationNode.ComputeScore([R(MatchStatus.Unclear)]));
        Assert.Equal(0, RecommendationNode.ComputeScore([R(MatchStatus.Missing)]));
    }

    [Fact]
    public void Mandatory_requirements_weigh_double()
    {
        // (100 * 2 + 0 * 1) / 3
        Assert.Equal(66, RecommendationNode.ComputeScore([R(MatchStatus.Match, true), R(MatchStatus.Missing)]));
    }

    [Fact]
    public void A_missing_mandatory_requirement_caps_the_score_at_35()
    {
        var matched = new[] { R(MatchStatus.Missing, true), R(MatchStatus.Match), R(MatchStatus.Match), R(MatchStatus.Match) };

        Assert.Equal(35, RecommendationNode.ComputeScore(matched));
    }

    [Fact]
    public void An_unclear_mandatory_requirement_caps_the_score_at_55()
    {
        var matched = new[] { R(MatchStatus.Unclear, true), R(MatchStatus.Match), R(MatchStatus.Match), R(MatchStatus.Match) };

        Assert.Equal(55, RecommendationNode.ComputeScore(matched));
    }

    [Fact]
    public void A_partial_mandatory_requirement_caps_the_score_at_80()
    {
        var matched = new[] { R(MatchStatus.Partial, true), R(MatchStatus.Match), R(MatchStatus.Match), R(MatchStatus.Match), R(MatchStatus.Match) };

        Assert.Equal(80, RecommendationNode.ComputeScore(matched));
    }

    [Fact]
    public void The_lowest_cap_wins_when_several_apply()
    {
        var matched = new[] { R(MatchStatus.Partial, true), R(MatchStatus.Missing, true), R(MatchStatus.Match) };

        Assert.True(RecommendationNode.ComputeScore(matched) <= 35);
    }

    [Fact]
    public void Without_the_model_word_overlap_decides_match_partial_or_missing()
    {
        const string cv = "Prepared ledgers and reconciled bank statements in Tally.";

        Assert.Equal(MatchStatus.Match, MatchingNode.Deterministic("ledgers bank statements", cv).Status);
        Assert.Equal(MatchStatus.Partial, MatchingNode.Deterministic("ledgers payroll", cv).Status);
        Assert.Equal(MatchStatus.Missing, MatchingNode.Deterministic("welding payroll audit", cv).Status);
    }

    [Fact]
    public void A_requirement_made_only_of_filler_words_is_unclear()
    {
        Assert.Equal(MatchStatus.Unclear, MatchingNode.Deterministic("good knowledge of the", "anything").Status);
    }

    [Fact]
    public void Word_overlap_quotes_the_sentence_it_found_the_words_in()
    {
        var result = MatchingNode.Deterministic("tally", "Intro line here. Experienced with Tally for two years in practice.");

        Assert.Equal("Experienced with Tally for two years in practice", result.Evidence);
    }

    [Fact]
    public void Requirements_repeated_across_fields_are_counted_once_in_field_order()
    {
        var requirements = JobRequirements.FromModel(JsonNode.Parse("""
            {"mandatory": ["Excel", " SQL "], "technical_skills": ["excel", "Tally"], "preferred": ["SQL", "SAP"]}
            """)!.AsObject());

        Assert.Equal(["Excel", "SQL"], requirements.Mandatory);
        Assert.Equal(["SAP"], requirements.Preferred);
        Assert.Equal(["Tally"], requirements.TechnicalSkills);
        Assert.Equal(["Excel", "SQL", "SAP", "Tally"], requirements.AsFlatList());
    }

    [Fact]
    public void Structured_requirement_entries_are_written_out_as_text()
    {
        var requirements = JobRequirements.FromModel(JsonNode.Parse("""
            {"education": [{"degree": "BS", "field": "Accounting", "institution": "UoS"}, "ACCA"],
             "experience": [{"years": "2", "type": "relevant"}, {"years": "5+"}, {"title": "Manager", "level": "senior"}]}
            """)!.AsObject());

        Assert.Equal(["BS in Accounting (UoS)", "ACCA"], requirements.Education);
        Assert.Equal(["2 years (relevant)", "5+", "Manager senior"], requirements.Experience);
    }

    [Fact]
    public void An_education_entry_without_a_degree_is_refused_so_the_text_rules_take_over()
    {
        var json = JsonNode.Parse("""{"education": [{"institution": "UoS"}]}""")!.AsObject();

        Assert.Throws<FormatException>(() => ScreeningProfile.FromModel(json));
    }

    [Fact]
    public void A_bare_string_entry_is_kept_rather_than_lost()
    {
        var profile = ScreeningProfile.FromModel(JsonNode.Parse(
            """{"education": ["BS Accounting"], "experience": ["Evyol Test Company"], "skills": null}""")!.AsObject());

        Assert.Equal("BS Accounting", profile.Education.Single().Degree);
        Assert.Equal("Evyol Test Company", profile.Experience.Single().Company);
        Assert.Empty(profile.Skills);
    }

    [Fact]
    public void The_job_text_joins_title_description_and_listed_requirements()
    {
        var text = JobDescriptionBuilder.Build("Accounts Officer", "Prepare ledgers.", JsonNode.Parse("""
            {"skills": ["Excel", "Tally"], "must_have": "BS Accounting", "experience": "2 years", "education": ""}
            """));

        Assert.Equal(
            "Accounts Officer\n\nPrepare ledgers.\n\nRequirements:\n- Excel\n- Tally\n- BS Accounting\n- 2 years",
            text);
    }

    [Fact]
    public void A_job_with_only_a_title_gives_just_the_title()
    {
        Assert.Equal("Accounts Officer", JobDescriptionBuilder.Build("Accounts Officer", null, null));
    }
}
