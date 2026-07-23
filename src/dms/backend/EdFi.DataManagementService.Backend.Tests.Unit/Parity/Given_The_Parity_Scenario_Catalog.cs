// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Common.Parity;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Parity;

/// <summary>
/// Asserts the authoritative parity catalog is well-formed and complete. The expected id sets are
/// maintained here independently of the catalog so a collapsed or omitted variant fails the build.
/// </summary>
[TestFixture]
public class Given_The_Parity_Scenario_Catalog
{
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

    private static readonly string[] ExpectedApiIds =
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

    private static readonly string[] ExpectedProfileIds =
    [
        "ProfileRootCreateRejectedWhenNonCreatable",
        "ProfileHiddenInlinedColumnPreservation",
        "ProfileHiddenInlinedColumnPreservation/RootScopePreservedText",
        "ProfileHiddenInlinedColumnPreservation/HiddenMemberPathOnVisibleChild",
        "ProfileHiddenInlinedColumnPreservation/KeyUnifiedCanonicalStorage",
        "ProfileHiddenInlinedColumnPreservation/SyntheticPresenceFlag",
        "ProfileHiddenInlinedColumnPreservation/HiddenReferenceBinding",
        "ProfileVisibleButAbsentNonCollectionScope",
        "ProfileVisibleButAbsentNonCollectionScope/SeparateTable",
        "ProfileHiddenExtensionRowPreservation",
        "ProfileHiddenExtensionRowPreservation/WholeSeparateTableScope",
        "ProfileHiddenExtensionRowPreservation/HiddenDescriptorFkOnSeparateTable",
        "ProfileHiddenExtensionRowPreservation/CollectionAlignedExtensionHidden",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/TopLevel",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/NoPreviouslyVisibleRows",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/InterleavedUpdatePlusInsert",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/NestedCollection",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/RootLevelExtensionChildCollection",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/CollectionAlignedExtensionChildCollection",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/NestedExtensionChildCollection",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/HiddenDescriptorBinding",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/SiblingOrdinalRenumbering",
        "ProfileVisibleRowDeleteWithHiddenRowPreservation",
        "ProfileVisibleRowDeleteWithHiddenRowPreservation/DeleteOmittedVisible",
        "ProfileVisibleRowDeleteWithHiddenRowPreservation/DeleteAllVisibleWhileHiddenRemain",
        "ProfileVisibleRowDeleteWithHiddenRowPreservation/AlignedExtensionChildOmission",
        "ProfileVisibleRowDeleteWithHiddenRowPreservation/NestedExtensionChildOmission",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/CollectionOrCommonTypeItem",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/NewVisible1To1Scope",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ExtensionScope",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/TwoLevelCreatableFalseChildrenRejected",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/NestedCommonTypeScope",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ExtensionCollectionItem",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ThreeLevelChain",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ThreeLevelChainCreatabilityDerivation",
        "ProfileHiddenExtensionChildCollectionPreservation",
        "ProfileUnchangedWriteGuardedNoOp",
        "ProfileUnchangedWriteGuardedNoOp/RootOnlyPut",
        "ProfileUnchangedWriteGuardedNoOp/RootOnlyPostAsUpdate",
        "ProfileUnchangedWriteGuardedNoOp/StalePut",
        "ProfileUnchangedWriteGuardedNoOp/StalePostAsUpdate",
        "ProfileUnchangedWriteGuardedNoOp/SeparateTablePut",
        "ProfileUnchangedWriteGuardedNoOp/TopLevelCollectionPut",
        "ProfileUnchangedWriteGuardedNoOp/OrdinalAlignmentAcrossNoProfilePath",
    ];

