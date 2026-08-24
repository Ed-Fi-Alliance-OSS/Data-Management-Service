// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.Metrics;
using System.Globalization;
using EdFi.DataManagementService.Core.Telemetry;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration;

/// <summary>
/// One measurement the host published on the collection-paging meter, reduced to what the metric
/// contract actually promises: an instrument name, a value, and the four bounded dimensions.
/// </summary>
/// <param name="Instrument">
/// The instrument object the measurement arrived on, not just its name. Each host builds its own
/// instruments on the process-static meter, so this is what distinguishes two hosts publishing the same
/// instrument name from one host publishing it twice.
/// </param>
internal sealed record CollectionPagingMeasurement(
    Instrument Instrument,
    double Value,
    IReadOnlyDictionary<string, string> Tags
)
{
    public string InstrumentName => Instrument.Name;

    public string PagingMode => Tag("paging_mode");

    public string CommandCategory => Tag("command_category");

    public string Provider => Tag("provider");

    public string Outcome => Tag("outcome");

    private string Tag(string key) =>
        Tags.TryGetValue(key, out string? value)
            ? value
            : throw new InvalidOperationException(
                $"Measurement '{InstrumentName}' carries no '{key}' tag; tags present: "
                    + $"{string.Join(", ", Tags.Keys)}."
            );
}

/// <summary>
/// Observes the collection-paging meter from inside the test process while an assembled host serves
/// requests, so what the tests assert on is what a metrics pipeline in the host would collect.
/// </summary>
/// <remarks>
/// .NET <c>Meter</c> instruments are observable only in-process, which is what makes an in-process
/// listener the only way to prove these measurements end to end. The meter is process-static, so a
/// collector observes whichever host is currently serving; the fixtures in this suite run one host at a
/// time, and <see cref="Clear" /> before each asserted request keeps a window to that request alone.
/// <para>
/// That one-host-at-a-time assumption is checked rather than merely relied on. Each host builds its own
/// instruments on the shared meter, so <see cref="Single" /> can tell a second host publishing the same
/// instrument name from one host publishing twice, and says which it saw. A fixture that leaves an
/// earlier host serving therefore fails with its own cause named instead of looking like the telemetry
/// contract emitting more than one measurement set per request.
/// </para>
/// </remarks>
internal sealed class CollectionPagingMetricCollector : IDisposable
{
    private readonly object _sync = new();
    private readonly List<CollectionPagingMeasurement> _measurements = [];
    private readonly MeterListener _listener;

