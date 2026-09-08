// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationalAuthorizationAuth1Dispatcher
{
    [Test]
    public void It_routes_a_postgresql_relationship_payload_to_the_relationship_codec()
    {
        var payloadText = "1|7|2|0:0:s,1:0:n";

        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            providerErrorCode: "AUTH1",
            providerMessage: payloadText,
            out var result
        );

        dispatched.Should().BeTrue();
        result.Should().BeOfType<RelationalAuthorizationAuth1DispatchResult.Relationship>();
        var relationship = (RelationalAuthorizationAuth1DispatchResult.Relationship)result!;
        relationship.Payload.EmittedAuth1Index.Should().Be(7);
        relationship.Payload.SubjectFailures.Should().HaveCount(2);
    }

    [Test]
    public void It_routes_a_sql_server_relationship_payload_via_the_AUTH1_dash_marker()
    {
        var sqlServerMessage =
            "Conversion failed when converting the varchar value 'AUTH1 - 1|3|1|0:0:p' to data type int.";

        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Mssql,
            providerErrorCode: null,
            providerMessage: sqlServerMessage,
            out var result
        );

        dispatched.Should().BeTrue();
        result.Should().BeOfType<RelationalAuthorizationAuth1DispatchResult.Relationship>();
    }

    [Test]
    public void It_routes_a_postgresql_namespace_payload_to_the_namespace_codec()
    {
        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            providerErrorCode: "AUTH1",
            providerMessage: "ns1|2|m",
            out var result
        );

        dispatched.Should().BeTrue();
        result.Should().BeOfType<RelationalAuthorizationAuth1DispatchResult.Namespace>();
        var ns = (RelationalAuthorizationAuth1DispatchResult.Namespace)result!;
        ns.Payload.EmittedAuth1Index.Should().Be(2);
        ns.Payload.FailureKind.Should().Be(NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch);
    }

    [Test]
    public void It_routes_a_sql_server_namespace_payload_via_the_AUTH1_dash_marker()
    {
        var sqlServerMessage =
            "Conversion failed when converting the varchar value 'AUTH1 - ns1|4|u' to data type int.";

        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Mssql,
            providerErrorCode: null,
            providerMessage: sqlServerMessage,
            out var result
        );

        dispatched.Should().BeTrue();
        result.Should().BeOfType<RelationalAuthorizationAuth1DispatchResult.Namespace>();
        var ns = (RelationalAuthorizationAuth1DispatchResult.Namespace)result!;
        ns.Payload.FailureKind.Should()
            .Be(NamespaceAuthorizationAuth1FailureKind.StoredNamespaceUninitialized);
    }

    [Test]
    public void It_returns_invalid_payload_for_an_unknown_discriminator()
    {
        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            providerErrorCode: "AUTH1",
            providerMessage: "v2|0|x",
            out var result
        );

        dispatched.Should().BeTrue();
        result.Should().BeOfType<RelationalAuthorizationAuth1DispatchResult.InvalidPayload>();
        var invalid = (RelationalAuthorizationAuth1DispatchResult.InvalidPayload)result!;
        invalid.RawPayload.Should().Be("v2|0|x");
    }

    [Test]
    public void It_returns_false_when_postgresql_error_code_is_not_AUTH1()
    {
        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            providerErrorCode: "P0001",
            providerMessage: "ns1|0|m",
            out var result
        );

        dispatched.Should().BeFalse();
        result.Should().BeNull();
    }

    [Test]
    public void It_returns_false_when_sql_server_message_has_no_AUTH1_marker()
    {
        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Mssql,
            providerErrorCode: null,
            providerMessage: "Some unrelated SQL Server exception.",
            out var result
        );

        dispatched.Should().BeFalse();
        result.Should().BeNull();
    }
}