    private static readonly string[] ExpectedNoProfileIds =
    [
        "NoProfileFullSurfaceCreate",
        "NoProfileFullSurfaceCreate/InsertSuccess",
        "NoProfileFullSurfaceCreate/RootAndNestedCollectionStableIds",
        "NoProfileFullSurfaceCreate/RootAndCollectionExtensionAndExtensionChild",
        "NoProfileChangedPutOmissionSemantics",
        "NoProfileChangedPutOmissionSemantics/ClearedInlinedColumn",
        "NoProfileChangedPutOmissionSemantics/DeletedAlignedExtensionScope",
        "NoProfileChangedPutOmissionSemantics/ContentVersionBump",
        "NoProfileChangedPutOmissionSemantics/DeletedBaseCollectionRows",
        "NoProfileChangedPutOmissionSemantics/DeletedAndReplacedChildCollectionRows",
        "NoProfileWriteBehavior",
        "NoProfileWriteBehavior/OmittedNonCollectionScope",
        "NoProfileWriteBehavior/NoProfileExt",
        "FullSurfaceCollectionReorder",
        "FullSurfaceCollectionReorder/OrdinalReuseStableIds",
        "FullSurfaceCollectionReorder/TwoRowSwapUnderSiblingUniqueness",
        "FullSurfaceCollectionReorder/ContentVersionBump",
        "NoProfileGuardedNoOp",
        "NoProfileGuardedNoOp/Put",
        "NoProfileGuardedNoOp/PostAsUpdate",
        "NoProfileGuardedNoOp/PutCurrentStateRefresh",
        "NoProfileGuardedNoOp/PostAsUpdateCurrentStateRefresh",
        "NoProfileGuardedNoOp/PutAfterReorder",
        "NoProfileGuardedNoOp/PostAsUpdateAfterReorder",
        "NoProfileGuardedNoOp/StalePut",
        "NoProfileGuardedNoOp/StalePostAsUpdate",
        "NoProfileGuardedNoOp/PutCommitWindowRace",
        "NoProfileGuardedNoOp/PostAsUpdateCommitWindowRace",
        "NoProfileMultiBatchCollection",
        "NoProfileMultiBatchCollection/Create",
        "NoProfileMultiBatchCollection/DeleteUpdate",
        "NoProfileMultiBatchCollection/AlignedExtensionCreate",
        "NoProfileMultiBatchCollection/AuthoritativeParameterPressure",
        "NoProfileMultiBatchCollection/AuthoritativeChangedPutIdentity",
        "NoProfileMultiBatchCollection/ChangedUpdateBatchPartitions",
        "NoProfilePostAsUpdate",
        "NoProfilePostAsUpdate/FocusedStableKey",
        "NoProfilePostAsUpdate/ImmutableIdentityRejected",
        "NoProfilePostAsUpdate/CreateRaceConvertedToUpdate",
        "NoProfilePostAsUpdate/AuthoritativeDs52SchoolYearType",
        "NoProfilePostAsUpdate/AuthoritativeStudentAcademicRecord",
        "NoProfileRollbackSafety",
        "NoProfileRollbackSafety/CreateFailureAfterEarlyWrites",
    ];

    private IReadOnlyList<ParityScenario> _all = null!;
    private IReadOnlyList<string> _violations = null!;

