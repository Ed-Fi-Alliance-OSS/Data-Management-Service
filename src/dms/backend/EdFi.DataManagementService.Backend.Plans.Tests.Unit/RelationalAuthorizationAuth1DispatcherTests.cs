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
    public void It_routes_a_postgresql_ownership_payload_to_the_ownership_codec()
    {
        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            providerErrorCode: "AUTH1",
            providerMessage: "own1|1|m",
            out var result
        );

        dispatched.Should().BeTrue();
        result.Should().BeOfType<RelationalAuthorizationAuth1DispatchResult.Ownership>();
        var ownership = (RelationalAuthorizationAuth1DispatchResult.Ownership)result!;
        ownership.Payload.ConfiguredStrategyIndex.Should().Be(1);
        ownership
            .Payload.FailureKind.Should()
            .Be(OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch);
    }

    [Test]
    public void It_routes_a_sql_server_ownership_payload_via_the_AUTH1_dash_marker()
    {
        var sqlServerMessage =
            "Conversion failed when converting the varchar value 'AUTH1 - own1|3|u' to data type int.";

        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Mssql,
            providerErrorCode: null,
            providerMessage: sqlServerMessage,
            out var result
        );

        dispatched.Should().BeTrue();
        var ownership = result
            .Should()
            .BeOfType<RelationalAuthorizationAuth1DispatchResult.Ownership>()
            .Subject;
        ownership.Payload.ConfiguredStrategyIndex.Should().Be(3);
        ownership
            .Payload.FailureKind.Should()
            .Be(OwnershipAuthorizationAuth1FailureKind.StoredOwnershipTokenUninitialized);
    }

    [Test]
    public void It_routes_an_ownership_stale_target_payload_to_the_ownership_codec()
    {
        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            providerErrorCode: "AUTH1",
            providerMessage: "own1|0|s",
            out var result
        );

        dispatched.Should().BeTrue();
        result
            .Should()
            .BeOfType<RelationalAuthorizationAuth1DispatchResult.Ownership>()
            .Which.Payload.FailureKind.Should()
            .Be(OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing);
    }

    /// <summary>
    /// The relationship family's discriminator is <c>1|</c>, and <c>own1|…</c> contains it. A prefix match is
    /// anchored, so ownership must not be claimed by the relationship codec regardless of arm order.
    /// </summary>
    [Test]
    public void It_does_not_let_the_relationship_family_claim_an_ownership_payload()
    {
        RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            providerErrorCode: "AUTH1",
            providerMessage: "own1|1|m",
            out var result
        );

        result.Should().NotBeOfType<RelationalAuthorizationAuth1DispatchResult.Relationship>();
    }

    /// <summary>
    /// Adding the ownership family must not change how the three existing families are routed.
    /// </summary>
    [TestCase("1|7|2|0:0:s,1:0:n", typeof(RelationalAuthorizationAuth1DispatchResult.Relationship))]
    [TestCase("ns1|2|m", typeof(RelationalAuthorizationAuth1DispatchResult.Namespace))]
    [TestCase("own1|2|m", typeof(RelationalAuthorizationAuth1DispatchResult.Ownership))]
    public void It_keeps_each_family_isolated_by_discriminator(string payloadText, Type expectedResultType)
    {
        RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            providerErrorCode: "AUTH1",
            providerMessage: payloadText,
            out var result
        );

        result.Should().BeOfType(expectedResultType);
    }

    /// <summary>
    /// An <c>own1</c> payload the codec cannot parse must still reach the caller as an invalid payload — a
    /// security-configuration outcome — rather than being silently dropped.
    /// </summary>
    [TestCase("own1|0")]
    [TestCase("own1|0|x")]
    [TestCase("own1|-1|m")]
    public void It_returns_invalid_payload_for_a_malformed_ownership_payload(string payloadText)
    {
        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            providerErrorCode: "AUTH1",
            providerMessage: payloadText,
            out var result
        );

        dispatched.Should().BeTrue();
        var invalid = result
            .Should()
            .BeOfType<RelationalAuthorizationAuth1DispatchResult.InvalidPayload>()
            .Subject;
        invalid.RawPayload.Should().Be(payloadText);
        invalid.RecognizedFamily.Should().Be(RelationalAuthorizationAuth1PayloadFamily.Ownership);
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
        // No known discriminator, so no family owns it and every mapper's catch-all is free to claim it.
        invalid.RecognizedFamily.Should().BeNull();
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

    // ── dispatcher-owned family recognition ────────────────────────────

    /// <summary>
    /// A malformed payload still announces which family emitted it, so the mapper that owns the
    /// discriminator can claim it and every other mapper can decline it without re-testing the raw prefix.
    /// </summary>
    /// <remarks>
    /// The prefix tests used to be copied into each provider failure mapper, where a family added to one
    /// copy and missed in another silently misattributed that family's malformed payloads. The dispatcher
    /// is now the only place that decides, and this is what pins it for every family at once.
    /// </remarks>
    [TestCase("1|x|2|0:0:s", RelationalAuthorizationAuth1PayloadFamily.Relationship)]
    [TestCase("1|7", RelationalAuthorizationAuth1PayloadFamily.Relationship)]
    [TestCase("ns1|0", RelationalAuthorizationAuth1PayloadFamily.Namespace)]
    [TestCase("ns1|0|x", RelationalAuthorizationAuth1PayloadFamily.Namespace)]
    [TestCase("cv1|0", RelationalAuthorizationAuth1PayloadFamily.CustomView)]
    [TestCase("cv1|0|x", RelationalAuthorizationAuth1PayloadFamily.CustomView)]
    [TestCase("own1|0", RelationalAuthorizationAuth1PayloadFamily.Ownership)]
    [TestCase("own1|0|x", RelationalAuthorizationAuth1PayloadFamily.Ownership)]
    public void It_reports_the_recognized_family_of_a_malformed_known_family_payload(
        string payloadText,
        RelationalAuthorizationAuth1PayloadFamily expectedFamily
    )
    {
        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            providerErrorCode: "AUTH1",
            providerMessage: payloadText,
            out var result
        );

        dispatched.Should().BeTrue();
        var invalid = result
            .Should()
            .BeOfType<RelationalAuthorizationAuth1DispatchResult.InvalidPayload>()
            .Subject;
        invalid.RawPayload.Should().Be(payloadText);
        invalid.RecognizedFamily.Should().Be(expectedFamily);
    }

    /// <summary>
    /// A payload leading with no known discriminator is invalid with no family, which is what keeps the
    /// mappers' catch-all diagnostics reachable for a payload nobody owns.
    /// </summary>
    [TestCase("v2|0|x")]
    [TestCase("garbage")]
    [TestCase("nsx1|0|m")]
    [TestCase("own|0|m")]
    public void It_reports_no_recognized_family_for_an_unknown_discriminator(string payloadText)
    {
        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            providerErrorCode: "AUTH1",
            providerMessage: payloadText,
            out var result
        );

        dispatched.Should().BeTrue();
        result
            .Should()
            .BeOfType<RelationalAuthorizationAuth1DispatchResult.InvalidPayload>()
            .Which.RecognizedFamily.Should()
            .BeNull();
    }

    /// <summary>
    /// The recognizer is exposed for the one caller that already holds an extracted payload, so it is pinned
    /// directly too — including the anchored-prefix case that keeps <c>own1|</c> out of the relationship
    /// family despite containing <c>1|</c>.
    /// </summary>
    [TestCase("1|7|2|0:0:s", RelationalAuthorizationAuth1PayloadFamily.Relationship)]
    [TestCase("ns1|2|m", RelationalAuthorizationAuth1PayloadFamily.Namespace)]
    [TestCase("cv1|2|m", RelationalAuthorizationAuth1PayloadFamily.CustomView)]
    [TestCase("own1|2|m", RelationalAuthorizationAuth1PayloadFamily.Ownership)]
    public void It_recognizes_each_family_from_a_raw_payload(
        string payloadText,
        RelationalAuthorizationAuth1PayloadFamily expectedFamily
    )
    {
        RelationalAuthorizationAuth1Dispatcher.RecognizeFamily(payloadText).Should().Be(expectedFamily);
    }

    [TestCase("v2|0|x")]
    [TestCase("garbage")]
    public void It_recognizes_no_family_for_an_unknown_raw_payload(string payloadText)
    {
        RelationalAuthorizationAuth1Dispatcher.RecognizeFamily(payloadText).Should().BeNull();
    }

    [Test]
    public void It_rejects_a_null_raw_payload()
    {
        Action act = () => RelationalAuthorizationAuth1Dispatcher.RecognizeFamily(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
