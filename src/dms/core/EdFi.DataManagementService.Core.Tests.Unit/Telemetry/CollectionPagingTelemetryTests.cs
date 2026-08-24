// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.Metrics;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Serilog;

namespace EdFi.DataManagementService.Core.Tests.Unit.Telemetry;

/// <summary>
/// The bounded collection-paging metric contract.
/// </summary>
/// <remarks>
/// Asserts the contract an operator builds dashboards and alerts from — instrument names, units, and the
/// complete allowed value list for each dimension — because a rename or a new dimension breaks queries
/// silently rather than breaking a response.
/// </remarks>
[TestFixture]
[Parallelizable]
[Category("CollectionPagingTelemetry")]
public class Given_CollectionPagingTelemetry
{
    private static readonly string[] AllowedTagKeys =
    [
        "paging_mode",
        "command_category",
        "provider",
        "outcome",
    ];

    private static readonly CollectionPaging TraditionalPaging = new CollectionPaging.Traditional(
        new PaginationParameters(Limit: 25, Offset: 0, TotalCount: false, MaximumPageSize: 500)
    );

    private static readonly CollectionPaging CursorPaging = new CollectionPaging.Cursor(
        new CursorRange(1L, 1000L),
        new PageSize(100)
    );

    [Test]
    public void It_records_the_get_many_instruments_with_documented_names_units_and_tags()
    {
        using MetricCollector collector = new();
        CollectionPagingTelemetry telemetry = collector.CreateTelemetry();

        telemetry.RecordPage(
            CollectionPagingTelemetryContext.ForPaging(
                CursorPaging,
                CollectionPagingTelemetryLabel.PageCommandCategory,
                SqlDialect.Pgsql,
                CollectionPagingTelemetryLabel.SuccessOutcome
            ),
            TimeSpan.FromMilliseconds(37),
            requestedPageSize: 100,
            returnedPageSize: 42
        );

        MetricMeasurement request = collector.Single(CollectionPagingTelemetry.RequestCounterName);
        request.LongValue.Should().Be(1);
        request.Unit.Should().Be("{request}");
        request.Tags["paging_mode"].Should().Be("cursor");
        request.Tags["command_category"].Should().Be("page");
        request.Tags["provider"].Should().Be("postgresql");
        request.Tags["outcome"].Should().Be("success");

        MetricMeasurement duration = collector.Single(CollectionPagingTelemetry.DurationName);
        duration.DoubleValue.Should().Be(37);
        duration.Unit.Should().Be("ms");

        MetricMeasurement requestedPageSize = collector.Single(
            CollectionPagingTelemetry.RequestedPageSizeName
        );
        requestedPageSize.IntValue.Should().Be(100);
        requestedPageSize.Unit.Should().Be("{item}");

        MetricMeasurement returnedPageSize = collector.Single(CollectionPagingTelemetry.ReturnedPageSizeName);
        returnedPageSize.IntValue.Should().Be(42);
        returnedPageSize.Unit.Should().Be("{item}");

        // Page-size and partition-count instruments measure different things, so a GET-many emission must
        // not contribute to a histogram whose unit is partitions.
        collector.MeasurementsFor(CollectionPagingTelemetry.RequestedPartitionCountName).Should().BeEmpty();
        collector.MeasurementsFor(CollectionPagingTelemetry.ReturnedPartitionCountName).Should().BeEmpty();

        collector.AllMeasurements.Should().OnlyContain(measurement => HasExactlyTheAllowedTags(measurement));
    }

