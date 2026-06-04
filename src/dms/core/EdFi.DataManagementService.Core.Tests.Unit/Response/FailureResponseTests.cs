// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Response;

[TestFixture]
[Parallelizable]
public class Given_FailureResponse_For_Data_Conflict
{
    private const string ExpectedType = "urn:ed-fi:api:data-conflict:dependent-item-exists";
    private const string ExpectedTitle = "Dependent Item Exists";
    private const int ExpectedStatus = 409;

    private static readonly TraceId _traceId = new("fr-trace");

    [Test]
    public void It_renders_a_generic_fallback_detail_when_the_dependent_item_names_array_is_empty()
    {
        // DMS-1011: the relational delete path surfaces an empty array when the FK violation's
        // constraint name is missing (pgsql null ConstraintName / mssql localized 547) or
        // cannot be mapped to a resource in the compiled model. The response layer owns the
        // user-facing fallback string so the backend contract can stay honest.
        var response = FailureResponse.ForDataConflict([], _traceId);

        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "The requested action cannot be performed because this item is referenced by existing item(s)."
            );
        AssertSharedEnvelope(response);
    }

    [Test]
    public void It_preserves_the_existing_single_name_ods_api_phrasing_byte_for_byte()
    {
        // Regression: the byte-for-byte string here is asserted by E2E scenarios in
        // DeleteReferenceValidation.feature. If this test fails, the E2E regression will too.
        var response = FailureResponse.ForDataConflict(["Calendar"], _traceId);

        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "The requested action cannot be performed because this item is referenced by existing Calendar item(s)."
            );
        AssertSharedEnvelope(response);
    }

    [Test]
    public void It_joins_multiple_dependent_item_names_with_a_comma_and_space()
    {
        var response = FailureResponse.ForDataConflict(["Calendar", "School"], _traceId);

        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "The requested action cannot be performed because this item is referenced by existing Calendar, School item(s)."
            );
        AssertSharedEnvelope(response);
    }

    private static void AssertSharedEnvelope(System.Text.Json.Nodes.JsonNode response)
    {
        // The empty-array branch must not diverge on type / title / status / correlationId /
        // validationErrors / errors — only the rendered detail differs.
        response["type"]!.ToString().Should().Be(ExpectedType);
        response["title"]!.ToString().Should().Be(ExpectedTitle);
        response["status"]!.GetValue<int>().Should().Be(ExpectedStatus);
        response["correlationId"]!.ToString().Should().Be(_traceId.Value);
        response["validationErrors"]!.AsObject().Count.Should().Be(0);
        response["errors"]!.AsArray().Count.Should().Be(0);
    }
}

[TestFixture]
[Parallelizable]
public class Given_FailureResponse_For_Security_Configuration
{
    private static readonly TraceId _traceId = new("security-trace");

    [Test]
    public void It_renders_the_canonical_security_configuration_problem_details_shape()
    {
        var response = FailureResponse.ForSecurityConfiguration(
            _traceId,
            [SecurityConfigurationFailureMessages.MissingSecurityMetadata]
        );

        response["type"]!.ToString().Should().Be(SecurityConfigurationProblemDetails.Type);
        response["title"]!.ToString().Should().Be(SecurityConfigurationProblemDetails.Title);
        response["detail"]!.ToString().Should().Be(SecurityConfigurationProblemDetails.Detail);
        response["status"]!.GetValue<int>().Should().Be(SecurityConfigurationProblemDetails.Status);
        response["correlationId"]!.ToString().Should().Be(_traceId.Value);
        response["validationErrors"]!.AsObject().Count.Should().Be(0);
        response["errors"]!
            .AsArray()
            .Select(static error => error!.ToString())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(SecurityConfigurationFailureMessages.MissingSecurityMetadata);
    }

    [Test]
    public void It_preserves_supplied_security_configuration_errors_in_order()
    {
        string firstError = SecurityConfigurationFailureMessages.NoAuthorizationStrategies(
            "Read",
            ["http://ed-fi.org/identity/claims/ed-fi/school"],
            "http://ed-fi.org/identity/claims/ed-fi/school"
        );
        string secondError = SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([
            "RelationshipsWithStudentsOnly",
        ]);

        var response = FailureResponse.ForSecurityConfiguration(_traceId, [firstError, secondError]);

        response["errors"]!
            .AsArray()
            .Select(static error => error!.ToString())
            .Should()
            .Equal(firstError, secondError);
    }

    [Test]
    public void It_formats_the_canonical_no_strategies_message_with_resource_claim_uris()
    {
        string message = SecurityConfigurationFailureMessages.NoAuthorizationStrategies(
            "ReadChanges",
            [
                "http://ed-fi.org/identity/claims/ed-fi/student",
                "http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationAssociation",
            ],
            "http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationAssociation"
        );

        message
            .Should()
            .Be(
                "No authorization strategies were defined for the requested action 'ReadChanges' against resource URIs ['http://ed-fi.org/identity/claims/ed-fi/student', 'http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationAssociation'] matched by the caller's claim 'http://ed-fi.org/identity/claims/ed-fi/studentEducationOrganizationAssociation'."
            );
    }

    [Test]
    public void It_formats_the_canonical_unknown_strategy_message_in_deterministic_order()
    {
        string message = SecurityConfigurationFailureMessages.UnknownAuthorizationStrategies([
            "StudentScope",
            "CalendarScope",
            "StudentScope",
        ]);

        message
            .Should()
            .Be(
                "Could not find authorization strategy implementations for the following strategy names: 'CalendarScope', 'StudentScope'."
            );
    }

    [Test]
    public void It_formats_the_canonical_custom_view_basis_property_message()
    {
        string message = SecurityConfigurationFailureMessages.CustomViewBasisPropertyUnavailable(
            "edfi.CourseTranscript",
            "StudentUniqueId",
            "edfi.Student"
        );

        message
            .Should()
            .Be(
                "Unable to find a property on the authorization subject entity type 'edfi.CourseTranscript' corresponding to the 'StudentUniqueId' property on the custom authorization view's basis entity type 'edfi.Student' in order to perform authorization. Should a different authorization strategy be used?"
            );
    }
}
