// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Telemetry;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// PostgreSQL end-to-end proof of the partitions endpoint: a token this host emitted is a token this
/// host accepts, and walking every token it hands out covers the collection without returning a
/// document twice. Boundary selection is provider-specific, so the walk is observed on both engines.
/// </summary>
/// <remarks>
/// Leases the descriptor runtime fixture because its ApiSchema declares both the regular resource and
/// the descriptor these walks partition; the walks seed the documents they assert on themselves.
/// </remarks>
public sealed class Given_Postgresql_PartitionEndpoint : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    /// <summary>
    /// The mandatory minimum partition size is a multiple of this, so at the deployed value the seeded
    /// collections below would each be a single partition and every multi-partition assertion would pass
    /// without ever crossing a range boundary.
    /// </summary>
    protected override int? MaximumPageSizeOverride => PartitionEndpointScenario.HostMaximumPageSize;

    /// <summary>
    /// Counts the database commands a read really issued, which is what the telemetry case below asserts
    /// against the design's per-operation literal.
    /// </summary>
    protected override bool CaptureQueryPlans => true;

    [Test]
    public Task It_covers_a_regular_resource_collection_across_its_partitions() =>
        PartitionEndpointScenario.It_covers_a_regular_resource_collection_across_its_partitions(Harness);

    [Test]
    public Task It_covers_a_descriptor_collection_across_its_partitions() =>
        PartitionEndpointScenario.It_covers_a_descriptor_collection_across_its_partitions(Harness);

    [Test]
    public Task It_never_returns_more_partitions_than_requested() =>
        PartitionEndpointScenario.It_never_returns_more_partitions_than_requested(Harness);

    [Test]
    public Task It_partitions_only_the_filtered_candidate_set() =>
        PartitionEndpointScenario.It_partitions_only_the_filtered_candidate_set(Harness);

    [Test]
    public Task It_serves_plain_json_without_paging_headers() =>
        PartitionEndpointScenario.It_serves_plain_json_without_paging_headers(Harness);

    [Test]
    public Task It_returns_an_empty_token_array_for_a_filter_matching_nothing() =>
        PartitionEndpointScenario.It_returns_an_empty_token_array_for_a_filter_matching_nothing(Harness);

    [Test]
    public Task It_refuses_write_methods_with_a_get_only_allow_header() =>
        PartitionEndpointScenario.It_refuses_write_methods_with_a_get_only_allow_header(Harness);

    [Test]
    public Task It_refuses_a_reserved_paging_parameter() =>
        PartitionEndpointScenario.It_refuses_a_reserved_paging_parameter(Harness);

    [Test]
    public Task It_refuses_a_partition_count_outside_the_supported_range() =>
        PartitionEndpointScenario.It_refuses_a_partition_count_outside_the_supported_range(Harness);

    [Test]
    public Task It_leaves_the_neighbouring_route_shapes_unchanged() =>
        PartitionEndpointScenario.It_leaves_the_neighbouring_route_shapes_unchanged(Harness);

    [Test]
    public Task It_emits_bounded_telemetry_for_partition_requests() =>
        PartitionEndpointScenario.It_emits_bounded_telemetry_for_partition_requests(
            Harness,
            CollectionPagingTelemetryLabel.PostgresqlProvider
        );

    [Test]
    public Task It_records_an_early_empty_partition_request_without_a_database_command() =>
        PartitionEndpointScenario.It_records_an_early_empty_partition_request_without_a_database_command(
            Harness,
            CollectionPagingTelemetryLabel.PostgresqlProvider
        );

    [Test]
    public Task It_records_a_partition_validation_rejection_without_reaching_the_backend() =>
        PartitionEndpointScenario.It_records_a_partition_validation_rejection_without_reaching_the_backend(
            Harness,
            CollectionPagingTelemetryLabel.PostgresqlProvider
        );
}