    [Test]
    public void It_records_the_partition_instruments_with_documented_names_units_and_tags()
    {
        using MetricCollector collector = new();
        CollectionPagingTelemetry telemetry = collector.CreateTelemetry();

        telemetry.RecordPartitions(
            CollectionPagingTelemetryContext.ForPagingMode(
                CollectionPagingTelemetryLabel.PartitionPagingMode,
                CollectionPagingTelemetryLabel.BoundaryCommandCategory,
                SqlDialect.Mssql,
                CollectionPagingTelemetryLabel.SuccessOutcome
            ),
            TimeSpan.FromMilliseconds(12),
            requestedPartitionCount: 8,
            returnedPartitionCount: 3
        );

        MetricMeasurement request = collector.Single(CollectionPagingTelemetry.RequestCounterName);
        request.LongValue.Should().Be(1);
        request.Tags["paging_mode"].Should().Be("partition");
        request.Tags["command_category"].Should().Be("boundary");
        request.Tags["provider"].Should().Be("sqlserver");
        request.Tags["outcome"].Should().Be("success");

        collector.Single(CollectionPagingTelemetry.DurationName).DoubleValue.Should().Be(12);

        MetricMeasurement requestedPartitionCount = collector.Single(
            CollectionPagingTelemetry.RequestedPartitionCountName
        );
        requestedPartitionCount.IntValue.Should().Be(8);
        requestedPartitionCount.Unit.Should().Be("{partition}");

        MetricMeasurement returnedPartitionCount = collector.Single(
            CollectionPagingTelemetry.ReturnedPartitionCountName
        );
        returnedPartitionCount.IntValue.Should().Be(3);
        returnedPartitionCount.Unit.Should().Be("{partition}");

        collector.MeasurementsFor(CollectionPagingTelemetry.RequestedPageSizeName).Should().BeEmpty();
        collector.MeasurementsFor(CollectionPagingTelemetry.ReturnedPageSizeName).Should().BeEmpty();

        collector.AllMeasurements.Should().OnlyContain(measurement => HasExactlyTheAllowedTags(measurement));
    }

    // A rejection did no backend work, so a duration sample from it would report microseconds as a read
    // latency and drag every percentile down.
    [Test]
    public void It_records_only_the_request_counter_for_a_validation_rejection()
    {
        using MetricCollector collector = new();
        CollectionPagingTelemetry telemetry = collector.CreateTelemetry();

        telemetry.RecordValidationRejected(
            CollectionPagingTelemetryContext.ForPagingMode(
                CollectionPagingTelemetryLabel.CursorPagingMode,
                CollectionPagingTelemetryLabel.NoCommandCategory,
                SqlDialect.Pgsql,
                CollectionPagingTelemetryLabel.ValidationRejectedOutcome
            )
        );

        MetricMeasurement request = collector.Single(CollectionPagingTelemetry.RequestCounterName);
        request.Tags["outcome"].Should().Be("validation_rejected");
        request.Tags["command_category"].Should().Be("none");

        collector.MeasurementsFor(CollectionPagingTelemetry.DurationName).Should().BeEmpty();
        collector.MeasurementsFor(CollectionPagingTelemetry.RequestedPageSizeName).Should().BeEmpty();
        collector.MeasurementsFor(CollectionPagingTelemetry.ReturnedPageSizeName).Should().BeEmpty();
        collector.MeasurementsFor(CollectionPagingTelemetry.RequestedPartitionCountName).Should().BeEmpty();
        collector.MeasurementsFor(CollectionPagingTelemetry.ReturnedPartitionCountName).Should().BeEmpty();
    }

    // A failure produced no page, and recording zero returned items for it would be indistinguishable
    // from a successful empty page in the requested-versus-returned gap operators watch.
    [Test]
    public void It_suppresses_the_returned_histograms_when_nothing_was_produced()
    {
        using MetricCollector collector = new();
        CollectionPagingTelemetry telemetry = collector.CreateTelemetry();

        telemetry.RecordPage(
            FailureContext(CollectionPagingTelemetryLabel.TraditionalPagingMode),
            TimeSpan.FromMilliseconds(5),
            requestedPageSize: 25,
            returnedPageSize: null
        );
        telemetry.RecordPartitions(
            FailureContext(CollectionPagingTelemetryLabel.PartitionPagingMode),
            TimeSpan.FromMilliseconds(5),
            requestedPartitionCount: 4,
            returnedPartitionCount: null
        );

        collector.MeasurementsFor(CollectionPagingTelemetry.RequestedPageSizeName).Should().ContainSingle();
        collector.MeasurementsFor(CollectionPagingTelemetry.ReturnedPageSizeName).Should().BeEmpty();
        collector
            .MeasurementsFor(CollectionPagingTelemetry.RequestedPartitionCountName)
            .Should()
            .ContainSingle();
        collector.MeasurementsFor(CollectionPagingTelemetry.ReturnedPartitionCountName).Should().BeEmpty();
        collector.MeasurementsFor(CollectionPagingTelemetry.RequestCounterName).Should().HaveCount(2);
    }

