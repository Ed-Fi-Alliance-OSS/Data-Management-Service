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
    //  6. Given_C6_Stub_Invoked_For_Update_Flow — mock projector called
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_C6_Stub_Invoked_For_Update_Flow : ProfileWritePipelineTests
    {
        private ProfileWritePipelineResult _result = null!;
        private MockStoredStateProjector _mockProjector = null!;

        private sealed class MockStoredStateProjector : IStoredStateProjector
        {
            public bool WasCalled { get; private set; }

            public ProfileAppliedWriteContext ProjectStoredState(
                JsonNode? storedDocument,
                IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
                ContentTypeDefinition writeContentType,
                ProfileAppliedWriteRequest request,
                StoredSideExistenceLookupResult existenceLookupResult
            )
            {
                WasCalled = true;
                return new ProfileAppliedWriteContext(request, JsonNode.Parse("{}")!, [], []);
            }
        }

        [SetUp]
        public void Setup()
        {
            _mockProjector = new MockStoredStateProjector();

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
                effectiveSchemaRequiredMembersByScope: StandardRequiredMembers,
                storedStateProjector: _mockProjector
            );
        }

        [Test]
        public void It_should_invoke_the_projector()
        {
            _mockProjector.WasCalled.Should().BeTrue();
        }

        [Test]
        public void It_should_have_a_context()
        {
            _result.Context.Should().NotBeNull();
        }

        [Test]
        public void It_should_succeed()
        {
            _result.IsSuccess.Should().BeTrue();
        }
    }

    // -----------------------------------------------------------------------
    //  7. Given_C6_Not_Invoked_When_Upsert_Misses_Stored_Document —
    //     PUT/upsert with isCreate=false but no stored document must NOT invoke C6
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_C6_Not_Invoked_When_Upsert_Misses_Stored_Document : ProfileWritePipelineTests
    {
        private ProfileWritePipelineResult _result = null!;

        /// <summary>
        /// Projector that throws if called, making any accidental invocation an immediate failure.
        /// </summary>
        private sealed class ThrowingStoredStateProjector : IStoredStateProjector
        {
            public ProfileAppliedWriteContext ProjectStoredState(
                JsonNode? storedDocument,
                IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
                ContentTypeDefinition writeContentType,
                ProfileAppliedWriteRequest request,
                StoredSideExistenceLookupResult existenceLookupResult
            )
            {
                throw new InvalidOperationException(
                    "C6 projector must not be called when storedDocument is null."
                );
            }
        }

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
                effectiveSchemaRequiredMembersByScope: StandardRequiredMembers,
                storedStateProjector: new ThrowingStoredStateProjector()
            );
        }

        [Test]
        public void It_should_not_invoke_the_projector()
        {
            // If the guard is wrong the ThrowingStoredStateProjector would have thrown,
            // causing Setup to fail before this assertion is ever reached.
            _result.Context.Should().BeNull();
        }

        [Test]
        public void It_should_succeed()
        {
            _result.IsSuccess.Should().BeTrue();
        }
    }

    // -----------------------------------------------------------------------
    //  8. Given_C6_Not_Invoked_For_Create_Flow — mock projector not called
    // -----------------------------------------------------------------------

    [TestFixture]
    public class Given_C6_Not_Invoked_For_Create_Flow : ProfileWritePipelineTests
    {
        private ProfileWritePipelineResult _result = null!;
        private MockStoredStateProjector _mockProjector = null!;

        private sealed class MockStoredStateProjector : IStoredStateProjector
        {
            public bool WasCalled { get; private set; }

            public ProfileAppliedWriteContext ProjectStoredState(
                JsonNode? storedDocument,
                IReadOnlyList<CompiledScopeDescriptor> scopeCatalog,
                ContentTypeDefinition writeContentType,
                ProfileAppliedWriteRequest request,
                StoredSideExistenceLookupResult existenceLookupResult
            )
            {
                WasCalled = true;
                return new ProfileAppliedWriteContext(request, JsonNode.Parse("{}")!, [], []);
            }
        }

        [SetUp]
        public void Setup()
        {
            _mockProjector = new MockStoredStateProjector();

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
                effectiveSchemaRequiredMembersByScope: StandardRequiredMembers,
                storedStateProjector: _mockProjector
            );
        }

        [Test]
        public void It_should_not_invoke_the_projector()
        {
            _mockProjector.WasCalled.Should().BeFalse();
        }

        [Test]
        public void It_should_have_no_context()
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
