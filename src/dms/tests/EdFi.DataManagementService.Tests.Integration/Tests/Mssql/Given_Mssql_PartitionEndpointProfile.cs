// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SQL Server twin of the partitions profile behavior. Profile resolution happens in Core ahead of any
/// provider work, but the success case reaches real SQL Server query execution, so both engines run it.
/// </summary>
public sealed class Given_Mssql_PartitionEndpointProfile : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    /// <summary>
    /// Assigning the write-only profile is what puts a request naming no profile onto the implicit
    /// selection path, where a GET must exclude it for having no readable content type.
    /// </summary>
    protected override IReadOnlyList<string> AssignedProfileNames =>
        [PartitionProfileScenario.AssignedWriteOnlyProfileName];

    [Test]
    public Task It_refuses_a_write_only_profile_exactly_as_the_collection_get_does() =>
        PartitionProfileScenario.It_refuses_a_write_only_profile_exactly_as_the_collection_get_does(Harness);

    [Test]
    public Task It_serves_partitions_unfiltered_when_an_assigned_profile_is_not_readable() =>
        PartitionProfileScenario.It_serves_partitions_unfiltered_when_an_assigned_profile_is_not_readable(
            Harness
        );
}
