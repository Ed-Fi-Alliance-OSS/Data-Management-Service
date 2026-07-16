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
        "ProfileVisibleRowUpdateWithHiddenRowPreservation",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/TopLevel",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/NoPreviouslyVisibleRows",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/InterleavedUpdatePlusInsert",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/NestedCollection",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/RootLevelExtensionChildCollection",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/CollectionAlignedExtensionChildCollection",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/HiddenDescriptorBinding",
        "ProfileVisibleRowUpdateWithHiddenRowPreservation/SiblingOrdinalRenumbering",
        "ProfileVisibleRowDeleteWithHiddenRowPreservation",
        "ProfileVisibleRowDeleteWithHiddenRowPreservation/DeleteOmittedVisible",
        "ProfileVisibleRowDeleteWithHiddenRowPreservation/DeleteAllVisibleWhileHiddenRemain",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/CollectionOrCommonTypeItem",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/NewVisible1To1Scope",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ExtensionScope",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/TwoLevelCreatableFalseChildrenRejected",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/NestedCommonTypeScope",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ExtensionCollectionItem",
        "ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable/ThreeLevelChain",
        "ProfileHiddenExtensionChildCollectionPreservation",
        "ProfileHiddenExtensionChildCollectionPreservation/CollectionAlignedExtensionHidden",
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
        "NoProfileChangedPutOmissionSemantics/DeletedStandaloneExtensionChildCollection",
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
        "NoProfilePostAsUpdate",
        "NoProfilePostAsUpdate/FocusedStableKey",
        "NoProfilePostAsUpdate/ImmutableIdentityRejected",
        "NoProfilePostAsUpdate/CreateRaceConvertedToUpdate",
        "NoProfilePostAsUpdate/AuthoritativeDs52SchoolYearType",
        "NoProfilePostAsUpdate/AuthoritativeStudentAcademicRecord",
        "NoProfileRollbackSafety",
        "NoProfileRollbackSafety/CreateFailureAfterEarlyWrites",
        "NoProfileRollbackSafety/KeyUnificationConflictRejectedAtomically",
        "NoProfile/AuthoritativeSmoke/Ds52Contact/Create",
        "NoProfile/AuthoritativeSmoke/Ds52Contact/ChangedPut",
        "NoProfile/AuthoritativeSmoke/Ds52Contact/RepeatPutNoOp",
        "NoProfile/AuthoritativeSmoke/Ds52School/Create",
        "NoProfile/AuthoritativeSmoke/Ds52School/ChangedPut",
        "NoProfile/AuthoritativeSmoke/Ds52School/RepeatPutNoOp",
        "NoProfile/AuthoritativeSmoke/SampleStudentEducationOrganizationAssociation/Create",
        "NoProfile/AuthoritativeSmoke/SampleStudentEducationOrganizationAssociation/ChangedPut",
        "NoProfile/AuthoritativeSmoke/SampleStudentEducationOrganizationAssociation/RepeatPutNoOp",
        "NoProfile/AuthoritativeSmoke/SampleStudentSchoolAssociation/Create",
        "NoProfile/AuthoritativeSmoke/SampleStudentSchoolAssociation/ChangedPut",
        "NoProfile/AuthoritativeSmoke/SampleStudentSchoolAssociation/RepeatPutNoOp",
        "NoProfile/AuthoritativeSmoke/SampleStudentSectionAssociation/Create",
        "NoProfile/AuthoritativeSmoke/SampleStudentSectionAssociation/ChangedPut",
        "NoProfile/AuthoritativeSmoke/SampleStudentSectionAssociation/RepeatPutNoOp",
        "NoProfile/AuthoritativeSmoke/SampleSurveyQuestion/Create",
        "NoProfile/AuthoritativeSmoke/SampleSurveyQuestion/ChangedPut",
        "NoProfile/AuthoritativeSmoke/SampleSurveyQuestion/RepeatPutNoOp",
        "NoProfile/ReferenceIdentityRuntime",
        "NoProfile/RelationalReadback",
        "NoProfile/RelationalReadback/ChangedPutEtag",
        "NoProfile/RelationalReadback/RepeatPutEtag",
    ];

    private static readonly string[] AuthoritativeFiles =
    [
        "PostgresqlRelationalWriteAuthoritativeDs52ContactSmokeTests.cs",
        "PostgresqlRelationalWriteAuthoritativeDs52SchoolSmokeTests.cs",
        "PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSectionAssociationSmokeTests.cs",
        "PostgresqlRelationalWriteAuthoritativeSampleSurveyQuestionSmokeTests.cs",
        "MssqlRelationalWriteAuthoritativeDs52SurveySmokeTests.cs",
        "MssqlRelationalWriteAuthoritativeSampleStudentArtProgramAssociationSmokeTests.cs",
        "MssqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs",
    ];

    // Independent (File::Fixture::Method) inventory of every public test method in the nine audited
    // authoritative smoke files. Catches a missing method, a wrong fixture, or a wrong file — which
    // exact scenario ids alone cannot.
    private static readonly string[] ExpectedAuthoritativeMethodInventory =
    [
        "PostgresqlRelationalWriteAuthoritativeDs52ContactSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_Contact_Fixture::It_persists_authoritative_ds52_contact_addresses_and_nested_address_periods_on_create",
        "PostgresqlRelationalWriteAuthoritativeDs52ContactSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_Contact_Fixture::It_reuses_stable_collection_item_ids_for_retained_addresses_and_nested_periods_on_changed_put",
        "PostgresqlRelationalWriteAuthoritativeDs52ContactSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_Contact_Fixture::It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put",
        "PostgresqlRelationalWriteAuthoritativeDs52SchoolSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_School_Fixture::It_persists_authoritative_ds52_school_root_and_collection_rows_on_create",
        "PostgresqlRelationalWriteAuthoritativeDs52SchoolSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_School_Fixture::It_reuses_stable_collection_item_ids_and_updates_ordinals_for_a_changed_put",
        "PostgresqlRelationalWriteAuthoritativeDs52SchoolSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Ds52_School_Fixture::It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put",
        "PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture::It_persists_authoritative_sample_base_root_and_extension_rows_on_create",
        "PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture::It_reuses_stable_collection_item_ids_and_updates_root_extension_data_for_a_changed_put",
        "PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture::It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put",
        "PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture::It_reads_back_the_written_document_via_relational_get_by_id_with_readable_profile_projection_for_collection_aligned_extensions",
        "PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs::Given_A_Postgresql_Relational_Write_Propagated_Reference_Identity_Cascade_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture::It_should_store_runtime_written_reference_identity_columns_in_all_or_none_shape",
        "PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs::Given_A_Postgresql_Relational_Write_Propagated_Reference_Identity_Cascade_With_The_Authoritative_Sample_StudentEducationOrganizationAssociation_Fixture::It_should_cascade_abstract_reference_identity_updates_into_runtime_written_reference_columns",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_extracts_descriptor_valued_collection_reference_members_from_concrete_paths_via_the_shared_document_info_helper",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_persists_authoritative_student_school_association_root_extension_and_child_rows_on_create",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_reuses_stable_collection_item_ids_and_updates_authoritative_state_on_changed_put",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_returns_the_create_etag_from_follow_up_get_by_id",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_returns_the_changed_put_etag_from_follow_up_get_by_id",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_returns_the_repeat_put_etag_from_follow_up_get_by_id",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_matches_ResourceLinks_IfMatch_against_the_current_relational_state",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_reads_back_the_written_document_via_relational_get_by_id_with_semantic_json_equivalence_and_metadata",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_reads_back_the_written_document_via_relational_get_by_id_with_readable_profile_projection",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Propagated_Reference_Identity_Runtime_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_populates_persisted_reference_identity_columns_on_create",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Propagated_Reference_Identity_Runtime_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_repopulates_persisted_reference_identity_columns_from_resolved_references_on_changed_put",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Key_Unification_Conflict_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_returns_a_validation_failure_and_leaves_document_and_authoritative_tables_unchanged",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSectionAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSectionAssociation_Fixture::It_persists_the_authoritative_sample_root_extension_and_extension_child_collection_rows_on_create",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSectionAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSectionAssociation_Fixture::It_reuses_stable_collection_item_ids_when_extension_children_are_reordered_removed_and_replaced",
        "PostgresqlRelationalWriteAuthoritativeSampleStudentSectionAssociationSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentSectionAssociation_Fixture::It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put",
        "PostgresqlRelationalWriteAuthoritativeSampleSurveyQuestionSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_SurveyQuestion_Fixture::It_persists_authoritative_sample_survey_question_root_and_child_rows_on_create",
        "PostgresqlRelationalWriteAuthoritativeSampleSurveyQuestionSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_SurveyQuestion_Fixture::It_reuses_stable_collection_item_ids_for_retained_matrices_and_response_choices_on_changed_put",
        "PostgresqlRelationalWriteAuthoritativeSampleSurveyQuestionSmokeTests.cs::Given_A_Postgresql_Relational_Write_Smoke_With_The_Authoritative_Sample_SurveyQuestion_Fixture::It_keeps_rowsets_and_content_version_unchanged_for_a_repeat_put",
        "MssqlRelationalWriteAuthoritativeDs52SurveySmokeTests.cs::Given_A_Mssql_Relational_Write_Propagated_Reference_Identity_Runtime_With_The_Authoritative_DS52_Survey_Fixture::It_populates_persisted_reference_identity_columns_on_create",
        "MssqlRelationalWriteAuthoritativeDs52SurveySmokeTests.cs::Given_A_Mssql_Relational_Write_Propagated_Reference_Identity_Runtime_With_The_Authoritative_DS52_Survey_Fixture::It_repopulates_persisted_reference_identity_columns_from_resolved_references_on_changed_put",
        "MssqlRelationalWriteAuthoritativeDs52SurveySmokeTests.cs::Given_A_Mssql_Relational_Write_Propagated_Reference_Identity_Runtime_With_The_Authoritative_DS52_Survey_Fixture::It_should_keep_runtime_written_rows_participating_in_identity_propagation_trigger_fallback",
        "MssqlRelationalWriteAuthoritativeSampleStudentArtProgramAssociationSmokeTests.cs::Given_A_Mssql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentArtProgramAssociation_Fixture::It_extracts_descriptor_backed_root_reference_members_via_the_shared_document_info_helper",
        "MssqlRelationalWriteAuthoritativeSampleStudentArtProgramAssociationSmokeTests.cs::Given_A_Mssql_Relational_Write_Smoke_With_The_Authoritative_Sample_StudentArtProgramAssociation_Fixture::It_populates_root_reference_columns_from_descriptor_backed_reference_members_on_create",
        "MssqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Mssql_Relational_Write_Then_Read_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_returns_the_create_etag_from_follow_up_get_by_id",
        "MssqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Mssql_Relational_Write_Then_Read_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_matches_ResourceLinks_IfMatch_against_the_current_relational_state",
        "MssqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Mssql_Relational_Write_Then_Read_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_reads_back_the_written_document_via_relational_get_by_id_with_semantic_json_equivalence_and_metadata",
        "MssqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs::Given_A_Mssql_Relational_Write_Then_Read_Smoke_With_The_Authoritative_Sample_StudentSchoolAssociation_Fixture::It_reads_back_the_written_document_via_relational_get_by_id_with_readable_profile_projection",
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
    public void It_catalogs_exactly_the_expected_authoritative_file_fixture_method_inventory()
    {
        List<string> actual = _all.SelectMany(s => s.PgsqlLocations.Concat(s.MssqlLocations))
            .Where(loc => AuthoritativeFiles.Contains(loc.File))
            .SelectMany(loc => loc.Methods.Select(m => $"{loc.File}::{loc.Fixture}::{m}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        actual.Should().BeEquivalentTo(ExpectedAuthoritativeMethodInventory);
    }

    [Test]
    public void It_maps_all_ten_dms_1022_api_behaviors_as_both_engine_covered() =>
        _all.Where(s => s.Layer == ParityLayer.Api)
            .Should()
            .OnlyContain(s => s.Classification == ParityClassification.Both);

    [Test]
    public void It_owns_every_canonical_no_profile_family_as_a_dms_1285_known_gap()
    {
        foreach (string id in ExpectedNoProfileCanonicalIds)
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
    public void It_splits_the_standalone_extension_child_gap_across_dms_1023_and_dms_1285()
    {
        _all.Should()
            .Contain(s =>
                s.Id == "NoProfileChangedPutOmissionSemantics/DeletedStandaloneExtensionChildCollection"
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
    public void It_records_reference_identity_and_readback_as_cross_engine_both_rows()
    {
        foreach (
            string id in (string[])["NoProfile/ReferenceIdentityRuntime", "NoProfile/RelationalReadback"]
        )
        {
            ParityScenario row = _all.Single(s => s.Id == id);
            row.Classification.Should().Be(ParityClassification.Both);
            row.PgsqlLocations.Should().NotBeEmpty();
            row.MssqlLocations.Should().NotBeEmpty();
        }
    }

    [Test]
    public void It_defers_the_seoa_changed_put_smoke_to_the_omission_semantics_family()
    {
        _all.Single(s =>
                s.Id
                == "NoProfile/AuthoritativeSmoke/SampleStudentEducationOrganizationAssociation/ChangedPut"
            )
            .CoveredByScenarioId.Should()
            .Be("NoProfileChangedPutOmissionSemantics");
    }

    [Test]
    public void It_records_the_multi_fixture_profile_variants_with_more_than_one_location()
    {
        foreach (
            string id in (string[])
                [
                    "ProfileVisibleRowUpdateWithHiddenRowPreservation/InterleavedUpdatePlusInsert",
                    "ProfileVisibleRowUpdateWithHiddenRowPreservation/RootLevelExtensionChildCollection",
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
        ParityScenarioCatalog
            .CanonicalIdOf("Api/CrudRoundTrip/CreatesAndReadsAStudent")
            .Should()
            .Be("Api/CrudRoundTrip/CreatesAndReadsAStudent");
        ParityScenarioCatalog
            .CanonicalIdOf("NoProfile/AuthoritativeSmoke/Ds52Contact/Create")
            .Should()
            .Be("NoProfile/AuthoritativeSmoke/Ds52Contact/Create");
    }
}
