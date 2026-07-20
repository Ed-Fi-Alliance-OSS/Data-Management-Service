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
        "NoProfile/AuthoritativeSmoke/SampleStudentAcademicRecord/RepeatPostAsUpdateNoOp",
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
        "MssqlRelationalWriteAuthoritativeDs52SurveySmokeTests.cs::Given_A_Mssql_Relational_Write_Propagated_Reference_Identity_Runtime_With_The_Authoritative_DS52_Survey_Fixture::It_should_keep_runtime_written_rows_participating_in_native_identity_cascades",
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
    public void It_covers_the_standalone_extension_child_delete_on_postgres_and_owes_sql_server_to_dms_1285()
    {
        ParityScenario row = _all.Single(s =>
            s.Id == "NoProfileChangedPutOmissionSemantics/DeletedStandaloneExtensionChildCollection"
        );

        row.PgsqlCoverage.Should().Be(EngineCoverage.Covered);
        row.PgsqlGapOwner.Should().BeNull();
        Flatten(row.PgsqlLocations)
            .Should()
            .BeEquivalentTo(
                (string[])
                    [
                        "PostgresqlRelationalWriteStandaloneExtensionChildDeleteTests.cs::Given_A_Postgresql_Changed_Put_Omitting_A_Standalone_Extension_Child_Collection::It_deletes_the_omitted_standalone_extension_child_collection_without_deleting_base_rows",
                    ]
            );

        row.MssqlCoverage.Should().Be(EngineCoverage.Gap);
        row.MssqlGapOwner.Should().Be("DMS-1285");
        row.Classification.Should().Be(ParityClassification.KnownGap);
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
    public void It_resolves_at_least_one_inherited_entry_point() =>
        _all.Any(s =>
                ParityEntryPointResolution.ResolveEffectiveEntryPoint(s)?.Kind == EntryPointKind.Inherited
            )
            .Should()
            .BeTrue();

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

    private static readonly string[] ExpectedNoProfileExtCreatePgTriples =
    [
        "PostgresqlRelationalWriteCreateBaselineTests.cs::Given_A_Postgresql_Relational_Write_Create_Baseline_With_A_Focused_Stable_Key_Fixture::It_persists_root_extensions_collection_extensions_and_extension_child_collections",
    ];

    private static readonly string[] ExpectedSiblingOrdinalPgTriples =
    [
        "PostgresqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children::It_assigns_aligned_extension_child_ordinals_in_new_request_order",
        "PostgresqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children::It_preserves_collection_item_ids_for_matched_aligned_extension_children_and_assigns_a_new_id_to_the_inserted_child",
        "PostgresqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_Postgresql_ProfileCollectionAlignedExtension_update_request_omitting_an_aligned_extension_child::It_recomputes_the_surviving_aligned_extension_child_ordinal",
    ];

    private static readonly string[] ExpectedSiblingOrdinalMssqlTriples =
    [
        "MssqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children::It_assigns_aligned_extension_child_ordinals_in_new_request_order",
        "MssqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_ProfileCollectionAlignedExtension_update_request_reordering_and_inserting_aligned_extension_children::It_preserves_collection_item_ids_for_matched_aligned_extension_children_and_assigns_a_new_id_to_the_inserted_child",
        "MssqlProfileCollectionAlignedExtensionMergeTests.cs::Given_a_ProfileCollectionAlignedExtension_update_request_omitting_an_aligned_extension_child::It_recomputes_the_surviving_aligned_extension_child_ordinal",
    ];

    [Test]
    public void It_records_the_no_profile_ext_create_entry_point()
    {
        ParityScenario row = _all.Single(s => s.Id == "NoProfileWriteBehavior/NoProfileExt");
        Flatten(row.PgsqlLocations).Should().BeEquivalentTo(ExpectedNoProfileExtCreatePgTriples);
    }

    private static readonly string[] ExpectedChangedUpdateBatchPartitionsPgTriples =
    [
        "PostgresqlRelationalWriteMultiBatchCollectionTests.cs::Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Changed_Descriptor_Update_With_A_Focused_Stable_Key_Fixture::It_returns_update_success_and_applies_the_changed_descriptor_to_every_row",
        "PostgresqlRelationalWriteMultiBatchCollectionTests.cs::Given_A_Postgresql_Relational_Write_Multi_Batch_Collection_Changed_Descriptor_Update_With_A_Focused_Stable_Key_Fixture::It_partitions_collection_update_commands_using_the_compiled_batch_limit",
    ];

    [Test]
    public void It_records_the_changed_update_batch_partitions_entry_point()
    {
        ParityScenario row = _all.Single(s =>
            s.Id == "NoProfileMultiBatchCollection/ChangedUpdateBatchPartitions"
        );
        row.Boundary.Should().Be(ProductionBoundary.BatchSqlEmitter);
        row.PgsqlCoverage.Should().Be(EngineCoverage.Covered);
        row.MssqlCoverage.Should().Be(EngineCoverage.Gap);
        row.MssqlGapOwner.Should().Be("DMS-1285");
        row.Classification.Should().Be(ParityClassification.KnownGap);
        Flatten(row.PgsqlLocations).Should().BeEquivalentTo(ExpectedChangedUpdateBatchPartitionsPgTriples);
        ParityEntryPointResolution
            .ResolveEffectiveEntryPoint(row)!
            .Kind.Should()
            .Be(EntryPointKind.Inherited);
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
        ParityScenarioCatalog
            .CanonicalIdOf("NoProfile/AuthoritativeSmoke/Ds52Contact/Create")
            .Should()
            .Be("NoProfile/AuthoritativeSmoke/Ds52Contact/Create");
    }
}
