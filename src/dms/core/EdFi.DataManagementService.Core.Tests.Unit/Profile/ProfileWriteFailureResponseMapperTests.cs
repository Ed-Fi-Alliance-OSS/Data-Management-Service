// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Profile;

public abstract class ProfileWriteFailureResponseMapperTests
{
    protected const string ProfileName = "TestProfile";
    protected static readonly TraceId TraceId = new("trace-123");

    protected const string DataValidationType = "urn:ed-fi:api:bad-request:data-validation-failed";
    protected const string DataPolicyEnforcedType = "urn:ed-fi:api:data-policy-enforced";
    protected const string SystemErrorType = "urn:ed-fi:api:system";

    internal static string TypeOf(FrontendResponse response) => response.Body!["type"]!.GetValue<string>();

    internal static int StatusInBody(FrontendResponse response) => response.Body!["status"]!.GetValue<int>();

    [TestFixture]
    public class Given_A_Collection_Value_Filter_Failure : ProfileWriteFailureResponseMapperTests
    {
        private FrontendResponse _response = null!;

        [SetUp]
        public void Setup()
        {
            _response = ProfileWriteFailureResponseMapper.Map(
                [
                    ProfileFailures.CollectionValueFilterViolation(
                        profileName: ProfileName,
                        resourceName: "School",
                        method: "POST",
                        operation: "upsert",
                        jsonScope: "$.gradeLevels[*]",
                        requestJsonPaths: ["$.gradeLevels[0]"],
                        filterPropertyName: "gradeLevelDescriptor",
                        filterMode: FilterMode.IncludeOnly,
                        filterValues: ["uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"]
                    ),
                ],
                ProfileName,
                TraceId
            );
        }

        [Test]
        public void It_should_return_status_400()
        {
            _response.StatusCode.Should().Be(400);
            StatusInBody(_response).Should().Be(400);
        }

        [Test]
        public void It_should_return_data_validation_failed_type()
        {
            TypeOf(_response).Should().Be(DataValidationType);
        }
    }

    [TestFixture]
    public class Given_A_Duplicate_Visible_Collection_Item_Collision_Failure
        : ProfileWriteFailureResponseMapperTests
    {
        private FrontendResponse _response = null!;

        [SetUp]
        public void Setup()
        {
            _response = ProfileWriteFailureResponseMapper.Map(
                [
                    ProfileFailures.DuplicateVisibleCollectionItemCollision(
                        profileName: ProfileName,
                        resourceName: "StudentSchoolAssociation",
                        method: "PUT",
                        operation: "update",
                        jsonScope: "$.classPeriods[*]",
                        stableParentAddress: new ScopeInstanceAddress("$", []),
                        semanticIdentityPartsInOrder:
                        [
                            new SemanticIdentityPart(
                                "classPeriodName",
                                JsonValue.Create("First Period"),
                                true
                            ),
                        ],
                        requestJsonPaths: ["$.classPeriods[0]", "$.classPeriods[1]"]
                    ),
                ],
                ProfileName,
                TraceId
            );
        }

        [Test]
        public void It_should_return_status_400()
        {
            _response.StatusCode.Should().Be(400);
            StatusInBody(_response).Should().Be(400);
        }

        [Test]
        public void It_should_return_data_validation_failed_type()
        {
            TypeOf(_response).Should().Be(DataValidationType);
        }
    }

    [TestFixture]
    public class Given_A_Creatability_Violation_Failure : ProfileWriteFailureResponseMapperTests
    {
        private FrontendResponse _response = null!;

        [SetUp]
        public void Setup()
        {
            _response = ProfileWriteFailureResponseMapper.Map(
                [
                    ProfileFailures.RootCreateRejectedWhenNonCreatable(
                        profileName: ProfileName,
                        resourceName: "School",
                        method: "POST",
                        operation: "upsert",
                        hiddenCreationRequiredMemberPaths: ["nameOfInstitution"]
                    ),
                ],
                ProfileName,
                TraceId
            );
        }

        [Test]
        public void It_should_return_status_400()
        {
            _response.StatusCode.Should().Be(400);
            StatusInBody(_response).Should().Be(400);
        }

        [Test]
        public void It_should_return_data_policy_enforced_type()
        {
            TypeOf(_response).Should().Be(DataPolicyEnforcedType);
        }
    }

    [TestFixture]
    public class Given_A_Generic_Writable_Profile_Validation_Failure : ProfileWriteFailureResponseMapperTests
    {
        private FrontendResponse _response = null!;

