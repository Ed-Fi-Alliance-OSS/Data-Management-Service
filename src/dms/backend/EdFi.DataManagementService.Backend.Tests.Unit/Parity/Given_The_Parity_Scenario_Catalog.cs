// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common.Parity;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Parity;

/// <summary>
/// Asserts the authoritative parity catalog is well-formed and complete: it satisfies every
/// structural invariant, carries the exact canonical identifier sets and the DMS-1022 API
/// behaviors, owns every DMS-1285 no-profile family as a SQL Server gap, catalogs all six
/// authoritative PostgreSQL smoke suites, and records the unit-level creatability variants.
/// </summary>
[TestFixture]
public class Given_The_Parity_Scenario_Catalog
{
    private static readonly string[] Dms1022ApiIds =
    [
        "Api/CrudRoundTrip/CreatesAndReadsAStudent",
        "Api/CrudRoundTrip/UpdatesAStudentViaPut",
        "Api/CrudRoundTrip/UpsertsAStudentViaPost",
        "Api/CrudRoundTrip/DeletesAStudent",
        "Api/CrudRoundTrip/PagesStudentsViaQuery",
        "Api/CrudRoundTrip/RejectsCreateWithMissingReference",
        "Api/CrudRoundTrip/RejectsDeleteWhenReferenced",
        "Api/ProfileRootOnlyMerge/CreatesAndReadsViaVisibleProfile",
        "Api/ProfileRootOnlyMerge/PreservesHiddenFieldOnProfiledPut",
        "Api/ProfileRootOnlyMerge/RejectsWriteAgainstReadOnlyProfile",
    ];

    private static readonly string[] AuthoritativeSmokeFiles =
    [
        "PostgresqlRelationalWriteAuthoritativeDs52ContactSmokeTests.cs",
        "PostgresqlRelationalWriteAuthoritativeDs52SchoolSmokeTests.cs",
        "PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSectionAssociationSmokeTests.cs",
        "PostgresqlRelationalWriteAuthoritativeSampleSurveyQuestionSmokeTests.cs",
    ];

    private static readonly string[] UnitLevelCreatabilityVariantIds =
    [
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/NestedCommonTypeScope",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ExtensionCollectionItem",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ThreeLevelChain",
    ];

    private IReadOnlyList<ParityScenario> _all = null!;
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        _all = ParityScenarioCatalog.All;
        _violations = ParityCatalogInvariants.Validate(_all);
    }

    [Test]
    public void It_satisfies_every_structural_invariant() => _violations.Should().BeEmpty();

    [Test]
    public void It_has_no_duplicate_scenario_ids() => _all.Select(s => s.Id).Should().OnlyHaveUniqueItems();

    [Test]
    public void It_defines_exactly_nine_canonical_profile_ids() =>
        ParityScenarioCatalog.CanonicalProfileIds.Length.Should().Be(9);

    [Test]
    public void It_defines_exactly_eight_canonical_no_profile_ids() =>
        ParityScenarioCatalog.CanonicalNoProfileIds.Length.Should().Be(8);

    [Test]
    public void It_contains_every_canonical_profile_id_in_the_profile_layer()
    {
        foreach (string id in ParityScenarioCatalog.CanonicalProfileIds)
        {
            _all.Should()
                .Contain(s => s.Id == id && s.Layer == ParityLayer.Profile, "canonical profile id {0}", id);
        }
    }

    [Test]
    public void It_contains_every_canonical_no_profile_id_in_the_no_profile_layer()
    {
        foreach (string id in ParityScenarioCatalog.CanonicalNoProfileIds)
        {
            _all.Should()
                .Contain(
                    s => s.Id == id && s.Layer == ParityLayer.NoProfile,
                    "canonical no-profile id {0}",
                    id
                );
        }
    }

    [Test]
    public void It_keeps_the_two_original_matrix_rows_in_the_no_profile_layer()
    {
        _all.Should().Contain(s => s.Id == "NoProfileWriteBehavior" && s.Layer == ParityLayer.NoProfile);
        _all.Should()
            .Contain(s => s.Id == "FullSurfaceCollectionReorder" && s.Layer == ParityLayer.NoProfile);
    }

    [Test]
    public void It_maps_all_ten_dms_1022_api_behaviors_as_both_engine_covered()
    {
        foreach (string id in Dms1022ApiIds)
        {
            _all.Should()
                .Contain(
                    s =>
                        s.Id == id
                        && s.Layer == ParityLayer.Api
                        && s.Classification == ParityClassification.Both,
                    "DMS-1022 API behavior {0}",
                    id
                );
        }
    }

    [Test]
    public void It_owns_every_canonical_no_profile_family_as_a_dms_1285_known_gap()
    {
        foreach (string id in ParityScenarioCatalog.CanonicalNoProfileIds)
        {
            _all.Should()
                .Contain(
                    s =>
                        s.Id == id
                        && s.Classification == ParityClassification.KnownGap
                        && s.GapOwner == "DMS-1285"
                        && s.MssqlCoverage == EngineCoverage.Gap,
                    "no-profile family {0} owed to DMS-1285",
                    id
                );
        }
    }

    [Test]
    public void It_catalogs_all_six_authoritative_postgresql_smoke_suites()
    {
        foreach (string file in AuthoritativeSmokeFiles)
        {
            _all.Should()
                .Contain(s => s.Pgsql != null && s.Pgsql.File == file, "authoritative smoke suite {0}", file);
        }
    }

    [Test]
    public void It_marks_the_key_unification_conflict_as_a_dms_1285_known_gap()
    {
        _all.Should()
            .Contain(s =>
                s.Id == "NoProfileRollbackSafety/KeyUnificationConflictRejectedAtomically"
                && s.Classification == ParityClassification.KnownGap
                && s.GapOwner == "DMS-1285"
                && s.Boundary == ProductionBoundary.KeyUnificationValidation
            );
    }

    [Test]
    public void It_records_the_three_unit_level_creatability_variants_as_not_applicable()
    {
        foreach (string id in UnitLevelCreatabilityVariantIds)
        {
            _all.Should()
                .Contain(
                    s =>
                        s.Id == id
                        && s.Classification == ParityClassification.Na
                        && s.PgsqlCoverage == EngineCoverage.NotApplicable
                        && s.MssqlCoverage == EngineCoverage.NotApplicable,
                    "unit-level creatability variant {0}",
                    id
                );
        }
    }

    [Test]
    public void It_defers_every_supporting_smoke_to_a_same_layer_no_profile_scenario()
    {
        var byId = _all.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (
            ParityScenario smoke in _all.Where(s => s.Classification == ParityClassification.SupportingSmoke)
        )
        {
            smoke.CoveredByScenarioId.Should().NotBeNullOrEmpty();
            byId.Should().ContainKey(smoke.CoveredByScenarioId!);
            byId[smoke.CoveredByScenarioId!].Layer.Should().Be(ParityLayer.NoProfile);
        }
    }
}
