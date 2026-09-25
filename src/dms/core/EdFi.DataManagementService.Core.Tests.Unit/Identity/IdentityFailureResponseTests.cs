// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
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
public class IdentityFailureResponseTests
{
    private static readonly TraceId _traceId = new("identity-trace");

    private static void AssertSharedEnvelope(JsonNode response)
    {
        response["correlationId"]!.ToString().Should().Be(_traceId.Value);
        response["validationErrors"]!.AsObject().Count.Should().Be(0);
        response["errors"]!.AsArray().Count.Should().Be(0);
    }

    [TestFixture]
    public class Given_Operation_Not_Supported
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            _response = IdentityFailureResponse.ForIdentityOperationNotSupported(_traceId);
        }

        [Test]
        public void It_uses_the_operation_not_supported_type_literal()
        {
            _response["type"]!.ToString().Should().Be("urn:ed-fi:api:identities:operation-not-supported");
        }

        [Test]
        public void It_uses_the_operation_not_supported_type_constant()
        {
            _response["type"]!.ToString().Should().Be(IdentityFailureResponse.OperationNotSupportedType);
        }

        [Test]
        public void It_returns_404()
        {
            _response["status"]!.GetValue<int>().Should().Be(404);
        }

        [Test]
        public void It_uses_the_shared_problem_envelope()
        {
            AssertSharedEnvelope(_response);
        }
    }

    [TestFixture]
    public class Given_Identity_Not_Found
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            _response = IdentityFailureResponse.ForIdentityNotFound(_traceId);
        }

        [Test]
        public void It_uses_the_not_found_type_literal()
        {
            _response["type"]!.ToString().Should().Be("urn:ed-fi:api:identities:not-found");
        }

        [Test]
        public void It_uses_the_not_found_type_constant()
        {
            _response["type"]!.ToString().Should().Be(IdentityFailureResponse.NotFoundType);
        }

        [Test]
        public void It_returns_404()
        {
            _response["status"]!.GetValue<int>().Should().Be(404);
        }

        [Test]
        public void It_is_distinct_from_operation_not_supported()
        {
            _response["type"]!.ToString().Should().NotBe(IdentityFailureResponse.OperationNotSupportedType);
        }

        [Test]
        public void It_uses_the_shared_problem_envelope()
        {
            AssertSharedEnvelope(_response);
        }
    }

    [TestFixture]
    public class Given_Provider_Contract_Violation
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            _response = IdentityFailureResponse.ForIdentityProviderContractViolation(
                _traceId,
                "Success result was missing the required payload."
            );
        }

        [Test]
        public void It_uses_the_provider_contract_violation_type_literal()
        {
            _response["type"]!.ToString().Should().Be("urn:ed-fi:api:identities:provider-contract-violation");
        }

        [Test]
        public void It_uses_the_provider_contract_violation_type_constant()
        {
            _response["type"]!.ToString().Should().Be(IdentityFailureResponse.ProviderContractViolationType);
        }

        [Test]
        public void It_returns_502()
        {
            _response["status"]!.GetValue<int>().Should().Be(502);
        }

        [Test]
        public void It_carries_the_supplied_detail()
        {
            _response["detail"]!.ToString().Should().Be("Success result was missing the required payload.");
        }

        [Test]
        public void It_uses_the_shared_problem_envelope()
        {
            AssertSharedEnvelope(_response);
        }
    }

    [TestFixture]
    public class Given_Upstream_Failure
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            _response = IdentityFailureResponse.ForIdentityUpstreamFailure(_traceId);
        }

        [Test]
        public void It_uses_the_upstream_failure_type_literal()
        {
            _response["type"]!.ToString().Should().Be("urn:ed-fi:api:identities:upstream-failure");
        }

        [Test]
        public void It_uses_the_upstream_failure_type_constant()
        {
            _response["type"]!.ToString().Should().Be(IdentityFailureResponse.UpstreamFailureType);
        }

        [Test]
        public void It_returns_502()
        {
            _response["status"]!.GetValue<int>().Should().Be(502);
        }

        [Test]
        public void It_names_identity_management_in_the_title()
        {
            _response["title"]!.ToString().Should().Contain("Identity Management");
        }

        [Test]
        public void It_has_a_non_blank_detail()
        {
            _response["detail"]!.ToString().Should().NotBeNullOrWhiteSpace();
        }

        [Test]
        public void It_omits_provider_and_exception_text_from_the_detail()
        {
            _response["detail"]!
                .ToString()
                .Should()
                .NotContainAny("Exception", "provider", "Provider", "stack", "Stack");
        }

        [Test]
        public void It_uses_the_shared_problem_envelope()
        {
            AssertSharedEnvelope(_response);
        }
    }

    [TestFixture]
    public class Given_Job_Failed
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            _response = IdentityFailureResponse.ForIdentityJobFailed(_traceId);
        }

        [Test]
        public void It_uses_the_job_failed_type_literal()
        {
            _response["type"]!.ToString().Should().Be("urn:ed-fi:api:identities:job-failed");
        }

        [Test]
        public void It_uses_the_job_failed_type_constant()
        {
            _response["type"]!.ToString().Should().Be(IdentityFailureResponse.JobFailedType);
        }

        [Test]
        public void It_returns_502()
        {
            _response["status"]!.GetValue<int>().Should().Be(502);
        }

        [Test]
        public void It_uses_the_fixed_title()
        {
            _response["title"]!.ToString().Should().Be("Identity job failed");
        }

        [Test]
        public void It_uses_the_fixed_detail()
        {
            _response["detail"]!
                .ToString()
                .Should()
                .Be("The accepted identity request failed permanently. Stop polling this job.");
        }

        [Test]
        public void It_uses_the_shared_problem_envelope()
        {
            AssertSharedEnvelope(_response);
        }
    }

    [TestFixture]
    public class Given_Provider_Configuration
    {
        private JsonNode _response = null!;

        [SetUp]
        public void Setup()
        {
            _response = IdentityFailureResponse.ForIdentityProviderConfiguration(_traceId);
        }

        [Test]
        public void It_uses_the_provider_configuration_type_literal()
        {
            _response["type"]!.ToString().Should().Be("urn:ed-fi:api:identities:provider-configuration");
        }

        [Test]
        public void It_uses_the_provider_configuration_type_constant()
        {
            _response["type"]!.ToString().Should().Be(IdentityFailureResponse.ProviderConfigurationType);
        }

        [Test]
        public void It_returns_500()
        {
            _response["status"]!.GetValue<int>().Should().Be(500);
        }

        [Test]
        public void It_uses_the_fixed_title()
        {
            _response["title"]!.ToString().Should().Be("Identity provider configuration failure");
        }

        [Test]
        public void It_uses_the_fixed_detail()
        {
            _response["detail"]!
                .ToString()
                .Should()
                .Be("The identity provider could not be initialized or its capabilities evaluated.");
        }

        [Test]
        public void It_uses_the_shared_problem_envelope()
        {
            AssertSharedEnvelope(_response);
        }
    }

    [TestFixture]
    public class Given_The_Six_Type_Constants
    {
        private string[] _types = null!;

        [SetUp]
        public void Setup()
        {
            _types =
            [
                IdentityFailureResponse.OperationNotSupportedType,
                IdentityFailureResponse.NotFoundType,
                IdentityFailureResponse.ProviderContractViolationType,
                IdentityFailureResponse.UpstreamFailureType,
                IdentityFailureResponse.JobFailedType,
                IdentityFailureResponse.ProviderConfigurationType,
            ];
        }

        [Test]
        public void It_are_all_distinct()
        {
            _types.Should().OnlyHaveUniqueItems();
        }

        [Test]
        public void It_share_the_identities_problem_namespace()
        {
            _types.Should().OnlyContain(type => type.StartsWith("urn:ed-fi:api:identities:"));
        }
    }
}