    // An empty page that really executed is a success with zero items, which is a different fact from a
    // failure that produced no page at all. Zero must therefore be recorded, not suppressed.
    [Test]
    public void It_records_a_zero_returned_count_that_a_successful_empty_result_produced()
    {
        using MetricCollector collector = new();
        CollectionPagingTelemetry telemetry = collector.CreateTelemetry();

        telemetry.RecordPage(
            CollectionPagingTelemetryContext.ForPaging(
                TraditionalPaging,
                CollectionPagingTelemetryLabel.PageCommandCategory,
                SqlDialect.Pgsql,
                CollectionPagingTelemetryLabel.SuccessOutcome
            ),
            TimeSpan.Zero,
            requestedPageSize: 25,
            returnedPageSize: 0
        );

        collector.Single(CollectionPagingTelemetry.ReturnedPageSizeName).IntValue.Should().Be(0);
        collector.Single(CollectionPagingTelemetry.DurationName).DoubleValue.Should().Be(0);
    }

    [TestCase(false, "traditional")]
    [TestCase(true, "cursor")]
    public void It_maps_the_paging_mode_from_the_resolved_collection_paging(bool cursor, string expected)
    {
        CollectionPagingTelemetryContext context = CollectionPagingTelemetryContext.ForPaging(
            cursor ? CursorPaging : TraditionalPaging,
            CollectionPagingTelemetryLabel.PageCommandCategory,
            SqlDialect.Pgsql,
            CollectionPagingTelemetryLabel.SuccessOutcome
        );

        context.PagingMode.Should().Be(expected);
    }

    [Test]
    public void It_maps_every_provider_including_the_unresolved_mapping_set()
    {
        CollectionPagingTelemetryContext.ProviderLabel(SqlDialect.Pgsql).Should().Be("postgresql");
        CollectionPagingTelemetryContext.ProviderLabel(SqlDialect.Mssql).Should().Be("sqlserver");
        CollectionPagingTelemetryContext.ProviderLabel(null).Should().Be("unknown");
    }

