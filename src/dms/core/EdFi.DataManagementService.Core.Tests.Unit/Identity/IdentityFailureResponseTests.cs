// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Identity;

/// <summary>
/// Pins the six fixed identity problem-detail builders (design.md D9): their type, title, status, and
/// (where the story fixes them) detail, plus the shared envelope every <see cref="FailureResponse" />
/// builder produces.
/// </summary>
[TestFixture]
public class IdentityFailureResponseTests
{
    private static readonly TraceId _traceId = new("identity-trace");

    [Test]
    public void It_builds_operation_not_supported_as_a_404_under_the_identities_namespace()
    {
        var response = IdentityFailureResponse.ForIdentityOperationNotSupported(_traceId);

        response["type"]!.ToString().Should().Be("urn:ed-fi:api:identities:operation-not-supported");
        response["type"]!.ToString().Should().Be(IdentityFailureResponse.OperationNotSupportedType);
        response["status"]!.GetValue<int>().Should().Be(404);
        AssertSharedEnvelope(response);
    }

    [Test]
    public void It_builds_identity_not_found_as_a_404_distinct_from_operation_not_supported()
    {
        var response = IdentityFailureResponse.ForIdentityNotFound(_traceId);

        response["type"]!.ToString().Should().Be("urn:ed-fi:api:identities:not-found");
        response["type"]!.ToString().Should().Be(IdentityFailureResponse.NotFoundType);
        response["status"]!.GetValue<int>().Should().Be(404);
        response["type"]!.ToString().Should().NotBe(IdentityFailureResponse.OperationNotSupportedType);
        AssertSharedEnvelope(response);
    }

    [Test]
    public void It_builds_provider_contract_violation_as_a_502_carrying_the_supplied_detail()
    {
        var response = IdentityFailureResponse.ForIdentityProviderContractViolation(
            _traceId,
            "Success result was missing the required payload."
        );

        response["type"]!.ToString().Should().Be("urn:ed-fi:api:identities:provider-contract-violation");
        response["type"]!.ToString().Should().Be(IdentityFailureResponse.ProviderContractViolationType);
        response["status"]!.GetValue<int>().Should().Be(502);
        response["detail"]!.ToString().Should().Be("Success result was missing the required payload.");
        AssertSharedEnvelope(response);
    }

    [Test]
    public void It_builds_upstream_failure_as_a_502_whose_title_names_identity_management_and_omits_provider_text()
    {
        var response = IdentityFailureResponse.ForIdentityUpstreamFailure(_traceId);

        response["type"]!.ToString().Should().Be("urn:ed-fi:api:identities:upstream-failure");
        response["type"]!.ToString().Should().Be(IdentityFailureResponse.UpstreamFailureType);
        response["status"]!.GetValue<int>().Should().Be(502);
        response["title"]!.ToString().Should().Contain("Identity Management");
        response["detail"]!.ToString().Should().NotBeNullOrWhiteSpace();
        response["detail"]!
            .ToString()
            .Should()
            .NotContainAny("Exception", "provider", "Provider", "stack", "Stack");
        AssertSharedEnvelope(response);
    }

    [Test]
    public void It_builds_job_failed_as_a_502_with_the_fixed_title_and_detail()
    {
        var response = IdentityFailureResponse.ForIdentityJobFailed(_traceId);

        response["type"]!.ToString().Should().Be("urn:ed-fi:api:identities:job-failed");
        response["type"]!.ToString().Should().Be(IdentityFailureResponse.JobFailedType);
        response["status"]!.GetValue<int>().Should().Be(502);
        response["title"]!.ToString().Should().Be("Identity job failed");
        response["detail"]!
            .ToString()
            .Should()
            .Be("The accepted identity request failed permanently. Stop polling this job.");
        AssertSharedEnvelope(response);
    }

    [Test]
    public void It_builds_provider_configuration_as_a_500_with_the_fixed_title_and_detail()
    {
        var response = IdentityFailureResponse.ForIdentityProviderConfiguration(_traceId);

        response["type"]!.ToString().Should().Be("urn:ed-fi:api:identities:provider-configuration");
        response["type"]!.ToString().Should().Be(IdentityFailureResponse.ProviderConfigurationType);
        response["status"]!.GetValue<int>().Should().Be(500);
        response["title"]!.ToString().Should().Be("Identity provider configuration failure");
        response["detail"]!
            .ToString()
            .Should()
            .Be("The identity provider could not be initialized or its capabilities evaluated.");
        AssertSharedEnvelope(response);
    }

    [Test]
    public void The_six_type_constants_are_distinct_and_share_the_identities_problem_namespace()
    {
        string[] types =
        [
            IdentityFailureResponse.OperationNotSupportedType,
            IdentityFailureResponse.NotFoundType,
            IdentityFailureResponse.ProviderContractViolationType,
            IdentityFailureResponse.UpstreamFailureType,
            IdentityFailureResponse.JobFailedType,
            IdentityFailureResponse.ProviderConfigurationType,
        ];

        types.Should().OnlyHaveUniqueItems();
        types.Should().OnlyContain(type => type.StartsWith("urn:ed-fi:api:identities:"));
    }

    private static void AssertSharedEnvelope(System.Text.Json.Nodes.JsonNode response)
    {
        response["correlationId"]!.ToString().Should().Be(_traceId.Value);
        response["validationErrors"]!.AsObject().Count.Should().Be(0);
        response["errors"]!.AsArray().Count.Should().Be(0);
    }
}
