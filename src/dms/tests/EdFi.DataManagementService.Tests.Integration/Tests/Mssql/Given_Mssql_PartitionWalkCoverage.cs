// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SQL Server twin of the partition tiling proof. Boundary counting, starting-identity selection, and
/// the range seeks the walks perform are all compiled and executed per provider, so the claim that the
/// ranges tile the candidate set has to be made on each engine.
/// </summary>
/// <remarks>
/// The unknown-query-field row is deliberately absent: it is answered before any provider is involved.
/// Leases the cursor-partition-contract fixture for the same reason the PostgreSQL twin does.
/// </remarks>
public sealed class Given_Mssql_PartitionWalkCoverage : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.CursorPartitionContract;

    /// <summary>
    /// The mandatory minimum partition size is a multiple of this, so at the deployed value every seed
    /// below would be a single partition and every coverage and disjointness assertion would hold
    /// vacuously over one range.
    /// </summary>
    protected override int? MaximumPageSizeOverride => PartitionWalkCoverageScenario.HostMaximumPageSize;

    [Test]
    public Task It_covers_a_regular_resource_collection_sequentially() =>
        PartitionWalkCoverageScenario.It_covers_a_regular_resource_collection_sequentially(Harness);

    [Test]
    public Task It_covers_a_regular_resource_collection_in_parallel() =>
        PartitionWalkCoverageScenario.It_covers_a_regular_resource_collection_in_parallel(Harness);

    [Test]
    public Task It_covers_an_extension_resource_collection_sequentially() =>
        PartitionWalkCoverageScenario.It_covers_an_extension_resource_collection_sequentially(Harness);

    [Test]
    public Task It_covers_an_extension_resource_collection_in_parallel() =>
        PartitionWalkCoverageScenario.It_covers_an_extension_resource_collection_in_parallel(Harness);

    [Test]
    public Task It_covers_a_descriptor_collection_sequentially() =>
        PartitionWalkCoverageScenario.It_covers_a_descriptor_collection_sequentially(Harness);

    [Test]
    public Task It_covers_a_descriptor_collection_in_parallel() =>
        PartitionWalkCoverageScenario.It_covers_a_descriptor_collection_in_parallel(Harness);

    [Test]
    public Task It_repeats_a_resource_filter_on_every_page_of_every_partition() =>
        PartitionWalkCoverageScenario.It_repeats_a_resource_filter_on_every_page_of_every_partition(Harness);

    [Test]
    public Task It_repeats_a_change_version_window_on_every_page_of_every_partition() =>
        PartitionWalkCoverageScenario.It_repeats_a_change_version_window_on_every_page_of_every_partition(
            Harness
        );

    [Test]
    public Task It_consumes_a_number_query_key_as_a_filter_on_a_collection_and_as_a_count_on_partitions() =>
        PartitionWalkCoverageScenario.It_consumes_a_number_query_key_as_a_filter_on_a_collection_and_as_a_count_on_partitions(
            Harness
        );
}
