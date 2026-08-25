// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SQL Server proof of what a walk observes when the collection changes underneath it: a later insert
/// reaches only the final unbounded partition, a member deleted before its page is simply absent, and a
/// filter is reapplied on every request rather than frozen into the token. Boundary placement and range
/// execution are provider-specific, so each engine is asked.
/// </summary>
/// <remarks>
/// Leases the cursor-partition-contract fixture for its filterable extension collection. Nothing here
/// asserts snapshot consistency, and no test uses a sleep or a race.
/// </remarks>
public sealed class Given_Mssql_CursorPartitionConcurrency : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.CursorPartitionContract;

    /// <summary>
    /// The mandatory minimum partition size is a multiple of this, so at the deployed value the seeds
    /// would be a single unbounded partition and the bounded-partition claims would hold vacuously.
    /// </summary>
    protected override int? MaximumPageSizeOverride => CursorPartitionConcurrencyScenario.HostMaximumPageSize;

    [Test]
    public Task It_admits_a_later_insert_only_to_the_final_unbounded_partition() =>
        CursorPartitionConcurrencyScenario.It_admits_a_later_insert_only_to_the_final_unbounded_partition(
            Harness
        );

    [Test]
    public Task It_drops_a_member_deleted_before_its_page_was_reached() =>
        CursorPartitionConcurrencyScenario.It_drops_a_member_deleted_before_its_page_was_reached(Harness);

    [Test]
    public Task It_reevaluates_a_filter_for_a_document_whose_eligibility_changed() =>
        CursorPartitionConcurrencyScenario.It_reevaluates_a_filter_for_a_document_whose_eligibility_changed(
            Harness
        );
}