    // A dialect the contract does not name must fail rather than mint a new provider label, which would
    // silently widen the dimension past what the operator documentation lists.
    [Test]
    public void It_rejects_a_dialect_outside_the_documented_provider_set()
    {
        var act = () =>
            CollectionPagingTelemetryContext.ForPagingMode(
                CollectionPagingTelemetryLabel.CursorPagingMode,
                CollectionPagingTelemetryLabel.PageCommandCategory,
                (SqlDialect)999,
                CollectionPagingTelemetryLabel.SuccessOutcome
            );

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void It_round_trips_every_documented_outcome_to_a_tag_value()
    {
        string[] outcomes =
        [
            CollectionPagingTelemetryLabel.SuccessOutcome,
            CollectionPagingTelemetryLabel.TerminalPageOutcome,
            CollectionPagingTelemetryLabel.EarlyEmptyOutcome,
            CollectionPagingTelemetryLabel.ValidationRejectedOutcome,
            CollectionPagingTelemetryLabel.NotAuthorizedOutcome,
            CollectionPagingTelemetryLabel.NotImplementedOutcome,
            CollectionPagingTelemetryLabel.SecurityConfigurationOutcome,
            CollectionPagingTelemetryLabel.RetryExhaustedOutcome,
            CollectionPagingTelemetryLabel.UnknownFailureOutcome,
            CollectionPagingTelemetryLabel.ExecutionExceptionOutcome,
        ];
        using MetricCollector collector = new();
        CollectionPagingTelemetry telemetry = collector.CreateTelemetry();

        foreach (string outcome in outcomes)
        {
            telemetry.RecordValidationRejected(
                CollectionPagingTelemetryContext.ForPagingMode(
                    CollectionPagingTelemetryLabel.TraditionalPagingMode,
                    CollectionPagingTelemetryLabel.NoCommandCategory,
                    SqlDialect.Pgsql,
                    outcome
                )
            );
        }

        collector
            .MeasurementsFor(CollectionPagingTelemetry.RequestCounterName)
            .Select(measurement => measurement.Tags["outcome"])
            .Should()
            .BeEquivalentTo(outcomes);
    }

    [Test]
    public void It_round_trips_every_documented_command_category_to_a_tag_value()
    {
        string[] commandCategories =
        [
            CollectionPagingTelemetryLabel.PageCommandCategory,
            CollectionPagingTelemetryLabel.PageWithCountCommandCategory,
            CollectionPagingTelemetryLabel.BoundaryCommandCategory,
            CollectionPagingTelemetryLabel.NoCommandCategory,
        ];
        using MetricCollector collector = new();
        CollectionPagingTelemetry telemetry = collector.CreateTelemetry();

        foreach (string commandCategory in commandCategories)
        {
            telemetry.RecordValidationRejected(
                CollectionPagingTelemetryContext.ForPagingMode(
                    CollectionPagingTelemetryLabel.TraditionalPagingMode,
                    commandCategory,
                    SqlDialect.Pgsql,
                    CollectionPagingTelemetryLabel.SuccessOutcome
                )
            );
        }

        collector
            .MeasurementsFor(CollectionPagingTelemetry.RequestCounterName)
            .Select(measurement => measurement.Tags["command_category"])
            .Should()
            .BeEquivalentTo(commandCategories);
    }

    // The cardinality guard. A fifth dimension multiplies every stored time series, so adding one has to
    // be a deliberate edit here rather than something a caller can do by passing another tag.
    [Test]
    public void It_emits_exactly_the_four_documented_dimensions_on_every_instrument()
    {
        using MetricCollector collector = new();
        CollectionPagingTelemetry telemetry = collector.CreateTelemetry();

        telemetry.RecordPage(
            CollectionPagingTelemetryContext.ForPaging(
                TraditionalPaging,
                CollectionPagingTelemetryLabel.PageWithCountCommandCategory,
                SqlDialect.Pgsql,
                CollectionPagingTelemetryLabel.TerminalPageOutcome
            ),
            TimeSpan.FromMilliseconds(9),
            requestedPageSize: 25,
            returnedPageSize: 25
        );
        telemetry.RecordPartitions(
            CollectionPagingTelemetryContext.ForPagingMode(
                CollectionPagingTelemetryLabel.PartitionPagingMode,
                CollectionPagingTelemetryLabel.BoundaryCommandCategory,
                SqlDialect.Mssql,
                CollectionPagingTelemetryLabel.EarlyEmptyOutcome
            ),
            TimeSpan.FromMilliseconds(9),
            requestedPartitionCount: 4,
            returnedPartitionCount: 0
        );
        telemetry.RecordValidationRejected(FailureContext(CollectionPagingTelemetryLabel.CursorPagingMode));

        collector
            .AllMeasurements.Should()
            .HaveCount(9)
            .And.OnlyContain(measurement => HasExactlyTheAllowedTags(measurement));
    }

    // The bound this layer guarantees: a label outside the dimension's allowed set never becomes a tag.
    // Length and character class are not the property that matters — a caller passing request-derived
    // text would add one tag set per distinct value however short and clean each one was — so the check
    // is membership and the answer is refusal rather than a reshaped label.
    [Test]
    public void It_refuses_a_label_outside_the_bounded_set()
    {
        var act = () =>
            CollectionPagingTelemetryContext.ForPagingMode(
                CollectionPagingTelemetryLabel.CursorPagingMode,
                CollectionPagingTelemetryLabel.NoCommandCategory,
                SqlDialect.Pgsql,
                "Unsafe\r\n\t{template}" + new string('x', 200)
            );

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("outcome");
    }

    // The refused label reaches the message the emission sites log, so it carries neither structured-log
    // template syntax nor the whole of an arbitrarily long value.
    [Test]
    public void It_sanitizes_and_bounds_a_refused_label_before_naming_it()
    {
        var act = () =>
            CollectionPagingTelemetryContext.ForPagingMode(
                CollectionPagingTelemetryLabel.CursorPagingMode,
                CollectionPagingTelemetryLabel.NoCommandCategory,
                SqlDialect.Pgsql,
                "Unsafe\r\n\t{template}" + new string('x', 200)
            );

        string message = act.Should().Throw<ArgumentException>().Which.Message;

        message.Should().NotContain("\n").And.NotContain("\r").And.NotContain("\t");
        message.Should().NotContain("{").And.NotContain("}");
        message.Should().NotContain(new string('x', 65));
    }

    // Each dimension carries its own set, so a value that is bounded but belongs to another dimension is
    // refused too. Without this a reordered argument list would emit a tag set the contract never
    // describes while every cardinality assertion still passed.
    [Test]
    public void It_refuses_a_bounded_label_belonging_to_another_dimension()
    {
        var act = () =>
            CollectionPagingTelemetryContext.ForPagingMode(
                CollectionPagingTelemetryLabel.SuccessOutcome,
                CollectionPagingTelemetryLabel.NoCommandCategory,
                SqlDialect.Pgsql,
                CollectionPagingTelemetryLabel.ValidationRejectedOutcome
            );

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("pagingMode");
    }

    // The sets are the contract's Dimensions table. A constant added to the label class but left out of
    // its set would be refused at every emission site and lost as a swallowed warning, so the two are
    // pinned to each other here rather than left to agree by inspection.
    [Test]
    public void It_allows_exactly_the_documented_dimension_values()
    {
        CollectionPagingTelemetryLabel
            .PagingModes.Should()
            .BeEquivalentTo("traditional", "cursor", "partition");
        CollectionPagingTelemetryLabel
            .CommandCategories.Should()
            .BeEquivalentTo("page", "page_with_count", "boundary", "none");
        CollectionPagingTelemetryLabel
            .Providers.Should()
            .BeEquivalentTo("postgresql", "sqlserver", "unknown");
        CollectionPagingTelemetryLabel
            .Outcomes.Should()
            .BeEquivalentTo(
                "success",
                "terminal_page",
                "early_empty",
                "validation_rejected",
                "not_authorized",
                "not_implemented",
                "security_configuration",
                "retry_exhausted",
                "unknown_failure",
                "execution_exception"
            );
    }

    // The strings an operator types into an AddMeter call and into every dashboard query. They are the
    // one part of the published contract that reading a constant back cannot check: every other test
    // here reaches an instrument through the same constant that named it, so both sides of the
    // comparison move together under a rename and no assertion notices. Spelled as literals from the
    // Instruments table and the Collecting These Metrics section of docs/PAGING-TELEMETRY.md, for the
    // same reason the dimension values above are.
    [Test]
    public void It_publishes_exactly_the_documented_meter_and_instrument_names()
    {
        CollectionPagingTelemetry.MeterName.Should().Be("EdFi.DataManagementService.CollectionPaging");
        CollectionPagingTelemetry.RequestCounterName.Should().Be("edfi.dms.collection_paging.requests");
        CollectionPagingTelemetry.DurationName.Should().Be("edfi.dms.collection_paging.duration");
        CollectionPagingTelemetry
            .RequestedPageSizeName.Should()
            .Be("edfi.dms.collection_paging.page_size.requested");
        CollectionPagingTelemetry
            .ReturnedPageSizeName.Should()
            .Be("edfi.dms.collection_paging.page_size.returned");
        CollectionPagingTelemetry
            .RequestedPartitionCountName.Should()
            .Be("edfi.dms.collection_paging.partition_count.requested");
        CollectionPagingTelemetry
            .ReturnedPartitionCountName.Should()
            .Be("edfi.dms.collection_paging.partition_count.returned");
    }

    // The meter name is published only by the parameterless constructor, which is the one dependency
    // injection selects and the only one that reaches the process-static meter. Every other test here
    // supplies its own meter, so pinning the constant alone would prove a constant has a value without
    // proving the meter an operator subscribes to carries it.
    [Test]
    public void It_publishes_on_the_documented_meter_from_the_production_constructor()
    {
        List<string> observedMeterNames = [];
        using MeterListener listener = new()
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Name == CollectionPagingTelemetry.RequestCounterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, _, _, _) => observedMeterNames.Add(instrument.Meter.Name)
        );
        listener.Start();

