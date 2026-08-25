// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// PostgreSQL end-to-end proof that the partitions endpoint ranks, cuts, and hands out
/// <c>ContentVersion</c> boundaries for a max-bearing change-version window, and that walking every
/// partition covers the window exactly once and reaches nothing above it. Boundary selection is
/// compiled per provider, so the answer is observed on both engines.
/// </summary>
/// <remarks>
/// Leases the descriptor runtime fixture because its ApiSchema declares both the regular resource and
/// the descriptor these boundaries are calculated over; the tests seed the documents they assert on
/// themselves.
/// </remarks>
public sealed class Given_Postgresql_WindowedPartitionAnchoring : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    /// <summary>
    /// The mandatory minimum partition size is a multiple of this, so at the deployed value the seeded
    /// window would be a single partition and every boundary assertion would pass without a cut ever
    /// being made.
    /// </summary>
    protected override int? MaximumPageSizeOverride => WindowedPartitionAnchoringScenario.HostMaximumPageSize;

    [Test]
    public Task It_partitions_a_windowed_regular_resource_collection_by_content_version() =>
        WindowedPartitionAnchoringScenario.It_partitions_a_windowed_regular_resource_collection_by_content_version(
            Harness
        );

    [Test]
    public Task It_partitions_a_windowed_descriptor_collection_by_content_version() =>
        WindowedPartitionAnchoringScenario.It_partitions_a_windowed_descriptor_collection_by_content_version(
            Harness
        );
}