    private CollectionPagingMetricCollector()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (
                    string.Equals(
                        instrument.Meter.Name,
                        CollectionPagingTelemetry.MeterName,
                        StringComparison.Ordinal
                    )
                )
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        // Every instrument type the contract publishes: the request counter is long, duration is double,
        // and the four size and count histograms are int. A missing callback would silently drop the
        // instrument rather than fail, so all three are registered.
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.SetMeasurementEventCallback<int>(OnMeasurement);
        _listener.Start();
    }

    public static CollectionPagingMetricCollector Start() => new();

    public IReadOnlyList<CollectionPagingMeasurement> Measurements
    {
        get
        {
            lock (_sync)
            {
                return [.. _measurements];
            }
        }
    }

    /// <summary>
    /// Discards everything observed so far, so the next request's measurements stand alone.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _measurements.Clear();
        }
    }

    /// <summary>
    /// Asserts the measurement set of exactly one GET-many emission and returns its request measurement.
    /// </summary>
    public CollectionPagingMeasurement AssertSinglePage(
        string expectedProvider,
        string expectedPagingMode,
        string expectedCommandCategory,
        string expectedOutcome,
        int expectedRequestedPageSize,
        int? expectedReturnedPageSize
    )
    {
        var request = AssertSingleRequest(
            expectedProvider,
            expectedPagingMode,
            expectedCommandCategory,
            expectedOutcome
        );

        AssertSingleValue(CollectionPagingTelemetry.RequestedPageSizeName, expectedRequestedPageSize);
        AssertOptionalValue(CollectionPagingTelemetry.ReturnedPageSizeName, expectedReturnedPageSize);
        AssertNotRecorded(CollectionPagingTelemetry.RequestedPartitionCountName);
        AssertNotRecorded(CollectionPagingTelemetry.ReturnedPartitionCountName);

        return request;
    }

    /// <summary>
    /// Asserts the measurement set of exactly one <c>/partitions</c> emission and returns its request
    /// measurement.
    /// </summary>
    public CollectionPagingMeasurement AssertSinglePartitions(
        string expectedProvider,
        string expectedCommandCategory,
        string expectedOutcome,
        int expectedRequestedPartitionCount,
        int? expectedReturnedPartitionCount
    )
    {
        var request = AssertSingleRequest(
            expectedProvider,
            CollectionPagingTelemetryLabel.PartitionPagingMode,
            expectedCommandCategory,
            expectedOutcome
        );

        AssertSingleValue(
            CollectionPagingTelemetry.RequestedPartitionCountName,
            expectedRequestedPartitionCount
        );
        AssertOptionalValue(
            CollectionPagingTelemetry.ReturnedPartitionCountName,
            expectedReturnedPartitionCount
        );
        AssertNotRecorded(CollectionPagingTelemetry.RequestedPageSizeName);
        AssertNotRecorded(CollectionPagingTelemetry.ReturnedPageSizeName);

        return request;
    }

    /// <summary>
    /// The single request-counter measurement observed, with its dimensions and the duration that must
    /// accompany a request the host actually executed.
    /// </summary>
    private CollectionPagingMeasurement AssertSingleRequest(
        string expectedProvider,
        string expectedPagingMode,
        string expectedCommandCategory,
        string expectedOutcome
    )
    {
        AssertDimensionsBounded();

        var request = Single(CollectionPagingTelemetry.RequestCounterName);

        request.Value.Should().Be(1, "the request counter counts one request per emission");
        request.PagingMode.Should().Be(expectedPagingMode);
        request.CommandCategory.Should().Be(expectedCommandCategory);
        request.Provider.Should().Be(expectedProvider);
        request.Outcome.Should().Be(expectedOutcome);

        var duration = Single(CollectionPagingTelemetry.DurationName);
        duration.Value.Should().BeGreaterThanOrEqualTo(0);
        duration.Tags.Should().Equal(request.Tags, "every measurement of one emission shares its tags");

        return request;
    }

    /// <summary>
    /// No measurement carries a dimension the contract does not define, and no dimension carries a value
    /// outside its allowed set. This is what keeps request data — resource names, tenant keys,
    /// namespaces, client identity, filter values, page tokens — out of the metric end to end.
    /// </summary>
    /// <remarks>
    /// The allowed values are read from the component rather than restated here. These are the same
    /// per-dimension sets the emission path checks membership against, so a value arriving on this
    /// collector that they do not contain is a breach of the published contract rather than a local copy
    /// that fell out of step — and their contents are pinned against the operator documentation by the
    /// component's own tests, which is where that job belongs.
    /// </remarks>
    private void AssertDimensionsBounded()
    {
        foreach (var measurement in Measurements)
        {
            measurement
                .Tags.Keys.Should()
                .BeEquivalentTo(
                    new[] { "paging_mode", "command_category", "provider", "outcome" },
                    $"'{measurement.InstrumentName}' must carry exactly the four defined dimensions"
                );
            measurement.PagingMode.Should().BeOneOf(CollectionPagingTelemetryLabel.PagingModes);
            measurement.CommandCategory.Should().BeOneOf(CollectionPagingTelemetryLabel.CommandCategories);
            measurement.Provider.Should().BeOneOf(CollectionPagingTelemetryLabel.Providers);
            measurement.Outcome.Should().BeOneOf(CollectionPagingTelemetryLabel.Outcomes);
        }
    }

    private CollectionPagingMeasurement Single(string instrumentName)
    {
        var recorded = Measurements
            .Where(measurement =>
                string.Equals(measurement.InstrumentName, instrumentName, StringComparison.Ordinal)
            )
            .ToList();

        // Checked ahead of the count, because it is the one cause of a duplicate here that is not a
        // defect in an emission site. This collector observes the whole process, so a second host still
        // serving would deliver its own instrument under the same name and be counted too. Left to the
        // count assertion below, that would read as the telemetry contract emitting twice per request.
        recorded
            .DistinctBy(measurement => measurement.Instrument)
            .Should()
            .HaveCountLessThanOrEqualTo(
                1,
                $"'{instrumentName}' must reach this collector from one host: the meter is "
                    + "process-static, so a fixture that left an earlier host serving is observed here "
                    + "as well and this is a fixture-lifetime fault rather than a metric one"
            );

        recorded
            .Should()
            .ContainSingle(
                $"'{instrumentName}' must be recorded exactly once per emission; observed: "
                    + $"{string.Join(", ", Measurements.Select(measurement => measurement.InstrumentName))}"
            );

        return recorded[0];
    }

    private void AssertSingleValue(string instrumentName, int expectedValue) =>
        Single(instrumentName).Value.Should().Be(expectedValue);

    private void AssertOptionalValue(string instrumentName, int? expectedValue)
    {
        if (expectedValue is { } value)
        {
            AssertSingleValue(instrumentName, value);
            return;
        }

        AssertNotRecorded(instrumentName);
    }

    private void AssertNotRecorded(string instrumentName) =>
        Measurements
            .Should()
            .NotContain(
                measurement =>
                    string.Equals(measurement.InstrumentName, instrumentName, StringComparison.Ordinal),
                $"'{instrumentName}' does not belong to this operation and would mix two units of measure"
            );

    private void OnMeasurement<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state
    )
        where T : struct
    {
        Dictionary<string, string> tagValues = new(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            tagValues[tag.Key] = tag.Value?.ToString() ?? string.Empty;
        }

        lock (_sync)
        {
            _measurements.Add(
                new CollectionPagingMeasurement(
                    instrument,
                    Convert.ToDouble(measurement, CultureInfo.InvariantCulture),
                    tagValues
                )
            );
        }
    }

    public void Dispose() => _listener.Dispose();
}
