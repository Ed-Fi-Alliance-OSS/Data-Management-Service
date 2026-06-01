// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Response;

[TestFixture]
[Parallelizable]
public class Given_Failure_Response_For_Namespace_Authorization
{
    private const string ExpectedTitle = "Authorization Denied";
    private const int ExpectedStatus = 403;

    private static readonly TraceId _traceId = new("ns-auth-trace");

    [Test]
    public void It_renders_the_no_prefixes_configured_problem_details()
    {
        var failure = new NamespaceAuthorizationFailure(
            NamespaceAuthorizationFailureKind.NoPrefixesConfigured,
            ValueSource: null,
            EmittedAuth1Index: null,
            StrategyName: AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: []
        );

        var response = NamespaceAuthorizationFailureResponse.ForFailure(failure, _traceId);

        response["type"]!
            .ToString()
            .Should()
            .Be("urn:ed-fi:api:security:authorization:namespace:invalid-client:no-namespaces");
        response["title"]!.ToString().Should().Be(ExpectedTitle);
        response["status"]!.GetValue<int>().Should().Be(ExpectedStatus);
        response["correlationId"]!.ToString().Should().Be(_traceId.Value);
        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "There was a problem authorizing the request. The caller has not been configured correctly for accessing resources authorized by Namespace."
            );
        response["errors"]!
            .AsArray()
            .Select(static error => error!.ToString())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                "The API client has been given permissions on a resource that uses the 'NamespaceBased' authorization strategy but the client doesn't have any namespace prefixes assigned."
            );
        response["validationErrors"]!.AsObject().Count.Should().Be(0);
    }

    [Test]
    public void It_renders_the_stored_namespace_uninitialized_problem_details()
    {
        var failure = new NamespaceAuthorizationFailure(
            NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized,
            NamespaceAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            StrategyName: AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/"]
        );

        var response = NamespaceAuthorizationFailureResponse.ForFailure(failure, _traceId);

        response["type"]!
            .ToString()
            .Should()
            .Be("urn:ed-fi:api:security:authorization:namespace:invalid-data:namespace-uninitialized");
        response["title"]!.ToString().Should().Be(ExpectedTitle);
        response["status"]!.GetValue<int>().Should().Be(ExpectedStatus);
        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "Access to the requested data could not be authorized. The existing 'Namespace' value has not been assigned but is required for authorization purposes."
            );
        response["errors"]!
            .AsArray()
            .Select(static error => error!.ToString())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                "The existing resource item is inaccessible to clients using the 'NamespaceBased' authorization strategy because the 'Namespace' value has not been assigned."
            );
    }

    [Test]
    public void It_renders_the_proposed_namespace_missing_problem_details()
    {
        var failure = new NamespaceAuthorizationFailure(
            NamespaceAuthorizationFailureKind.ProposedNamespaceMissing,
            NamespaceAuthorizationFailureValueSource.Proposed,
            EmittedAuth1Index: 0,
            StrategyName: AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/"]
        );

        var response = NamespaceAuthorizationFailureResponse.ForFailure(failure, _traceId);

        response["type"]!
            .ToString()
            .Should()
            .Be("urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-required");
        response["title"]!.ToString().Should().Be(ExpectedTitle);
        response["status"]!.GetValue<int>().Should().Be(ExpectedStatus);
        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "Access to the requested data could not be authorized. The 'Namespace' value has not been assigned but is required for authorization purposes."
            );
        response["errors"]!.AsArray().Count.Should().Be(0);
    }

    [Test]
    public void It_renders_the_namespace_mismatch_problem_details_for_a_stored_value_check_with_existing_in_detail()
    {
        var failure = new NamespaceAuthorizationFailure(
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Stored,
            EmittedAuth1Index: 0,
            StrategyName: AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/", "uri://gbisd.edu/"]
        );

        var response = NamespaceAuthorizationFailureResponse.ForFailure(failure, _traceId);

        response["type"]!
            .ToString()
            .Should()
            .Be("urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-mismatch");
        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "Access to the requested data could not be authorized. The existing 'Namespace' value of the data does not start with any of the caller's associated namespace prefixes ('uri://ed-fi.org/', 'uri://gbisd.edu/')."
            );
        response["errors"]!.AsArray().Count.Should().Be(0);
    }

    [Test]
    public void It_renders_the_namespace_mismatch_problem_details_for_a_proposed_value_check_without_existing()
    {
        var failure = new NamespaceAuthorizationFailure(
            NamespaceAuthorizationFailureKind.NamespaceMismatch,
            NamespaceAuthorizationFailureValueSource.Proposed,
            EmittedAuth1Index: 1,
            StrategyName: AuthorizationStrategyNameConstants.NamespaceBased,
            ConfiguredNamespacePrefixes: ["uri://ed-fi.org/"]
        );

        var response = NamespaceAuthorizationFailureResponse.ForFailure(failure, _traceId);

        response["detail"]!
            .ToString()
            .Should()
            .Be(
                "Access to the requested data could not be authorized. The 'Namespace' value of the data does not start with any of the caller's associated namespace prefixes ('uri://ed-fi.org/')."
            );
    }
}
