// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_CustomViewAuthorizationAuth1FailurePayloadCodec
{
    [Test]
    public void It_should_reject_negative_emitted_auth1_indexes()
    {
        var act = () =>
            new CustomViewAuthorizationAuth1FailurePayload(
                -1,
                CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow
            );

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("emittedAuth1Index")
            .WithMessage("Emitted AUTH1 index cannot be negative.*");
    }

    [Test]
    public void It_should_encode_a_no_matching_row_payload_with_the_cv1_discriminator()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            0,
            CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow
        );

        var encoded = CustomViewAuthorizationAuth1FailurePayloadCodec.Encode(payload);

        encoded.Should().Be("cv1|0|n");
    }

    [Test]
    public void It_should_throw_for_unknown_failure_kinds_when_encoding()
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(
            0,
            (CustomViewAuthorizationAuth1FailureKind)999
        );

        var act = () => CustomViewAuthorizationAuth1FailurePayloadCodec.Encode(payload);

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("failureKind")
            .WithMessage("Unsupported AUTH1 custom view failure kind.*");
    }

    [TestCase(0, CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow, "cv1|0|n")]
    [TestCase(3, CustomViewAuthorizationAuth1FailureKind.StoredBasisValueNull, "cv1|3|u")]
    [TestCase(5, CustomViewAuthorizationAuth1FailureKind.ProposedBasisValueMissing, "cv1|5|r")]
    [TestCase(2, CustomViewAuthorizationAuth1FailureKind.StoredTargetMissing, "cv1|2|s")]
    public void It_should_round_trip_each_failure_kind(
        int emittedIndex,
        CustomViewAuthorizationAuth1FailureKind kind,
        string expectedEncoding
    )
    {
        var payload = new CustomViewAuthorizationAuth1FailurePayload(emittedIndex, kind);

        var encoded = CustomViewAuthorizationAuth1FailurePayloadCodec.Encode(payload);
        var parsed = CustomViewAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
            encoded,
            out var parsedPayload
        );

        encoded.Should().Be(expectedEncoding);
        parsed.Should().BeTrue();
        parsedPayload.Should().BeEquivalentTo(payload);
    }

    [Test]
    public void It_should_dispatch_postgresql_and_sql_server_provider_failures_to_the_same_custom_view_payload()
    {
        var payloadText = "cv1|7|n";
        var sqlServerMessage =
            $"Conversion failed when converting the varchar value 'AUTH1 - {payloadText}' to data type int.";

        var postgresqlDispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            CustomViewAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
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
            .BeOfType<RelationalAuthorizationAuth1DispatchResult.CustomView>()
            .Subject.Payload;
        var sqlServerPayload = sqlServerResult
            .Should()
            .BeOfType<RelationalAuthorizationAuth1DispatchResult.CustomView>()
            .Subject.Payload;
        sqlServerPayload.Should().BeEquivalentTo(postgresqlPayload);
        sqlServerPayload.EmittedAuth1Index.Should().Be(7);
        sqlServerPayload
            .FailureKind.Should()
            .Be(CustomViewAuthorizationAuth1FailureKind.NoMatchingCustomViewRow);
    }

    [TestCase("cv2|0|n")] // unknown discriminator version
    [TestCase("1|0|n")] // relationship discriminator, must be rejected
    [TestCase("ns1|0|n")] // namespace discriminator, must be rejected
    [TestCase("cv1|0|x")] // unknown failure kind
    [TestCase("cv1|0|m")] // namespace's mismatch code is not a custom-view kind
    [TestCase("cv1|0|")] // missing failure kind
    [TestCase("cv1|0")] // missing failure kind segment entirely
    [TestCase("cv1||n")] // missing index
    [TestCase("cv1|-1|n")] // negative index
    [TestCase("cv1|0|n|extra")] // extra trailing segment
    [TestCase("")] // empty
    [TestCase("   ")] // whitespace
    public void It_should_fail_closed_for_malformed_or_unknown_payloads(string payloadText)
    {
        var parsed = CustomViewAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
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
            "cv1|0|n",
            out var result
        );

        dispatched.Should().BeFalse();
        result.Should().BeNull();
    }

    [Test]
    public void It_should_report_an_invalid_payload_for_a_malformed_custom_view_payload_on_the_auth1_transport()
    {
        // The AUTH1 transport was used, so the dispatcher must claim the failure, but the payload cannot be
        // decoded into an authorization decision. Callers log and fall through to a generic security
        // failure rather than inventing a 403.
        var dispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            CustomViewAuthorizationAuth1FailurePayloadCodec.ProviderFailureCode,
            "cv1|0|x",
            out var result
        );

        dispatched.Should().BeTrue();
        result
            .Should()
            .BeOfType<RelationalAuthorizationAuth1DispatchResult.InvalidPayload>()
            .Subject.RawPayload.Should()
            .Be("cv1|0|x");
    }

    [Test]
    public void It_should_keep_the_three_payload_families_independent()
    {
        // Same emitted index in all three families. Each must decode to its own result type, which is what
        // lets the custom-view index space stay independent of the namespace and relationship spaces.
        var customViewDispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            "AUTH1",
            "cv1|0|n",
            out var customViewResult
        );
        var namespaceDispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            "AUTH1",
            "ns1|0|m",
            out var namespaceResult
        );
        var relationshipDispatched = RelationalAuthorizationAuth1Dispatcher.TryDispatch(
            SqlDialect.Pgsql,
            "AUTH1",
            "1|0|1|0:0:n",
            out var relationshipResult
        );

        customViewDispatched.Should().BeTrue();
        namespaceDispatched.Should().BeTrue();
        relationshipDispatched.Should().BeTrue();
        customViewResult.Should().BeOfType<RelationalAuthorizationAuth1DispatchResult.CustomView>();
        namespaceResult.Should().BeOfType<RelationalAuthorizationAuth1DispatchResult.Namespace>();
        relationshipResult.Should().BeOfType<RelationalAuthorizationAuth1DispatchResult.Relationship>();
    }
}
