// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Telemetry;
using FluentAssertions;

namespace EdFi.DataManagementService.Core.Tests.Unit.TestSupport;

/// <summary>
/// Which recording method produced a measurement.
/// </summary>
/// <remarks>
/// Carried so a test can assert that a rejection went through the method that records no duration,
/// rather than through a handler method with a zero duration passed in. The two are indistinguishable
/// from the tag values alone.
/// </remarks>
internal enum CollectionPagingMeasurementKind
{
    Page,
    Partitions,
    ValidationRejected,
}

/// <summary>
/// One recorded call, with everything the emission site chose.
/// </summary>
internal sealed record CollectionPagingMeasurement(
    CollectionPagingMeasurementKind Kind,
    CollectionPagingTelemetryContext Context,
    TimeSpan? Duration,
    int? Requested,
    int? Returned
)
{
    public string PagingMode => Context.PagingMode;

    public string CommandCategory => Context.CommandCategory;

    public string Provider => Context.Provider;

    public string Outcome => Context.Outcome;
}

/// <summary>
/// Captures what an emission site classified, rather than what the meter did with it.
/// </summary>
/// <remarks>
/// Instrument names, units, and tag-set cardinality are pinned against a real MeterListener in the
/// telemetry component's own tests. What is under test here is the classification each call site
/// chose, so recording the arguments directly keeps these assertions readable and keeps a meter
/// listener out of handler tests.
/// </remarks>
internal sealed class RecordingCollectionPagingTelemetry : ICollectionPagingTelemetry
{
    private readonly List<CollectionPagingMeasurement> _measurements = [];

    public IReadOnlyList<CollectionPagingMeasurement> Measurements => _measurements;

    /// <summary>
    /// The single measurement this request contributed. Fails when a call site recorded none or more
    /// than one, which is itself part of the contract: one request is one measurement set.
    /// </summary>
    public CollectionPagingMeasurement Single => _measurements.Should().ContainSingle().Subject;

    public void RecordPage(
        CollectionPagingTelemetryContext context,
        TimeSpan duration,
        int requestedPageSize,
        int? returnedPageSize
    ) =>
        _measurements.Add(
            new CollectionPagingMeasurement(
                CollectionPagingMeasurementKind.Page,
                context,
                duration,
                requestedPageSize,
                returnedPageSize
            )
        );

    public void RecordPartitions(
        CollectionPagingTelemetryContext context,
        TimeSpan duration,
        int requestedPartitionCount,
        int? returnedPartitionCount
    ) =>
        _measurements.Add(
            new CollectionPagingMeasurement(
                CollectionPagingMeasurementKind.Partitions,
                context,
                duration,
                requestedPartitionCount,
                returnedPartitionCount
            )
        );

    public void RecordValidationRejected(CollectionPagingTelemetryContext context) =>
        _measurements.Add(
            new CollectionPagingMeasurement(
                CollectionPagingMeasurementKind.ValidationRejected,
                context,
                Duration: null,
                Requested: null,
                Returned: null
            )
        );
}

/// <summary>
/// Fails every recording call, standing in for a measurement callback the host subscribed that throws.
/// </summary>
/// <remarks>
/// A .NET instrument invokes its listeners' callbacks synchronously on the recording thread, and the
/// operator guidance for these metrics asks for exactly such a listener to be registered in the API
/// host. So third-party code that can throw sits between an emission site and the meter, and every
/// emission site has to survive it without changing what the client receives.
/// </remarks>
internal sealed class ThrowingCollectionPagingTelemetry : ICollectionPagingTelemetry
{
    /// <summary>
    /// The single fault every call raises, stated once so the message names the cause being simulated
    /// rather than reading as three unrelated test artifacts.
    /// </summary>
    private static readonly InvalidOperationException _fault = new(
        "A subscribed measurement callback failed."
    );

    public void RecordPage(
        CollectionPagingTelemetryContext context,
        TimeSpan duration,
        int requestedPageSize,
        int? returnedPageSize
    ) => throw _fault;

    public void RecordPartitions(
        CollectionPagingTelemetryContext context,
        TimeSpan duration,
        int requestedPartitionCount,
        int? returnedPartitionCount
    ) => throw _fault;

    public void RecordValidationRejected(CollectionPagingTelemetryContext context) => throw _fault;
}