        CollectionPagingTelemetry telemetry = new();
        telemetry.RecordValidationRejected(
            FailureContext(CollectionPagingTelemetryLabel.TraditionalPagingMode)
        );

        // OnlyContain rather than a single expected element: the meter is process-static, so a
        // concurrent test recording on it would be observed here too — and would carry this same name.
        observedMeterNames
            .Should()
            .NotBeEmpty("the production constructor must publish the request counter")
            .And.OnlyContain(name => name == "EdFi.DataManagementService.CollectionPaging");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void It_rejects_a_missing_label(string? label)
    {
        var act = () =>
            CollectionPagingTelemetryContext.ForPagingMode(
                CollectionPagingTelemetryLabel.CursorPagingMode,
                CollectionPagingTelemetryLabel.NoCommandCategory,
                SqlDialect.Pgsql,
                label!
            );

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_a_negative_duration_and_negative_counts()
    {
        using MetricCollector collector = new();
        CollectionPagingTelemetry telemetry = collector.CreateTelemetry();
        CollectionPagingTelemetryContext context = FailureContext(
            CollectionPagingTelemetryLabel.TraditionalPagingMode
        );

        AssertRejectsInvalidMeasurements(telemetry, context);
        collector.AllMeasurements.Should().BeEmpty();
    }

    [Test]
    public void It_validates_identically_and_emits_nothing_on_the_no_op()
    {
        using MetricCollector collector = new();
        NoOpCollectionPagingTelemetry telemetry = NoOpCollectionPagingTelemetry.Instance;
        CollectionPagingTelemetryContext context = FailureContext(
            CollectionPagingTelemetryLabel.TraditionalPagingMode
        );

        AssertRejectsInvalidMeasurements(telemetry, context);

        telemetry.RecordPage(context, TimeSpan.FromMilliseconds(5), 25, 25);
        telemetry.RecordPartitions(context, TimeSpan.FromMilliseconds(5), 4, 4);
        telemetry.RecordValidationRejected(context);

        collector.AllMeasurements.Should().BeEmpty();
    }

    [Test]
    public void It_resolves_the_telemetry_as_a_singleton_from_the_core_registration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var services = new ServiceCollection();

        services.AddDmsDefaultConfiguration(
            new LoggerConfiguration().CreateLogger(),
            configuration.GetSection("CircuitBreaker"),
            configuration.GetSection("DeadlockRetry"),
            false
        );

        using var provider = services.BuildServiceProvider();
        var telemetry = provider.GetRequiredService<ICollectionPagingTelemetry>();

        telemetry.Should().BeOfType<CollectionPagingTelemetry>();
        provider.GetRequiredService<ICollectionPagingTelemetry>().Should().BeSameAs(telemetry);
    }

