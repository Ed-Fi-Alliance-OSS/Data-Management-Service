// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_OwnershipAuthorizationAuth1FailurePayloadCodec
{
    [TestCase(0, OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch, "own1|0|m")]
    [TestCase(1, OwnershipAuthorizationAuth1FailureKind.StoredOwnershipTokenUninitialized, "own1|1|u")]
    [TestCase(7, OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing, "own1|7|s")]
    public void It_encodes_the_configured_strategy_index_and_kind(
        int configuredStrategyIndex,
        OwnershipAuthorizationAuth1FailureKind failureKind,
        string expectedPayload
    )
    {
        var encoded = OwnershipAuthorizationAuth1FailurePayloadCodec.Encode(
            new OwnershipAuthorizationAuth1FailurePayload(configuredStrategyIndex, failureKind)
        );

        encoded.Should().Be(expectedPayload);
    }

    [TestCase(0, OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch)]
    [TestCase(2, OwnershipAuthorizationAuth1FailureKind.StoredOwnershipTokenUninitialized)]
    [TestCase(31, OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing)]
    public void It_round_trips_every_kind(
        int configuredStrategyIndex,
        OwnershipAuthorizationAuth1FailureKind failureKind
    )
    {
        var original = new OwnershipAuthorizationAuth1FailurePayload(configuredStrategyIndex, failureKind);

        var parsed = OwnershipAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
            OwnershipAuthorizationAuth1FailurePayloadCodec.Encode(original),
            out var payload
        );

        parsed.Should().BeTrue();
        payload.Should().NotBeNull();
        payload!.ConfiguredStrategyIndex.Should().Be(configuredStrategyIndex);
        payload.FailureKind.Should().Be(failureKind);
    }

    /// <summary>
    /// The index in the payload is the configured strategy position, so a strategy configured after other
    /// strategies must encode its real position rather than the constant zero an emitted ordinal would carry.
    /// </summary>
    [Test]
    public void It_preserves_a_nonzero_configured_index_through_a_round_trip()
    {
        OwnershipAuthorizationAuth1FailurePayloadCodec
            .TryParsePayload("own1|4|m", out var payload)
            .Should()
            .BeTrue();

        payload!.ConfiguredStrategyIndex.Should().Be(4);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("own1|0")]
    [TestCase("own1|0|m|extra")]
    [TestCase("own1|-1|m")]
    [TestCase("own1|abc|m")]
    [TestCase("own1|0|x")]
    [TestCase("ns1|0|m")]
    [TestCase("cv1|0|n")]
    [TestCase("1|0|1|0:0:s")]
    public void It_rejects_a_payload_it_does_not_own(string payloadText)
    {
        var parsed = OwnershipAuthorizationAuth1FailurePayloadCodec.TryParsePayload(
            payloadText,
            out var payload
        );

        parsed.Should().BeFalse();
        payload.Should().BeNull();
    }

    [Test]
    public void It_rejects_a_negative_configured_index_at_construction()
    {
        Action act = () =>
            new OwnershipAuthorizationAuth1FailurePayload(
                -1,
                OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch
            );

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void It_rejects_encoding_an_unsupported_kind()
    {
        Action act = () =>
            OwnershipAuthorizationAuth1FailurePayloadCodec.Encode(
                new OwnershipAuthorizationAuth1FailurePayload(0, (OwnershipAuthorizationAuth1FailureKind)999)
            );

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void It_rejects_encoding_a_null_payload()
    {
        Action act = () => OwnershipAuthorizationAuth1FailurePayloadCodec.Encode(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
