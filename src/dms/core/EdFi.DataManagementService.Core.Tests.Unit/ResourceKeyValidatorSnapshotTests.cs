// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.External.Backend;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit;

/// <summary>
/// What the validator does when the slow-path dms.ResourceKey read could not acquire a connection.
/// </summary>
/// <remarks>
/// Every other read failure here is deliberately turned into a <c>ValidationFailure</c>, so it is
/// cached and reported as a seed problem. A snapshot that could not be reached is the one failure that
/// is not a statement about the database's contents - no row was read - so reporting it that way would
/// tell an operator to reprovision a healthy database and would answer the request with the mismatch
/// 503 instead of Snapshot Not Found.
/// </remarks>
[TestFixture]
[Parallelizable]
public class ResourceKeyValidatorSnapshotTests
{
    private const string ConnectionString = "Server=snapshot;Database=edfi";

    /// <summary>
    /// What an engine would have composed: the provider type and its own error code, and nothing else.
    /// Distinct from anything in the connection string, so a log assertion that the string is absent
    /// still means something when this is present.
    /// </summary>
    private const string FailureDescription = "TimeoutException(-2)";

    private static readonly IReadOnlyList<ResourceKeyRow> _expectedKeys =
    [
        new(1, "Ed-Fi", "Student", "5.0.0"),
    ];

    /// <summary>
    /// A count that does not match the expectation below, so the fast path cannot answer and the row
    /// read - the failing acquisition - actually runs.
    /// </summary>
    private static DatabaseFingerprint MismatchedFingerprint() =>
        new("1.0", "abc123", 99, new byte[32].ToImmutableArray());

    private static async Task<(ResourceKeyValidationResult? Result, Exception? Thrown)> ValidateWith(
        EffectiveTargetKind kind,
        Exception thrown
    )
    {
        var reader = A.Fake<IResourceKeyRowReader>();
        A.CallTo(() => reader.ReadResourceKeyRowsAsync(A<EffectiveDataStoreTarget>._, A<CancellationToken>._))
            .ThrowsAsync(() => thrown);

        ResourceKeyValidator validator = new(reader, NullLogger<ResourceKeyValidator>.Instance);

        try
        {
            ResourceKeyValidationResult result = await validator.ValidateAsync(
                MismatchedFingerprint(),
                (short)_expectedKeys.Count,
                new byte[32].ToImmutableArray(),
                _expectedKeys,
                new EffectiveDataStoreTarget(kind, ConnectionString)
            );

            return (result, null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    [Test]
    public async Task It_rethrows_a_snapshot_wrapper_rather_than_reporting_a_mismatch()
    {
        DatabaseConnectionUnavailableException wrapper = new(
            EffectiveTargetKind.Snapshot,
            FailureDescription,
            new TimeoutException("connection timed out")
        );

        var (result, thrown) = await ValidateWith(EffectiveTargetKind.Snapshot, wrapper);

        result.Should().BeNull("a seed verdict must not be produced for a database that was not read");
        thrown.Should().BeSameAs(wrapper);
    }

    /// <summary>
    /// The condition is what keeps the primary and read-replica bodies byte-identical to what they
    /// produce today: the wrapper still becomes a ValidationFailure naming the exception type.
    /// </summary>
    [TestCase(EffectiveTargetKind.Primary)]
    [TestCase(EffectiveTargetKind.ReadReplica)]
    public async Task It_still_reports_a_non_snapshot_wrapper_as_a_validation_failure(
        EffectiveTargetKind kind
    )
    {
        var (result, thrown) = await ValidateWith(
            kind,
            new DatabaseConnectionUnavailableException(
                kind,
                FailureDescription,
                new TimeoutException("connection timed out")
            )
        );

        thrown.Should().BeNull();
        result
            .Should()
            .BeOfType<ResourceKeyValidationResult.ValidationFailure>()
            .Which.DiffReport.Should()
            .Contain(nameof(DatabaseConnectionUnavailableException));
    }

    /// <summary>
    /// An ordinary provider failure on a snapshot - one the guard did not classify, so it arrives
    /// unwrapped - is unaffected by the new arm and still becomes a ValidationFailure.
    /// </summary>
    [Test]
    public async Task It_still_reports_an_unwrapped_snapshot_failure_as_a_validation_failure()
    {
        var (result, thrown) = await ValidateWith(
            EffectiveTargetKind.Snapshot,
            new TimeoutException("connection timed out")
        );

        thrown.Should().BeNull();
        result.Should().BeOfType<ResourceKeyValidationResult.ValidationFailure>();
    }

    [Test]
    public async Task It_still_propagates_cancellation()
    {
        var (result, thrown) = await ValidateWith(
            EffectiveTargetKind.Snapshot,
            new OperationCanceledException()
        );

        result.Should().BeNull();
        thrown.Should().BeOfType<OperationCanceledException>();
    }
}