    private static void AssertRejectsInvalidMeasurements(
        ICollectionPagingTelemetry telemetry,
        CollectionPagingTelemetryContext context
    )
    {
        var negativeDuration = () => telemetry.RecordPage(context, TimeSpan.FromMilliseconds(-1), 25, 25);
        var negativeRequestedPageSize = () => telemetry.RecordPage(context, TimeSpan.Zero, -1, 25);
        var negativeReturnedPageSize = () => telemetry.RecordPage(context, TimeSpan.Zero, 25, -1);
        var negativePartitionDuration = () =>
            telemetry.RecordPartitions(context, TimeSpan.FromMilliseconds(-1), 4, 4);
        var negativeRequestedPartitionCount = () => telemetry.RecordPartitions(context, TimeSpan.Zero, -1, 4);
        var negativeReturnedPartitionCount = () => telemetry.RecordPartitions(context, TimeSpan.Zero, 4, -1);
        var nullPageContext = () => telemetry.RecordPage(null!, TimeSpan.Zero, 25, 25);
        var nullPartitionContext = () => telemetry.RecordPartitions(null!, TimeSpan.Zero, 4, 4);
        var nullRejectionContext = () => telemetry.RecordValidationRejected(null!);

        // The parameter each fault names, not merely that one was raised. This helper runs against both
        // the recording implementation and the no-op, so asserting the name here is what makes "the
        // no-op validates exactly as the recording implementation does" a checked claim rather than a
        // comment — and a shared validation helper inside either one, which would have to name its
        // parameters something neither caller uses, fails this.
        AssertParamName(negativeDuration, "duration");
        AssertParamName(negativeRequestedPageSize, "requestedPageSize");
        AssertParamName(negativeReturnedPageSize, "returnedPageSize");
        AssertParamName(negativePartitionDuration, "duration");
        AssertParamName(negativeRequestedPartitionCount, "requestedPartitionCount");
        AssertParamName(negativeReturnedPartitionCount, "returnedPartitionCount");
        nullPageContext.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("context");
        nullPartitionContext.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("context");
        nullRejectionContext.Should().Throw<ArgumentNullException>().And.ParamName.Should().Be("context");
    }

