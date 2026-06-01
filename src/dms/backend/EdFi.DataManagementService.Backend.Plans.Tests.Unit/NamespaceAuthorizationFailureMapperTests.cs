// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_NamespaceAuthorizationFailureMapper
{
    private static readonly string[] _twoPrefixes = ["uri://ed-fi.org/", "uri://gbisd.edu/"];

    [Test]
    public void It_should_map_a_mismatch_payload_for_a_stored_check_to_a_stored_value_source_failure()
    {
        var plannedChecks = new[] { CreatePlannedCheck(NamespaceAuthorizationCheckValueSource.Stored) };
        var payload = new NamespaceAuthorizationAuth1FailurePayload(
            0,
            NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
        );

        var mapped = NamespaceAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            plannedChecks,
            _twoPrefixes,
            out var failure
        );

        mapped.Should().BeTrue();
        failure.Should().NotBeNull();
        failure!.FailureKind.Should().Be(NamespaceAuthorizationFailureKind.NamespaceMismatch);
        failure.ValueSource.Should().Be(NamespaceAuthorizationFailureValueSource.Stored);
        failure.EmittedAuth1Index.Should().Be(0);
        failure.StrategyName.Should().Be(AuthorizationStrategyNameConstants.NamespaceBased);
        failure.ConfiguredNamespacePrefixes.Should().Equal(_twoPrefixes);
    }

    [Test]
    public void It_should_map_a_mismatch_payload_for_a_proposed_check_to_a_proposed_value_source_failure()
    {
        var plannedChecks = new[]
        {
            CreatePlannedCheck(NamespaceAuthorizationCheckValueSource.Stored),
            CreatePlannedCheck(NamespaceAuthorizationCheckValueSource.Proposed),
        };
        var payload = new NamespaceAuthorizationAuth1FailurePayload(
            1,
            NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
        );

        var mapped = NamespaceAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            plannedChecks,
            _twoPrefixes,
            out var failure
        );

        mapped.Should().BeTrue();
        failure!.ValueSource.Should().Be(NamespaceAuthorizationFailureValueSource.Proposed);
        failure.EmittedAuth1Index.Should().Be(1);
    }

    [Test]
    public void It_should_map_a_stored_uninitialized_payload_to_a_stored_uninitialized_failure()
    {
        var plannedChecks = new[] { CreatePlannedCheck(NamespaceAuthorizationCheckValueSource.Stored) };
        var payload = new NamespaceAuthorizationAuth1FailurePayload(
            0,
            NamespaceAuthorizationAuth1FailureKind.StoredNamespaceUninitialized
        );

        var mapped = NamespaceAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            plannedChecks,
            _twoPrefixes,
            out var failure
        );

        mapped.Should().BeTrue();
        failure!.FailureKind.Should().Be(NamespaceAuthorizationFailureKind.StoredNamespaceUninitialized);
        failure.ValueSource.Should().Be(NamespaceAuthorizationFailureValueSource.Stored);
    }

    [Test]
    public void It_should_map_a_proposed_missing_payload_to_a_proposed_missing_failure()
    {
        var plannedChecks = new[] { CreatePlannedCheck(NamespaceAuthorizationCheckValueSource.Proposed) };
        var payload = new NamespaceAuthorizationAuth1FailurePayload(
            0,
            NamespaceAuthorizationAuth1FailureKind.ProposedNamespaceMissing
        );

        var mapped = NamespaceAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            plannedChecks,
            _twoPrefixes,
            out var failure
        );

        mapped.Should().BeTrue();
        failure!.FailureKind.Should().Be(NamespaceAuthorizationFailureKind.ProposedNamespaceMissing);
        failure.ValueSource.Should().Be(NamespaceAuthorizationFailureValueSource.Proposed);
    }

    [Test]
    public void It_should_fail_closed_when_the_emitted_index_is_out_of_range()
    {
        var plannedChecks = new[] { CreatePlannedCheck(NamespaceAuthorizationCheckValueSource.Stored) };
        var payload = new NamespaceAuthorizationAuth1FailurePayload(
            5,
            NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
        );

        var mapped = NamespaceAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            plannedChecks,
            _twoPrefixes,
            out var failure
        );

        mapped.Should().BeFalse();
        failure.Should().BeNull();
    }

    [Test]
    public void It_should_fail_closed_when_planned_checks_are_empty()
    {
        var payload = new NamespaceAuthorizationAuth1FailurePayload(
            0,
            NamespaceAuthorizationAuth1FailureKind.NamespaceMismatch
        );

        var mapped = NamespaceAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            [],
            _twoPrefixes,
            out var failure
        );

        mapped.Should().BeFalse();
        failure.Should().BeNull();
    }

    [Test]
    public void It_should_fail_closed_when_a_stored_uninitialized_failure_is_paired_with_a_proposed_check()
    {
        var plannedChecks = new[] { CreatePlannedCheck(NamespaceAuthorizationCheckValueSource.Proposed) };
        var payload = new NamespaceAuthorizationAuth1FailurePayload(
            0,
            NamespaceAuthorizationAuth1FailureKind.StoredNamespaceUninitialized
        );

        var mapped = NamespaceAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            plannedChecks,
            _twoPrefixes,
            out var failure
        );

        mapped.Should().BeFalse();
        failure.Should().BeNull();
    }

    [Test]
    public void It_should_fail_closed_when_a_proposed_missing_failure_is_paired_with_a_stored_check()
    {
        var plannedChecks = new[] { CreatePlannedCheck(NamespaceAuthorizationCheckValueSource.Stored) };
        var payload = new NamespaceAuthorizationAuth1FailurePayload(
            0,
            NamespaceAuthorizationAuth1FailureKind.ProposedNamespaceMissing
        );

        var mapped = NamespaceAuthorizationFailureMapper.TryMapAuth1Failure(
            payload,
            plannedChecks,
            _twoPrefixes,
            out var failure
        );

        mapped.Should().BeFalse();
        failure.Should().BeNull();
    }

    private static NamespaceAuthorizationCheckValueSource CreatePlannedCheck(
        NamespaceAuthorizationCheckValueSource valueSource
    ) => valueSource;
}