        [SetUp]
        public void Setup()
        {
            _response = ProfileWriteFailureResponseMapper.Map(
                [
                    ProfileFailures.WritableProfileValidationFailure(
                        ProfileFailureEmitter.RequestVisibilityAndWritableShaping,
                        "Generic writable validation failure.",
                        ProfileFailureContext.Empty
                    ),
                ],
                ProfileName,
                TraceId
            );
        }

        [Test]
        public void It_should_return_status_400()
        {
            _response.StatusCode.Should().Be(400);
        }

        [Test]
        public void It_should_return_data_policy_enforced_type()
        {
            TypeOf(_response).Should().Be(DataPolicyEnforcedType);
        }
    }

    [TestFixture]
    public class Given_A_Core_Backend_Contract_Mismatch_Failure : ProfileWriteFailureResponseMapperTests
    {
        private FrontendResponse _response = null!;

        [SetUp]
        public void Setup()
        {
            _response = ProfileWriteFailureResponseMapper.Map(
                [
                    ProfileFailures.CoreBackendContractMismatch(
                        ProfileFailureEmitter.BackendProfileWriteContext,
                        "Core/backend contract mismatch.",
                        ProfileFailureContext.Empty
                    ),
                ],
                ProfileName,
                TraceId
            );
        }

        [Test]
        public void It_should_return_status_500()
        {
            _response.StatusCode.Should().Be(500);
            StatusInBody(_response).Should().Be(500);
        }

        [Test]
        public void It_should_return_system_error_type()
        {
            TypeOf(_response).Should().Be(SystemErrorType);
        }
    }

    [TestFixture]
    public class Given_An_Invalid_Profile_Definition_Failure : ProfileWriteFailureResponseMapperTests
    {
        private FrontendResponse _response = null!;

        [SetUp]
        public void Setup()
        {
            _response = ProfileWriteFailureResponseMapper.Map(
                [
                    ProfileFailures.InvalidProfileDefinition(
                        ProfileFailureEmitter.SemanticIdentityCompatibilityValidation,
                        "Invalid profile definition.",
                        ProfileFailureContext.Empty
                    ),
                ],
                ProfileName,
                TraceId
            );
        }

        [Test]
        public void It_should_return_status_500()
        {
            _response.StatusCode.Should().Be(500);
        }

        [Test]
        public void It_should_return_system_error_type()
        {
            TypeOf(_response).Should().Be(SystemErrorType);
        }
    }

    [TestFixture]
    public class Given_A_Binding_Accounting_Failure : ProfileWriteFailureResponseMapperTests
    {
        private FrontendResponse _response = null!;

        [SetUp]
        public void Setup()
        {
            _response = ProfileWriteFailureResponseMapper.Map(
                [
                    ProfileFailures.BindingAccountingFailure(
                        ProfileFailureEmitter.ProfileErrorClassification,
                        "Binding accounting failure.",
                        ProfileFailureContext.Empty
                    ),
                ],
                ProfileName,
                TraceId
            );
        }

        [Test]
        public void It_should_return_status_500()
        {
            _response.StatusCode.Should().Be(500);
        }

        [Test]
        public void It_should_return_system_error_type()
        {
            TypeOf(_response).Should().Be(SystemErrorType);
        }
    }

    [TestFixture]
    public class Given_Mixed_Server_Error_And_Value_Filter_Failures : ProfileWriteFailureResponseMapperTests
    {
        private FrontendResponse _response = null!;

        [SetUp]
        public void Setup()
        {
            // A server-error category takes precedence over a data-validation failure.
            _response = ProfileWriteFailureResponseMapper.Map(
                [
                    ProfileFailures.CollectionValueFilterViolation(
                        profileName: ProfileName,
                        resourceName: "School",
                        method: "POST",
                        operation: "upsert",
                        jsonScope: "$.gradeLevels[*]",
                        requestJsonPaths: ["$.gradeLevels[0]"],
                        filterPropertyName: "gradeLevelDescriptor",
                        filterMode: FilterMode.IncludeOnly,
                        filterValues: ["uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"]
                    ),
                    ProfileFailures.CoreBackendContractMismatch(
                        ProfileFailureEmitter.BackendProfileWriteContext,
                        "Core/backend contract mismatch.",
                        ProfileFailureContext.Empty
                    ),
                ],
                ProfileName,
                TraceId
            );
        }

        [Test]
        public void It_should_return_status_500()
        {
            _response.StatusCode.Should().Be(500);
        }

        [Test]
        public void It_should_return_system_error_type()
        {
            TypeOf(_response).Should().Be(SystemErrorType);
        }
    }
}
