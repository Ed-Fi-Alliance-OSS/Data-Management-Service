// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Response;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Response;

[TestFixture]
[Parallelizable]
public class Given_Failure_Response_For_Ownership_Authorization
{
    private const string ExpectedTitle = "Authorization Denied";
    private const int ExpectedStatus = 403;

    /// <summary>
    /// auth.md gives §2.13 and §2.14 the same client-facing detail; only the type and the errors differ.
    /// </summary>
    private const string ExpectedDetail =
        "Access to the requested data could not be authorized. The item is not owned by the caller.";

    private static readonly TraceId _traceId = new("ownership-auth-trace");

    private static OwnershipAuthorizationFailure Failure(
        OwnershipAuthorizationFailureKind failureKind,
        int configuredStrategyIndex = 0
    ) => new(failureKind, configuredStrategyIndex, AuthorizationStrategyNameConstants.OwnershipBased);

    [Test]
    public void It_renders_the_ownership_mismatch_problem_details()
    {
        var response = OwnershipAuthorizationFailureResponse.ForFailure(
            Failure(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch),
            _traceId
        );

        response["type"]!
            .ToString()
            .Should()
            .Be("urn:ed-fi:api:security:authorization:ownership:access-denied:ownership-mismatch");
        response["title"]!.ToString().Should().Be(ExpectedTitle);
        response["status"]!.GetValue<int>().Should().Be(ExpectedStatus);
        response["correlationId"]!.ToString().Should().Be(_traceId.Value);
        response["detail"]!.ToString().Should().Be(ExpectedDetail);
        response["errors"]!.AsArray().Should().BeEmpty();
        response["validationErrors"]!.AsObject().Count.Should().Be(0);
    }

    [Test]
    public void It_renders_the_stored_ownership_token_uninitialized_problem_details()
    {
        var response = OwnershipAuthorizationFailureResponse.ForFailure(
            Failure(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized),
            _traceId
        );

        response["type"]!
            .ToString()
            .Should()
            .Be("urn:ed-fi:api:security:authorization:ownership:invalid-data:ownership-uninitialized");
        response["title"]!.ToString().Should().Be(ExpectedTitle);
        response["status"]!.GetValue<int>().Should().Be(ExpectedStatus);
        response["correlationId"]!.ToString().Should().Be(_traceId.Value);
        response["detail"]!.ToString().Should().Be(ExpectedDetail);
        response["errors"]!
            .AsArray()
            .Select(static error => error!.ToString())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                "The existing resource item has no 'CreatedByOwnershipTokenId' value assigned and thus will never be accessible to clients using the 'OwnershipBased' authorization strategy."
            );
        response["validationErrors"]!.AsObject().Count.Should().Be(0);
    }

    /// <summary>
    /// The two cases must stay distinguishable by type even though auth.md gives them one detail sentence, so a
    /// client can tell "not yours" from "will never be yours" without the response spelling it out.
    /// </summary>
    [Test]
    public void It_distinguishes_the_two_cases_only_by_type_and_errors()
    {
        var mismatch = OwnershipAuthorizationFailureResponse.ForFailure(
            Failure(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch),
            _traceId
        );
        var uninitialized = OwnershipAuthorizationFailureResponse.ForFailure(
            Failure(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized),
            _traceId
        );

        mismatch["detail"]!.ToString().Should().Be(uninitialized["detail"]!.ToString());
        mismatch["title"]!.ToString().Should().Be(uninitialized["title"]!.ToString());
        mismatch["status"]!.GetValue<int>().Should().Be(uninitialized["status"]!.GetValue<int>());
        mismatch["type"]!.ToString().Should().NotBe(uninitialized["type"]!.ToString());
        mismatch["errors"]!.AsArray().Count.Should().NotBe(uninitialized["errors"]!.AsArray().Count);
    }

    /// <summary>
    /// The configured strategy index exists for log traceability. Rendering it would leak the caller's claim-set
    /// shape into a client response for no benefit, so no case may include it.
    /// </summary>
    [TestCase(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch)]
    [TestCase(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized)]
    public void It_never_renders_the_configured_strategy_index(OwnershipAuthorizationFailureKind failureKind)
    {
        // A value no other part of the rendered body can produce, so the assertion cannot pass or fail by
        // coincidence with a status code, a trace id, or a URN segment.
        const int DistinctiveIndex = 91357;

        var response = OwnershipAuthorizationFailureResponse.ForFailure(
            Failure(failureKind, DistinctiveIndex),
            _traceId
        );

        response.ToJsonString().Should().NotContain(DistinctiveIndex.ToString(CultureInfo.InvariantCulture));
    }

    [Test]
    public void It_rejects_a_null_failure()
    {
        Action act = () => OwnershipAuthorizationFailureResponse.ForFailure(null!, _traceId);

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void It_rejects_an_unsupported_failure_kind()
    {
        Action act = () =>
            OwnershipAuthorizationFailureResponse.ForFailure(
                Failure((OwnershipAuthorizationFailureKind)999),
                _traceId
            );

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
