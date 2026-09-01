// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_OwnershipAuthorizationFailureMapper
{
    private static OwnershipAuthorizationAuth1FailurePayload Payload(
        OwnershipAuthorizationAuth1FailureKind failureKind,
        int configuredStrategyIndex = 0
    ) => new(configuredStrategyIndex, failureKind);

    [Test]
    public void It_maps_a_mismatch_payload_to_the_2_13_failure()
    {
        var result = OwnershipAuthorizationFailureMapper.Map(
            Payload(OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch, 2),
            plannedConfiguredStrategyIndex: 2
        );

        var denied = result.Should().BeOfType<OwnershipAuthorizationAuth1MapResult.Denied>().Subject;
        denied.Failure.FailureKind.Should().Be(OwnershipAuthorizationFailureKind.OwnershipTokenMismatch);
        denied.Failure.ConfiguredStrategyIndex.Should().Be(2);
        denied.Failure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    [Test]
    public void It_maps_an_uninitialized_payload_to_the_2_14_failure()
    {
        var result = OwnershipAuthorizationFailureMapper.Map(
            Payload(OwnershipAuthorizationAuth1FailureKind.StoredOwnershipTokenUninitialized, 1),
            plannedConfiguredStrategyIndex: 1
        );

        var denied = result.Should().BeOfType<OwnershipAuthorizationAuth1MapResult.Denied>().Subject;
        denied
            .Failure.FailureKind.Should()
            .Be(OwnershipAuthorizationFailureKind.StoredOwnershipTokenUninitialized);
        denied.Failure.ConfiguredStrategyIndex.Should().Be(1);
    }

    /// <summary>
    /// A strategy configured after namespace and custom-view strategies still attributes correctly, which is
    /// the case an emitted-ordinal payload could not have distinguished.
    /// </summary>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void It_attributes_a_denial_at_any_configured_position(int configuredStrategyIndex)
    {
        var result = OwnershipAuthorizationFailureMapper.Map(
            Payload(OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch, configuredStrategyIndex),
            configuredStrategyIndex
        );

        result
            .Should()
            .BeOfType<OwnershipAuthorizationAuth1MapResult.Denied>()
            .Which.Failure.ConfiguredStrategyIndex.Should()
            .Be(configuredStrategyIndex);
    }

    [Test]
    public void It_reports_a_stale_stored_target_as_a_retry_signal_rather_than_a_denial()
    {
        var result = OwnershipAuthorizationFailureMapper.Map(
            Payload(OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing, 0),
            plannedConfiguredStrategyIndex: 0
        );

        result.Should().BeOfType<OwnershipAuthorizationAuth1MapResult.StaleStoredTarget>();
    }

    /// <summary>
    /// The guardrail this mapper exists for: a payload whose configured index is not the planned check's
    /// index becomes a security-configuration outcome, never a 403 attributed to a strategy that did not
    /// deny the request.
    /// </summary>
    [TestCase(OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch)]
    [TestCase(OwnershipAuthorizationAuth1FailureKind.StoredOwnershipTokenUninitialized)]
    [TestCase(OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing)]
    public void It_reports_an_index_mismatch_as_unmappable_for_every_kind(
        OwnershipAuthorizationAuth1FailureKind failureKind
    )
    {
        var result = OwnershipAuthorizationFailureMapper.Map(
            Payload(failureKind, configuredStrategyIndex: 7),
            plannedConfiguredStrategyIndex: 1
        );

        result
            .Should()
            .BeOfType<OwnershipAuthorizationAuth1MapResult.Unmappable>()
            .Which.Reason.Should()
            .Be(OwnershipAuthorizationAuth1UnmappableReason.ConfiguredStrategyIndexMismatch);
    }

    [Test]
    public void It_reports_an_ownership_payload_with_no_planned_check_as_unmappable()
    {
        var result = OwnershipAuthorizationFailureMapper.Map(
            Payload(OwnershipAuthorizationAuth1FailureKind.OwnershipTokenMismatch, 0),
            plannedConfiguredStrategyIndex: null
        );

        result
            .Should()
            .BeOfType<OwnershipAuthorizationAuth1MapResult.Unmappable>()
            .Which.Reason.Should()
            .Be(OwnershipAuthorizationAuth1UnmappableReason.NoOwnershipCheckPlanned);
    }

    /// <summary>
    /// Attribution precedes the stale-target shortcut, so a stale payload from an unplanned check is a
    /// security-configuration outcome rather than a retry the request never earned.
    /// </summary>
    [Test]
    public void It_prefers_unmappable_over_stale_when_no_check_was_planned()
    {
        var result = OwnershipAuthorizationFailureMapper.Map(
            Payload(OwnershipAuthorizationAuth1FailureKind.StoredTargetMissing, 0),
            plannedConfiguredStrategyIndex: null
        );

        result.Should().BeOfType<OwnershipAuthorizationAuth1MapResult.Unmappable>();
    }

    [Test]
    public void It_rejects_a_null_payload()
    {
        Action act = () => OwnershipAuthorizationFailureMapper.Map(null!, 0);

        act.Should().Throw<ArgumentNullException>();
    }
}