    [SetUp]
    public void Setup()
    {
        _all = ParityScenarioCatalog.All;
        _violations = ParityCatalogInvariants.Validate(_all, ParityScenarioCatalog.CanonicalNoProfileIds);
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
    public void It_contains_exactly_the_expected_api_scenario_ids() =>
        _all.Where(s => s.Layer == ParityLayer.Api).Select(s => s.Id).Should().BeEquivalentTo(ExpectedApiIds);

    [Test]
    public void It_contains_exactly_the_expected_profile_scenario_ids() =>
        _all.Where(s => s.Layer == ParityLayer.Profile)
            .Select(s => s.Id)
            .Should()
            .BeEquivalentTo(ExpectedProfileIds);

    [Test]
    public void It_contains_exactly_the_expected_no_profile_scenario_ids() =>
        _all.Where(s => s.Layer == ParityLayer.NoProfile)
            .Select(s => s.Id)
            .Should()
            .BeEquivalentTo(ExpectedNoProfileIds);

    [Test]
    public void It_maps_all_ten_dms_1022_api_behaviors_as_both_engine_covered() =>
        _all.Where(s => s.Layer == ParityLayer.Api)
            .Should()
            .OnlyContain(s => s.Classification == ParityClassification.Both);

    // The exact remaining owed SQL Server no-profile gap set after DMS-1285 Flip B: empty — every
    // no-profile row runs on both engines. Rows are selected by MssqlCoverage == Gap — not by owner —
    // so any future regression to Gap (or a blocker-owned row, whose (ScenarioId, owner) pair would
    // need to be enumerated here explicitly) fails this exact-set sweep rather than hiding behind an
    // owner filter (resolution validates only Covered rows).
    private static readonly string[] ExpectedRemainingNoProfileMssqlGapPairs = [];

    [Test]
    public void It_records_exactly_the_remaining_no_profile_mssql_gap_rows_as_scenario_and_owner_pairs() =>
        _all.Where(s => s.Layer == ParityLayer.NoProfile && s.MssqlCoverage == EngineCoverage.Gap)
            .Select(s => $"{s.Id}::{s.MssqlGapOwner}")
            .Should()
            .BeEquivalentTo(ExpectedRemainingNoProfileMssqlGapPairs);

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
    public void It_resolves_an_effective_entry_point_for_every_row() =>
        _all.Where(s => ParityEntryPointResolution.ResolveEffectiveEntryPoint(s) is null)
            .Select(s => s.Id)
            .Should()
            .BeEmpty();

    [Test]
    public void It_resolves_a_direct_shared_contract_for_a_no_profile_family() =>
        ParityEntryPointResolution
            .ResolveEffectiveEntryPoint(_all.Single(s => s.Id == "NoProfileFullSurfaceCreate"))!
            .Kind.Should()
            .Be(EntryPointKind.Direct);

    [Test]
    public void It_resolves_a_provider_specific_entry_point_for_a_profile_row() =>
        ParityEntryPointResolution
            .ResolveEffectiveEntryPoint(_all.Single(s => s.Id == "ProfileHiddenInlinedColumnPreservation"))!
            .Kind.Should()
            .Be(EntryPointKind.ProviderSpecific);

    [Test]
    public void It_resolves_direct_shared_contracts_for_profile_rows_backed_by_a_common_fixture()
    {
        foreach (
            (string id, string expectedContract) in (ValueTuple<string, string>[])
                [
                    (
                        "ProfileVisibleRowUpdateWithHiddenRowPreservation/CollectionAlignedExtensionChildCollection",
                        "ProfileCollectionAlignedExtensionScenarios.CreateProfileContext"
                    ),
                    (
                        "ProfileVisibleRowUpdateWithHiddenRowPreservation/NestedCollection",
                        "ProfileNestedCollectionScenarios.CreateProfileContext"
                            + " + ProfileNestedCollectionScenarios.AssertVisibleChildUpdatePreservesHiddenSiblingAndIdentities"
                    ),
                ]
        )
        {
            EffectiveEntryPoint effective = ParityEntryPointResolution.ResolveEffectiveEntryPoint(
                _all.Single(s => s.Id == id)
            )!;
            effective.Kind.Should().Be(EntryPointKind.Direct, "{0} delegates to a shared common fixture", id);
            effective.SharedValue.Should().Be(expectedContract);
        }
    }

    [Test]
    public void It_records_the_multi_fixture_profile_variants_with_more_than_one_location()
    {
        foreach (
            string id in (string[])
                [
                    "ProfileVisibleRowUpdateWithHiddenRowPreservation/InterleavedUpdatePlusInsert",
                    "ProfileVisibleRowUpdateWithHiddenRowPreservation/TopLevel",
                    "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/NewVisible1To1Scope",
                ]
        )
        {
            ParityScenario row = _all.Single(s => s.Id == id);
            row.PgsqlLocations.Length.Should().BeGreaterThan(1, "{0} names multiple PostgreSQL fixtures", id);
            row.MssqlLocations.Length.Should().BeGreaterThan(1, "{0} names multiple SQL Server fixtures", id);
        }
    }

    [Test]
    public void It_records_the_three_unit_level_creatability_variants_as_not_applicable()
    {
        foreach (
            string id in (string[])
                [
                    "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/NestedCommonTypeScope",
                    "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ExtensionCollectionItem",
                    "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ThreeLevelChain",
                ]
        )
        {
            ParityScenario row = _all.Single(s => s.Id == id);
            row.Classification.Should().Be(ParityClassification.Na);
            row.Boundary.Should().Be(ProductionBoundary.ProfileMergeSynthesizer);
            row.UnitLocations.Should().NotBeEmpty();
            row.UnitLocations.Should().OnlyContain(l => l.UnitOwner == UnitTestAssembly.BackendTestsUnit);
            row.PgsqlCoverage.Should().Be(EngineCoverage.NotApplicable);
            row.MssqlCoverage.Should().Be(EngineCoverage.NotApplicable);
        }
    }

    private static readonly string[] ExpectedThreeLevelChainDerivationUnitTriples =
    [
        "CreatabilityAnalyzerTests.cs::Given_Collection_Items_Under_Different_Parent_Instances::It_should_mark_alpha_collection_item_as_creatable",
        "CreatabilityAnalyzerTests.cs::Given_Collection_Items_Under_Different_Parent_Instances::It_should_mark_beta_collection_item_as_non_creatable",
        "CreatabilityAnalyzerTests.cs::Given_Collection_Items_Under_Different_Parent_Instances::It_should_emit_failure_for_beta_parent",
        "CreatabilityAnalyzerTests.cs::Given_Collection_Items_Under_Different_Parent_Instances::It_should_emit_failure_for_beta_collection_item",
    ];

    [Test]
    public void It_records_the_creatability_derivation_variant_against_the_core_analyzer_assembly()
    {
        ParityScenario row = _all.Single(s =>
            s.Id
            == "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ThreeLevelChainCreatabilityDerivation"
        );

        row.Classification.Should().Be(ParityClassification.Na);
        row.Boundary.Should().Be(ProductionBoundary.ProfileCreatabilityAnalysis);
        row.PgsqlCoverage.Should().Be(EngineCoverage.NotApplicable);
        row.MssqlCoverage.Should().Be(EngineCoverage.NotApplicable);
        row.UnitLocations.Should().ContainSingle();
        row.UnitLocations[0].UnitOwner.Should().Be(UnitTestAssembly.CoreTestsUnit);
        Flatten(row.UnitLocations).Should().BeEquivalentTo(ExpectedThreeLevelChainDerivationUnitTriples);
    }

    private static readonly string[] ExpectedNoProfileExtCreatePgTriples =
    [
        "PostgresqlRelationalWriteCreateBaselineTests.cs::Given_A_Postgresql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture::It_persists_root_extensions_collection_extensions_and_extension_child_collections",
    ];

    private static readonly string[] ExpectedNoProfileExtCreateMssqlTriples =
    [
        "MssqlRelationalWriteCreateBaselineTests.cs::Given_A_Mssql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture::It_persists_root_extensions_collection_extensions_and_extension_child_collections",
    ];

    private static readonly string[] ExpectedSiblingOrdinalPgTriples =
    [
        "PostgresqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children::It_returns_update_success",
        "PostgresqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children::It_yields_three_aligned_extension_child_rows_after_reorder_and_insert",
        "PostgresqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children::It_assigns_aligned_extension_child_ordinals_in_new_request_order",
        "PostgresqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children::It_preserves_collection_item_ids_for_matched_aligned_extension_children_and_assigns_a_new_id_to_the_inserted_child",
        "PostgresqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_nested_extension_children::It_returns_update_success",
        "PostgresqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_nested_extension_children::It_yields_three_nested_extension_child_rows_after_reorder_and_insert",
        "PostgresqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_nested_extension_children::It_assigns_nested_extension_child_ordinals_in_new_request_order",
        "PostgresqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_nested_extension_children::It_preserves_collection_item_ids_for_matched_nested_extension_children_and_assigns_a_new_id_to_the_inserted_child",
    ];

    private static readonly string[] ExpectedSiblingOrdinalMssqlTriples =
    [
        "MssqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children::It_returns_update_success",
        "MssqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children::It_yields_three_aligned_extension_child_rows_after_reorder_and_insert",
        "MssqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children::It_assigns_aligned_extension_child_ordinals_in_new_request_order",
        "MssqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children::It_preserves_collection_item_ids_for_matched_aligned_extension_children_and_assigns_a_new_id_to_the_inserted_child",
        "MssqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_nested_extension_children::It_returns_update_success",
        "MssqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_nested_extension_children::It_yields_three_nested_extension_child_rows_after_reorder_and_insert",
        "MssqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_nested_extension_children::It_assigns_nested_extension_child_ordinals_in_new_request_order",
        "MssqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_nested_extension_children::It_preserves_collection_item_ids_for_matched_nested_extension_children_and_assigns_a_new_id_to_the_inserted_child",
    ];

    [Test]
    public void It_records_the_no_profile_ext_create_entry_point()
    {
        ParityScenario row = _all.Single(s => s.Id == "NoProfileWriteBehavior/NoProfileExt");
        Flatten(row.PgsqlLocations).Should().BeEquivalentTo(ExpectedNoProfileExtCreatePgTriples);
        Flatten(row.MssqlLocations).Should().BeEquivalentTo(ExpectedNoProfileExtCreateMssqlTriples);
    }

    private static readonly string[] ExpectedChangedUpdateBatchPartitionsPgTriples =
    [
        "PostgresqlRelationalWriteMultiBatchCollectionTests.cs::Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Changed_Descriptor_Update_With_A_Focused_Stable_Key_Fixture::It_returns_update_success_and_applies_the_changed_descriptor_to_every_row",
        "PostgresqlRelationalWriteMultiBatchCollectionTests.cs::Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Changed_Descriptor_Update_With_A_Focused_Stable_Key_Fixture::It_partitions_collection_update_commands_using_the_compiled_batch_limit",
    ];

    private static readonly string[] ExpectedChangedUpdateBatchPartitionsMssqlTriples =
    [
        "MssqlRelationalWriteMultiBatchCollectionTests.cs::Given_A_Mssql_Relational_Write_Multi_Batch_Collection_Changed_Descriptor_Update_With_A_Focused_Stable_Key_Fixture::It_returns_update_success_and_applies_the_changed_descriptor_to_every_row",
        "MssqlRelationalWriteMultiBatchCollectionTests.cs::Given_A_Mssql_Relational_Write_Multi_Batch_Collection_Changed_Descriptor_Update_With_A_Focused_Stable_Key_Fixture::It_partitions_collection_update_commands_using_the_compiled_batch_limit",
    ];

    [Test]
    public void It_records_the_changed_update_batch_partitions_entry_point()
    {
        ParityScenario row = _all.Single(s =>
            s.Id == "NoProfileMultiBatchCollection/ChangedUpdateBatchPartitions"
        );
        row.Boundary.Should().Be(ProductionBoundary.BatchSqlEmitter);
        row.PgsqlCoverage.Should().Be(EngineCoverage.Covered);
        row.MssqlCoverage.Should().Be(EngineCoverage.Covered);
        row.MssqlGapOwner.Should().BeNull();
        row.Classification.Should().Be(ParityClassification.Both);
        Flatten(row.PgsqlLocations).Should().BeEquivalentTo(ExpectedChangedUpdateBatchPartitionsPgTriples);
        Flatten(row.MssqlLocations).Should().BeEquivalentTo(ExpectedChangedUpdateBatchPartitionsMssqlTriples);

        // The changed-update batch row runs update-specific helpers, not its family's create-batch contract, so
        // it names its own Direct entry point rather than inheriting the family contract by shared boundary.
        EffectiveEntryPoint resolved = ParityEntryPointResolution.ResolveEffectiveEntryPoint(row)!;
        resolved.Kind.Should().Be(EntryPointKind.Direct);
        resolved
            .SharedValue.Should()
            .Be(
                "NoProfileMultiBatchCollectionScenarios.AssertLargeCollectionChangedDescriptorUpdatePersisted"
                    + " + NoProfileMultiBatchCollectionScenarios.AssertUpdateBatchPartitions"
            );
    }

    [Test]
    public void It_records_the_commit_window_race_scheduling_dialect_difference()
    {
        // Phase 6 of DMS-1285 validated the commit-window race on SQL Server with unchanged shared
        // outcomes; the scheduling/blocking difference that required the redesigned twin choreography
        // is recorded on exactly these two rows.
        foreach (
            string id in (string[])
                [
                    "NoProfileGuardedNoOp/PutCommitWindowRace",
                    "NoProfileGuardedNoOp/PostAsUpdateCommitWindowRace",
                ]
        )
        {
            ParityScenario row = _all.Single(s => s.Id == id);
            row.DialectDifference.Should().NotBeNull("{0} pins the commit-window scheduling difference", id);
            row.DialectDifference!.Description.Should().Contain("X-lock");
            row.DialectDifference.Rationale.Should().Contain("[false, true]");
        }
    }

    [Test]
    public void It_records_accurate_dialect_metadata_on_the_multi_batch_rows()
    {
        // DialectDifference is row-local (variants do not inherit it), so every multi-batch row
        // whose compiled batch shape depends on dialect facts must record them itself.
        foreach (
            (string id, string expectedFragment) in (ValueTuple<string, string>[])
                [
                    ("NoProfileMultiBatchCollection", "generate_series"),
                    ("NoProfileMultiBatchCollection/Create", "generate_series"),
                    ("NoProfileMultiBatchCollection/AlignedExtensionCreate", "no id reservation"),
                    ("NoProfileMultiBatchCollection/ChangedUpdateBatchPartitions", "no id reservation"),
                ]
        )
        {
            ParityScenario row = _all.Single(s => s.Id == id);
            row.DialectDifference.Should().NotBeNull("{0} pins its dialect batch-shape facts", id);
            row.DialectDifference!.Description.Should().Contain("65535").And.Contain("2100");
            row.DialectDifference.Description.Should().Contain(expectedFragment);
        }
    }

    [Test]
    public void It_records_the_exact_sibling_ordinal_renumber_entry_points()
    {
        ParityScenario row = _all.Single(s =>
            s.Id == "ProfileVisibleRowUpdateWithHiddenRowPreservation/SiblingOrdinalRenumbering"
        );
        Flatten(row.PgsqlLocations).Should().BeEquivalentTo(ExpectedSiblingOrdinalPgTriples);
        Flatten(row.MssqlLocations).Should().BeEquivalentTo(ExpectedSiblingOrdinalMssqlTriples);
    }

    private static List<string> Flatten(IEnumerable<ScenarioLocation> locations) =>
        locations.SelectMany(l => l.Methods.Select(m => $"{l.File}::{l.Fixture}::{m}")).ToList();

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
        ParityScenarioCatalog
            .CanonicalIdOf("Api/CrudRoundTrip/CreatesAndReadsAStudent")
            .Should()
            .Be("Api/CrudRoundTrip/CreatesAndReadsAStudent");
    }
}
