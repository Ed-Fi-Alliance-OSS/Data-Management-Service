// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SQL Server twin of the public partitions contract, restricted to the sizing rows: how many tokens a
/// requested count produces and which ranges they cover. Boundary counting and starting-identity
/// selection are compiled and executed per provider, so each engine has to be asked.
/// </summary>
/// <remarks>
/// The validation rows are deliberately absent. The change-version, filter, count, and
/// reserved-parameter phases all answer before a provider is involved and return the same response
/// from the same code on both engines, so binding them here would duplicate a PostgreSQL answer rather
/// than test SQL Server.
/// </remarks>
public sealed class Given_Mssql_PartitionPublicContract : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    /// <summary>
    /// The mandatory minimum partition size is a multiple of this, so at the deployed value the seeded
    /// collection would be a single partition and the sizing assertions would pass without the
    /// requested count ever mattering.
    /// </summary>
    protected override int? MaximumPageSizeOverride => PartitionPublicContractScenario.HostMaximumPageSize;

    [Test]
    public Task It_covers_the_collection_with_one_partition_when_one_is_requested() =>
        PartitionPublicContractScenario.It_covers_the_collection_with_one_partition_when_one_is_requested(
            Harness
        );

    [Test]
    public Task It_returns_the_same_boundaries_once_the_minimum_partition_size_binds() =>
        PartitionPublicContractScenario.It_returns_the_same_boundaries_once_the_minimum_partition_size_binds(
            Harness
        );
}