    private static void AssertParamName(Action act, string expectedParameterName) =>
        act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be(expectedParameterName);

    private static CollectionPagingTelemetryContext FailureContext(string pagingMode) =>
        CollectionPagingTelemetryContext.ForPagingMode(
            pagingMode,
            CollectionPagingTelemetryLabel.NoCommandCategory,
            SqlDialect.Pgsql,
            CollectionPagingTelemetryLabel.UnknownFailureOutcome
        );

    private static bool HasExactlyTheAllowedTags(MetricMeasurement measurement) =>
        measurement.Tags.Count == AllowedTagKeys.Length
        && Array.TrueForAll(AllowedTagKeys, measurement.Tags.ContainsKey);

    /// <summary>
    /// A MeterListener-backed collector over a per-test meter.
    /// </summary>
    /// <remarks>
    /// Carries an int callback alongside the long and double ones because the page-size and
    /// partition-count instruments are <c>Histogram&lt;int&gt;</c>; without it those measurements would be
    /// invisible and every assertion about them would pass vacuously. The unit is captured from the
    /// instrument so a unit change is a test failure rather than a dashboard that silently rescales.
    /// </remarks>
    private sealed class MetricCollector : IDisposable
    {
        private readonly Meter _meter = new($"CollectionPagingTelemetryTests.{Guid.NewGuid()}");
        private readonly MeterListener _listener = new();
        private readonly List<MetricMeasurement> _measurements = [];

        public MetricCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == _meter.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>(
                (instrument, measurement, tags, _) =>
                    _measurements.Add(
                        new MetricMeasurement(
                            instrument.Name,
                            instrument.Unit,
                            LongValue: measurement,
                            DoubleValue: null,
                            IntValue: null,
                            Tags: CopyTags(tags)
                        )
                    )
            );
            _listener.SetMeasurementEventCallback<double>(
                (instrument, measurement, tags, _) =>
                    _measurements.Add(
                        new MetricMeasurement(
                            instrument.Name,
                            instrument.Unit,
                            LongValue: null,
                            DoubleValue: measurement,
                            IntValue: null,
                            Tags: CopyTags(tags)
                        )
                    )
            );
            _listener.SetMeasurementEventCallback<int>(
                (instrument, measurement, tags, _) =>
                    _measurements.Add(
                        new MetricMeasurement(
                            instrument.Name,
                            instrument.Unit,
                            LongValue: null,
                            DoubleValue: null,
                            IntValue: measurement,
                            Tags: CopyTags(tags)
                        )
                    )
            );
            _listener.Start();
        }

        public CollectionPagingTelemetry CreateTelemetry() => new(_meter);

        public IReadOnlyList<MetricMeasurement> AllMeasurements => _measurements;

        public MetricMeasurement[] MeasurementsFor(string instrumentName) =>
            [.. _measurements.Where(measurement => measurement.InstrumentName == instrumentName)];

        public MetricMeasurement Single(string instrumentName) =>
            MeasurementsFor(instrumentName).Should().ContainSingle().Which;

        public void Dispose()
        {
            _listener.Dispose();
            _meter.Dispose();
        }

        private static Dictionary<string, object?> CopyTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Dictionary<string, object?> result = [];
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                result[tag.Key] = tag.Value;
            }

            return result;
        }
    }

    private sealed record MetricMeasurement(
        string InstrumentName,
        string? Unit,
        long? LongValue,
        double? DoubleValue,
        int? IntValue,
        Dictionary<string, object?> Tags
    );
}
