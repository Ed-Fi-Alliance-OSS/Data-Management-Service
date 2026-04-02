// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class ProfileWritePipelineTests
{
    // -----------------------------------------------------------------------
    //  Shared constants
    // -----------------------------------------------------------------------

    protected const string ProfileName = "TestProfile";
    protected const string ResourceName = "StudentSchoolAssociation";
    protected const string Method = "POST";
    protected const string Operation = "write";

    // Shared scope catalog
    protected static IReadOnlyList<CompiledScopeDescriptor> SharedFixtureScopes =>
        ProfileTestFixtures.SharedFixtureScopes;

    /// <summary>
    /// Standard request body for a StudentSchoolAssociation with all fields.
    /// </summary>
    protected static JsonNode BuildStandardRequestBody() =>
        JsonNode.Parse(
            """
            {
                "studentReference": { "studentUniqueId": "S001" },
                "schoolReference": { "schoolId": 100 },
                "entryDate": "2024-08-01",
                "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Original",
                "calendarReference": { "calendarCode": "2024-01" },
                "classPeriods": [
                    { "classPeriodName": "Period1", "officialAttendancePeriod": true }
                ]
            }
            """
        )!;

    /// <summary>
    /// Standard effective schema-required members for the shared fixture.
    /// </summary>
    protected static IReadOnlyDictionary<string, IReadOnlyList<string>> StandardRequiredMembers =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["$"] = ["studentReference", "schoolReference", "entryDate"],
        };

    // -----------------------------------------------------------------------
    //  1. Given_No_Profile — no-profile passthrough
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_No_Profile : ProfileWritePipelineTests
    {
        private ProfileWritePipelineResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ProfileWritePipeline.Execute(
                canonicalizedRequestBody: BuildStandardRequestBody(),
                writeContentType: null,
                resolvedContentType: null,
                scopeCatalog: SharedFixtureScopes,
                storedDocument: null,
                isCreate: true,
                profileName: ProfileName,
                resourceName: ResourceName,
                method: Method,
                operation: Operation,
                effectiveSchemaRequiredMembersByScope: StandardRequiredMembers
            );
        }

        [Test]
        public void It_should_not_have_a_profile()
        {
            _result.HasProfile.Should().BeFalse();
        }

        [Test]
        public void It_should_have_no_request()
        {
            _result.Request.Should().BeNull();
        }

        [Test]
        public void It_should_have_no_failures()
        {
            _result.Failures.Should().BeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  2. Given_Profile_Mode_Mismatch — category-2 error
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Profile_Mode_Mismatch : ProfileWritePipelineTests
    {
        private ProfileWritePipelineResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ProfileWritePipeline.Execute(
                canonicalizedRequestBody: BuildStandardRequestBody(),
                writeContentType: ProfileTestFixtures.BuildIncludeAllProfile(),
                resolvedContentType: ProfileContentType.Read,
                scopeCatalog: SharedFixtureScopes,
                storedDocument: null,
                isCreate: true,
                profileName: ProfileName,
                resourceName: ResourceName,
                method: Method,
                operation: Operation,
                effectiveSchemaRequiredMembersByScope: StandardRequiredMembers
            );
        }

        [Test]
        public void It_should_have_a_profile()
        {
            _result.HasProfile.Should().BeTrue();
        }

        [Test]
        public void It_should_not_succeed()
        {
            _result.IsSuccess.Should().BeFalse();
        }

        [Test]
        public void It_should_have_exactly_one_failure()
        {
            _result.Failures.Should().HaveCount(1);
        }

        [Test]
        public void It_should_be_a_category_2_invalid_profile_usage_failure()
        {
            _result.Failures[0].Should().BeOfType<ProfileModeMismatchProfileUsageFailure>();
            _result.Failures[0].Category.Should().Be(ProfileFailureCategory.InvalidProfileUsage);
        }
    }

    // -----------------------------------------------------------------------
    //  3. Given_Read_Only_Profile_With_Null_WriteContentType — category-2 error
    //     even when writeContentType is null (read-only profile on write op)
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Read_Only_Profile_With_Null_WriteContentType : ProfileWritePipelineTests
    {
        private ProfileWritePipelineResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ProfileWritePipeline.Execute(
                canonicalizedRequestBody: BuildStandardRequestBody(),
                writeContentType: null,
                resolvedContentType: ProfileContentType.Read,
                scopeCatalog: SharedFixtureScopes,
                storedDocument: null,
                isCreate: true,
                profileName: ProfileName,
                resourceName: ResourceName,
                method: Method,
                operation: Operation,
                effectiveSchemaRequiredMembersByScope: StandardRequiredMembers
            );
        }

        [Test]
        public void It_should_have_a_profile()
        {
            _result.HasProfile.Should().BeTrue();
        }

        [Test]
        public void It_should_not_succeed()
        {
            _result.IsSuccess.Should().BeFalse();
        }

        [Test]
        public void It_should_have_exactly_one_failure()
        {
            _result.Failures.Should().HaveCount(1);
        }

        [Test]
        public void It_should_be_a_category_2_invalid_profile_usage_failure()
        {
            _result.Failures[0].Should().BeOfType<ProfileModeMismatchProfileUsageFailure>();
            _result.Failures[0].Category.Should().Be(ProfileFailureCategory.InvalidProfileUsage);
        }
    }

    // -----------------------------------------------------------------------
    //  4. Given_Valid_Create_With_All_Required_Visible — full pipeline success
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Valid_Create_With_All_Required_Visible : ProfileWritePipelineTests
    {
        private ProfileWritePipelineResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ProfileWritePipeline.Execute(
                canonicalizedRequestBody: BuildStandardRequestBody(),
                writeContentType: ProfileTestFixtures.BuildIncludeAllProfile(),
                resolvedContentType: ProfileContentType.Write,
                scopeCatalog: SharedFixtureScopes,
                storedDocument: null,
                isCreate: true,
                profileName: ProfileName,
                resourceName: ResourceName,
                method: Method,
                operation: Operation,
                effectiveSchemaRequiredMembersByScope: StandardRequiredMembers
            );
        }

        [Test]
        public void It_should_succeed()
        {
            _result.IsSuccess.Should().BeTrue();
        }

        [Test]
        public void It_should_have_a_request()
        {
            _result.Request.Should().NotBeNull();
        }

        [Test]
        public void It_should_have_a_writable_request_body()
        {
            _result.Request!.WritableRequestBody.Should().NotBeNull();
        }

        [Test]
        public void It_should_have_root_resource_creatable()
        {
            _result.Request!.RootResourceCreatable.Should().BeTrue();
        }

        [Test]
        public void It_should_have_non_empty_request_scope_states()
        {
            _result.Request!.RequestScopeStates.Should().NotBeEmpty();
        }

        [Test]
        public void It_should_have_non_empty_visible_request_collection_items()
        {
            _result.Request!.VisibleRequestCollectionItems.Should().NotBeEmpty();
        }

        [Test]
        public void It_should_have_no_context_for_create_flow()
        {
            _result.Context.Should().BeNull();
        }
    }

    // -----------------------------------------------------------------------
    //  4. Given_Update_With_Stored_Document — existence affects creatability
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Update_With_Stored_Document : ProfileWritePipelineTests
    {
        private ProfileWritePipelineResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // Same shape as request body, representing the existing stored document
            JsonNode storedDocument = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Original",
                    "calendarReference": { "calendarCode": "2024-01" },
                    "classPeriods": [
                        { "classPeriodName": "Period1", "officialAttendancePeriod": true }
                    ]
                }
                """
            )!;

            _result = ProfileWritePipeline.Execute(
                canonicalizedRequestBody: BuildStandardRequestBody(),
                writeContentType: ProfileTestFixtures.BuildIncludeAllProfile(),
                resolvedContentType: ProfileContentType.Write,
                scopeCatalog: SharedFixtureScopes,
                storedDocument: storedDocument,
                isCreate: false,
                profileName: ProfileName,
                resourceName: ResourceName,
                method: "PUT",
                operation: "write",
                effectiveSchemaRequiredMembersByScope: StandardRequiredMembers
            );
        }

        [Test]
        public void It_should_succeed()
        {
            _result.IsSuccess.Should().BeTrue();
        }

        [Test]
        public void It_should_have_root_resource_not_creatable_for_update()
        {
            _result.Request!.RootResourceCreatable.Should().BeFalse();
        }

        [Test]
        public void It_should_have_no_creatability_failures()
        {
            _result.Failures.Should().BeEmpty();
        }

        [Test]
        public void It_should_have_context_for_update_flow()
        {
            _result.Context.Should().NotBeNull();
        }

        [Test]
        public void It_should_have_visible_stored_body_with_all_fields()
        {
            // IncludeAll profile: stored body includes everything
            _result.Context!.VisibleStoredBody["entryDate"]!
                .GetValue<string>()
                .Should()
                .Be("2024-08-01");
        }

        [Test]
        public void It_should_have_stored_scope_states()
        {
            _result.Context!.StoredScopeStates.Should().NotBeEmpty();
        }

        [Test]
        public void It_should_have_visible_stored_collection_rows()
        {
            _result.Context!.VisibleStoredCollectionRows.Should().NotBeEmpty();
        }
    }

    // -----------------------------------------------------------------------
    //  4b. Given_Update_With_Filtering_Profile — end-to-end C6 with hidden members
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Update_With_Filtering_Profile : ProfileWritePipelineTests
    {
        private ProfileWritePipelineResult _result = null!;

        /// <summary>
        /// IncludeOnly profile: root includes studentReference, schoolReference only.
        /// classPeriods includes classPeriodName only (officialAttendancePeriod is hidden).
        /// entryDate, entryTypeDescriptor, calendarReference are hidden.
        /// </summary>
        private static ContentTypeDefinition BuildFilteringProfile() =>
            new(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("studentReference"), new PropertyRule("schoolReference")],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "classPeriods",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("classPeriodName")],
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                ],
                Extensions: []
            );

        [SetUp]
        public void Setup()
        {
            // Request body only contains visible fields (no forbidden data)
            JsonNode requestBody = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "classPeriods": [
                        { "classPeriodName": "Period1" }
                    ]
                }
                """
            )!;

            // Stored document has all fields including hidden ones
            JsonNode storedDocument = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "entryDate": "2024-08-01",
                    "entryTypeDescriptor": "uri://ed-fi.org/EntryType#Original",
                    "calendarReference": { "calendarCode": "2024-01" },
                    "classPeriods": [
                        { "classPeriodName": "Period1", "officialAttendancePeriod": true }
                    ]
                }
                """
            )!;

            _result = ProfileWritePipeline.Execute(
                canonicalizedRequestBody: requestBody,
                writeContentType: BuildFilteringProfile(),
                resolvedContentType: ProfileContentType.Write,
                scopeCatalog: SharedFixtureScopes,
                storedDocument: storedDocument,
                isCreate: false,
                profileName: ProfileName,
                resourceName: ResourceName,
                method: "PUT",
                operation: "write",
                effectiveSchemaRequiredMembersByScope: new Dictionary<string, IReadOnlyList<string>>
                {
                    ["$"] = ["studentReference", "schoolReference"],
                }
            );
        }

        [Test]
        public void It_should_succeed()
        {
            _result.IsSuccess.Should().BeTrue();
        }

        [Test]
        public void It_should_have_context()
        {
            _result.Context.Should().NotBeNull();
        }

        [Test]
        public void It_should_have_visible_stored_body_with_only_visible_members()
        {
            _result.Context!.VisibleStoredBody["studentReference"].Should().NotBeNull();
            _result.Context!.VisibleStoredBody["schoolReference"].Should().NotBeNull();
        }

        [Test]
        public void It_should_strip_hidden_members_from_visible_stored_body()
        {
            _result.Context!.VisibleStoredBody["entryDate"].Should().BeNull();
            _result.Context!.VisibleStoredBody["entryTypeDescriptor"].Should().BeNull();
            _result.Context!.VisibleStoredBody["calendarReference"].Should().BeNull();
        }

        [Test]
        public void It_should_strip_hidden_collection_item_members()
        {
            var item = _result.Context!.VisibleStoredBody["classPeriods"]!.AsArray()[0]!;
            item["classPeriodName"]!.GetValue<string>().Should().Be("Period1");
            item["officialAttendancePeriod"].Should().BeNull();
        }

        [Test]
        public void It_should_have_stored_scope_states_from_existence_lookup()
        {
            _result.Context!.StoredScopeStates.Should().NotBeEmpty();
        }

        [Test]
        public void It_should_have_visible_stored_collection_rows_from_existence_lookup()
        {
            _result.Context!.VisibleStoredCollectionRows.Should().NotBeEmpty();
        }

        [Test]
        public void It_should_include_hidden_collection_member_in_hidden_member_paths()
        {
            // officialAttendancePeriod is hidden by the IncludeOnly profile (only classPeriodName is included)
            _result
                .Context!.VisibleStoredCollectionRows.Should()
                .ContainSingle()
                .Which.HiddenMemberPaths.Should()
                .Contain("officialAttendancePeriod");
        }

        [Test]
        public void It_should_include_hidden_root_members_in_stored_scope_state_hidden_paths()
        {
            // entryDate, entryTypeDescriptor, calendarReference are all hidden at root scope
            var rootScope = _result.Context!.StoredScopeStates.First(s => s.Address.JsonScope == "$");
            rootScope.HiddenMemberPaths.Should().Contain("entryDate");
            rootScope.HiddenMemberPaths.Should().Contain("entryTypeDescriptor");
        }
    }

    // -----------------------------------------------------------------------
    //  5. Given_Create_With_Hidden_Required_Root_Member — C3 forbidden data
    //     short-circuits before C4 creatability
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Create_With_Hidden_Required_Root_Member : ProfileWritePipelineTests
    {
        private ProfileWritePipelineResult _result = null!;

        /// <summary>
        /// IncludeOnly profile that explicitly hides entryDate by not listing it.
        /// Only studentReference and schoolReference are included.
        /// </summary>
        private static ContentTypeDefinition BuildProfileHidingEntryDate() =>
            new(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("studentReference"), new PropertyRule("schoolReference")],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "classPeriods",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("classPeriodName")],
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                ],
                Extensions: []
            );

        [SetUp]
        public void Setup()
        {
            // Request body includes entryDate, which is hidden by the profile.
            // The pipeline short-circuits at C3 because forbidden data is submitted.
            _result = ProfileWritePipeline.Execute(
                canonicalizedRequestBody: BuildStandardRequestBody(),
                writeContentType: BuildProfileHidingEntryDate(),
                resolvedContentType: ProfileContentType.Write,
                scopeCatalog: SharedFixtureScopes,
                storedDocument: null,
                isCreate: true,
                profileName: ProfileName,
                resourceName: ResourceName,
                method: Method,
                operation: Operation,
                effectiveSchemaRequiredMembersByScope: StandardRequiredMembers
            );
        }

        [Test]
        public void It_should_not_succeed()
        {
            _result.IsSuccess.Should().BeFalse();
        }

        [Test]
        public void It_should_have_failures()
        {
            _result.Failures.Should().NotBeEmpty();
        }

        [Test]
        public void It_should_contain_forbidden_submitted_data_failures()
        {
            // The pipeline short-circuits at C3 with ForbiddenSubmittedData failures
            // because entryDate and entryTypeDescriptor are submitted but hidden by the profile.
            _result
                .Failures.OfType<ForbiddenSubmittedDataWritableProfileValidationFailure>()
                .Should()
                .NotBeEmpty();
        }

        [Test]
        public void It_should_be_category_3_writable_profile_validation_failures()
        {
            _result
                .Failures.Should()
                .AllSatisfy(f =>
                    f.Category.Should().Be(ProfileFailureCategory.WritableProfileValidationFailure)
                );
        }
    }

    // -----------------------------------------------------------------------
    //  5b. Given_Create_With_Hidden_Required_Root_Member_Not_Submitted —
    //      C4 creatability violation when hidden required member is not submitted
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Create_With_Hidden_Required_Root_Member_Not_Submitted : ProfileWritePipelineTests
    {
        private ProfileWritePipelineResult _result = null!;

        /// <summary>
        /// IncludeOnly profile that hides entryDate. Includes calendarReference and classPeriods.
        /// </summary>
        private static ContentTypeDefinition BuildProfileHidingEntryDate() =>
            new(
                MemberSelection: MemberSelection.IncludeOnly,
                Properties: [new PropertyRule("studentReference"), new PropertyRule("schoolReference")],
                Objects: [],
                Collections:
                [
                    new CollectionRule(
                        Name: "classPeriods",
                        MemberSelection: MemberSelection.IncludeOnly,
                        LogicalSchema: null,
                        Properties: [new PropertyRule("classPeriodName")],
                        NestedObjects: null,
                        NestedCollections: null,
                        Extensions: null,
                        ItemFilter: null
                    ),
                ],
                Extensions: []
            );

        [SetUp]
        public void Setup()
        {
            // Request body does NOT include entryDate (the hidden required member).
            // C3 passes (nothing forbidden submitted), but C4 detects that the root
            // scope cannot be created because entryDate is hidden and required.
            JsonNode requestBody = JsonNode.Parse(
                """
                {
                    "studentReference": { "studentUniqueId": "S001" },
                    "schoolReference": { "schoolId": 100 },
                    "classPeriods": [
                        { "classPeriodName": "Period1" }
                    ]
                }
                """
            )!;

            _result = ProfileWritePipeline.Execute(
                canonicalizedRequestBody: requestBody,
                writeContentType: BuildProfileHidingEntryDate(),
                resolvedContentType: ProfileContentType.Write,
                scopeCatalog: SharedFixtureScopes,
                storedDocument: null,
                isCreate: true,
                profileName: ProfileName,
                resourceName: ResourceName,
                method: Method,
                operation: Operation,
                effectiveSchemaRequiredMembersByScope: StandardRequiredMembers
            );
        }

        [Test]
        public void It_should_not_succeed()
        {
            _result.IsSuccess.Should().BeFalse();
        }

        [Test]
        public void It_should_contain_creatability_violation_failure()
        {
            _result
                .Failures.OfType<RootCreateRejectedWhenNonCreatableCreatabilityViolationFailure>()
                .Should()
                .NotBeEmpty();
        }

        [Test]
        public void It_should_be_category_4_creatability_violation()
        {
            _result
                .Failures.OfType<CreatabilityViolationFailure>()
                .Should()
                .AllSatisfy(f => f.Category.Should().Be(ProfileFailureCategory.CreatabilityViolation));
        }
    }

    // -----------------------------------------------------------------------
    //  6. Given_Upsert_With_No_Stored_Document — C6 not invoked
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_Upsert_With_No_Stored_Document : ProfileWritePipelineTests
    {
        private ProfileWritePipelineResult _result = null!;

        [SetUp]
        public void Setup()
        {
            // isCreate=false (PUT/upsert), but no stored document was found — simulates
            // an upsert that will insert a new record. C6 must be skipped entirely.
            _result = ProfileWritePipeline.Execute(
                canonicalizedRequestBody: BuildStandardRequestBody(),
                writeContentType: ProfileTestFixtures.BuildIncludeAllProfile(),
                resolvedContentType: ProfileContentType.Write,
                scopeCatalog: SharedFixtureScopes,
                storedDocument: null,
                isCreate: false,
                profileName: ProfileName,
                resourceName: ResourceName,
                method: "PUT",
                operation: "write",
                effectiveSchemaRequiredMembersByScope: StandardRequiredMembers
            );
        }

        [Test]
        public void It_should_not_have_context()
        {
            _result.Context.Should().BeNull();
        }

        [Test]
        public void It_should_succeed()
        {
            _result.IsSuccess.Should().BeTrue();
        }
    }
}
