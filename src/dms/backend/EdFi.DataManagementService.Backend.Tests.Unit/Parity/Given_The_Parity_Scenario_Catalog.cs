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

    // Independent expected sets: a typo made consistently in the catalog's own arrays and rows
    // would still fail these, unlike asserting the catalog arrays against themselves.
    private static readonly string[] ExpectedProfileCanonicalIds =
    [
        "ProfileVisibleRowUpdateWithHiddenRowPreservation",
        "ProfileVisibleRowDeleteWithHiddenRowPreservation",
        "ProfileVisibleButAbsentNonCollectionScope",
        "ProfileHiddenInlinedColumnPreservation",
        "ProfileRootCreateRejectedWhenNonCreatable",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable",
        "ProfileHiddenExtensionRowPreservation",
        "ProfileHiddenExtensionChildCollectionPreservation",
        "ProfileUnchangedWriteGuardedNoOp",
    ];

    private static readonly string[] ExpectedNoProfileCanonicalIds =
    [
        "NoProfileWriteBehavior",
        "FullSurfaceCollectionReorder",
        "NoProfileFullSurfaceCreate",
        "NoProfileChangedPutOmissionSemantics",
        "NoProfileGuardedNoOp",
        "NoProfileMultiBatchCollection",
        "NoProfilePostAsUpdate",
        "NoProfileRollbackSafety",
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
    public void It_defines_exactly_the_expected_nine_canonical_profile_ids() =>
        ParityScenarioCatalog.CanonicalProfileIds.Should().BeEquivalentTo(ExpectedProfileCanonicalIds);

    [Test]
    public void It_defines_exactly_the_expected_eight_canonical_no_profile_ids() =>
        ParityScenarioCatalog.CanonicalNoProfileIds.Should().BeEquivalentTo(ExpectedNoProfileCanonicalIds);

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
                        && s.MssqlGapOwner == "DMS-1285"
                        && s.MssqlCoverage == EngineCoverage.Gap,
                    "no-profile family {0} owed to DMS-1285",
                    id
                );
        }
    }

    [Test]
    public void It_catalogs_all_six_authoritative_postgresql_smoke_suites()
    {
        List<string> pgSmokeFiles = _all.Where(s => s.Pgsql is not null).Select(s => s.Pgsql!.File).ToList();
        foreach (string file in AuthoritativeSmokeFiles)
        {
            pgSmokeFiles.Should().Contain(file, "authoritative smoke suite {0}", file);
        }
    }

    [Test]
    public void It_marks_the_key_unification_conflict_as_a_dms_1285_known_gap()
    {
        _all.Should()
            .Contain(s =>
                s.Id == "NoProfileRollbackSafety/KeyUnificationConflictRejectedAtomically"
                && s.Classification == ParityClassification.KnownGap
                && s.MssqlGapOwner == "DMS-1285"
                && s.Boundary == ProductionBoundary.KeyUnificationValidation
            );
    }

    [Test]
    public void It_splits_the_standalone_extension_child_gap_across_dms_1023_and_dms_1285()
    {
        _all.Should()
            .Contain(s =>
                s.Id == "NoProfileChangedPutOmissionSemantics/DeletedStandaloneExtensionChildCollection"
                && s.Classification == ParityClassification.KnownGap
                && s.PgsqlCoverage == EngineCoverage.Gap
                && s.PgsqlGapOwner == "DMS-1023"
                && s.MssqlCoverage == EngineCoverage.Gap
                && s.MssqlGapOwner == "DMS-1285"
            );
    }

    [Test]
    public void It_gives_every_no_profile_canonical_family_a_shared_entry_point()
    {
        foreach (string id in ExpectedNoProfileCanonicalIds)
        {
            _all.Should()
                .Contain(
                    s => s.Id == id && !string.IsNullOrWhiteSpace(s.SharedEntryPoint),
                    "no-profile family {0} names its shared contract entry point",
                    id
                );
        }
    }

    [Test]
    public void It_records_the_three_unit_level_creatability_variants_as_not_applicable()
    {
        foreach (string id in UnitLevelCreatabilityVariantIds)
        {
            ParityScenario row = _all.Single(s => s.Id == id);
            row.Classification.Should().Be(ParityClassification.Na);
            row.Boundary.Should().Be(ProductionBoundary.ProfileMergeSynthesizer);
            row.Unit.Should().NotBeNull();
            row.PgsqlCoverage.Should().Be(EngineCoverage.NotApplicable);
            row.MssqlCoverage.Should().Be(EngineCoverage.NotApplicable);
        }
    }

    [Test]
    public void It_derives_canonical_ids_by_matching_approved_prefixes()
    {
        ParityScenarioCatalog
            .CanonicalIdOf("ProfileVisibleRowUpdateWithHiddenRowPreservation/NestedCollection")
            .Should()
            .Be("ProfileVisibleRowUpdateWithHiddenRowPreservation");
        ParityScenarioCatalog
            .CanonicalIdOf("NoProfileGuardedNoOp/StalePut")
            .Should()
            .Be("NoProfileGuardedNoOp");
        // API and supporting-smoke ids use slashes as namespace segments, so they are unchanged.
        ParityScenarioCatalog
            .CanonicalIdOf("Api/CrudRoundTrip/CreatesAndReadsAStudent")
            .Should()
            .Be("Api/CrudRoundTrip/CreatesAndReadsAStudent");
        ParityScenarioCatalog
            .CanonicalIdOf("NoProfile/AuthoritativeSmoke/Ds52Contact/Create")
            .Should()
            .Be("NoProfile/AuthoritativeSmoke/Ds52Contact/Create");
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
