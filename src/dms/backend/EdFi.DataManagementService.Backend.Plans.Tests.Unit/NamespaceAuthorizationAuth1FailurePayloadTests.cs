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
public class Given_NamespaceAuthorizationAuth1FailurePayloadCodec
{
    [Test]
    public void It_should_encode_a_mismatch_payload_with_the_ns1_discriminator()
    {
        var payload = new NamespaceAuthorizationAuth1FailurePayload(
            0,
            NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
        );

        var encoded = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(payload);

        encoded.Should().Be("ns1|0|m");
    }

    [TestCase(0, NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch, "ns1|0|m")]
    [TestCase(3, NamespaceAuthorizationAuth1FailureKind.StoredNamespaceUninitialized, "ns1|3|u")]
    [TestCase(5, NamespaceAuthorizationAuth1FailureKind.ProposedNamespaceMissing, "ns1|5|r")]
    [TestCase(2, NamespaceAuthorizationAuth1FailureKind.StoredTargetMissing, "ns1|2|s")]
    public void It_should_round_trip_each_failure_kind(
        int emittedIndex,
        NamespaceAuthorizationAuth1FailureKind kind,
        string expectedEncoding
    )
    {
        var payload = new NamespaceAuthorizationAuth1FailurePayload(emittedIndex, kind);

        var encoded = NamespaceAuthorizationAuth1FailurePayloadCodec.Encode(payload);
        var parsed = NamespaceAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
            encoded,
            out var parsedPayload
        );

        encoded.Should().Be(expectedEncoding);
        parsed.Should().BeTrue();
        parsedPayload.Should().BeEquivalentTo(payload);
    }

    [Test]
    public void It_should_dispatch_postgresql_and_sql_server_provider_failures_to_the_same_namespace_payload()
    {
        // Production routes provider failures through the shared dispatcher rather than codec-specific
        // wrappers, so the namespace payload is recovered identically from the PostgreSQL SqlState
        // transport and the SQL Server message transport.
        var payloadText = "ns1|7|m";
        var sqlServerMessage =
            $"Conversion failed when converting the varchar value 'AUTH1 - {payloadText}' to data type int.";

        var postgresqlDispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            NamespaceAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
            payloadText,
            out var postgresqlResult
        );
        var sqlServerDispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Mssql,
            null,
            sqlServerMessage,
            out var sqlServerResult
        );

        postgresqlDispatched.Should().BeTrue();
        sqlServerDispatched.Should().BeTrue();
        var postgresqlPayload = postgresqlResult
            .Should()
            .BeOfType<RelationalAuthorizationAuth1DispatchResult.Namespace>()
            .Subject.Payload;
        var sqlServerPayload = sqlServerResult
            .Should()
            .BeOfType<RelationalAuthorizationAuth1DispatchResult.Namespace>()
            .Subject.Payload;
        sqlServerPayload.Should().BeEquivalentTo(postgresqlPayload);
        sqlServerPayload.EmittedAuth1Index.Should().Be(7);
        sqlServerPayload.FailureKind.Should().Be(NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch);
    }

    [TestCase("ns2|0|m")] // unknown version
    [TestCase("1|0|m")] // relationship discriminator, must be rejected
    [TestCase("ns1|0|x")] // unknown failure kind
    [TestCase("ns1|0|")] // missing failure kind
    [TestCase("ns1|0")] // missing failure kind segment entirely
    [TestCase("ns1||m")] // missing index
    [TestCase("ns1|-1|m")] // negative index
    [TestCase("ns1|0|m|extra")] // extra trailing segment
    [TestCase("")] // empty
    [TestCase("   ")] // whitespace
    public void It_should_fail_closed_for_malformed_or_unknown_payloads(string payloadText)
    {
        var parsed = NamespaceAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
            payloadText,
            out var payload
        );

        parsed.Should().BeFalse();
        payload.Should().BeNull();
    }

    [Test]
    public void It_should_not_dispatch_a_payload_when_postgresql_error_code_is_not_AUTH1()
    {
        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            "P0001",
            "ns1|0|m",
            out var result
        );

        dispatched.Should().BeFalse();
        result.Should().BeNull();
    }

    [Test]
    public void It_should_not_dispatch_a_payload_when_sql_server_message_lacks_the_AUTH1_marker()
    {
        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Mssql,
            null,
            "Some unrelated SQL Server error message.",
            out var result
        );

        dispatched.Should().BeFalse();
        result.Should().BeNull();
    }
}
