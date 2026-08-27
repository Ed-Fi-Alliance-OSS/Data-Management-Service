// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SQL Server twin of the windowed partition boundary proof. The ranking, the sizing, and the boundary
/// selection are compiled per provider, so boundaries that tile a window correctly on one engine say
/// nothing about the other.
/// </summary>
/// <remarks>
/// Leases the descriptor runtime fixture because its ApiSchema declares both the regular resource and
/// the descriptor these boundaries are calculated over; the tests seed the documents they assert on
/// themselves.
/// </remarks>
public sealed class Given_Mssql_WindowedPartitionAnchoring : MssqlApiIntegrationTestBase
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

/// <summary>
/// SQL Server proof that windowed boundaries are cut over the authorized candidate relation. Bound to the
/// authorization matrix's base rather than the fixture above, because the claim being made needs the
/// authorization fixture, the real authorization middleware, and a principal that reaches only part of the
/// seed — all of which that base already supplies, at the same lowered page size this scenario walks with.
/// </summary>
[Category("Authorization")]
public sealed class Given_Mssql_WindowedPartitionAnchoring_NamespaceAuthorization
    : MssqlCursorPartitionAuthorizationMatrixTestBase
{
    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        [CursorPartitionAuthorizationMatrixSupport.AuthorizedNamespacePrefix];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        CursorPartitionAuthorizationMatrixSupport.CreateNamespaceReadClaimSetProvider(fixture);

    [Test]
    public Task It_partitions_a_windowed_collection_over_the_authorized_candidate_set() =>
        WindowedPartitionAnchoringScenario.It_partitions_a_windowed_collection_over_the_authorized_candidate_set(
            Harness
        );
}
