// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SQL Server twin of the ODS comparison, restricted to the groups whose observation is produced by
/// provider work: the page a configured maximum truncates, partition sizing and its boundaries, the
/// filter-versus-count collision over a real candidate set, the range a token names, and the identity
/// maximum the provider's own identity column reaches.
/// </summary>
/// <remarks>
/// The validation, published-metadata, and profile groups are deliberately absent. Request validation,
/// document assembly, and profile resolution all answer before a provider is involved and return the
/// same response from the same code on both engines, so binding them here would duplicate a PostgreSQL
/// answer rather than test SQL Server.
/// </remarks>
public sealed class Given_Mssql_OdsComparison : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.CursorPartitionContract;

    protected override int? MaximumPageSizeOverride => OdsComparisonScenario.HostMaximumPageSize;

    [Test]
    public Task It_matches_the_recorded_ods_outcome_for_the_omitted_limit_default() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "omitted-limit-default");

    [Test]
    public Task It_matches_the_recorded_ods_outcome_for_partition_sizing() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "sizing");

    [Test]
    public Task It_matches_the_recorded_ods_outcome_for_the_number_collision() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "number-collision");

    [Test]
    public Task It_matches_the_recorded_ods_outcome_for_int64_range_bounds() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "int64-bounds");

    [Test]
    public Task It_matches_the_recorded_ods_outcome_at_the_identity_maximum() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "identity-maximum");
}

/// <summary>
/// SQL Server twin of the empty-hydration comparison. The selected maximum the header is gated on is
/// carried out of SQL Server's hydration batch through its <c>OUTPUT</c> clause, so the engine matters.
/// </summary>
public sealed class Given_Mssql_OdsComparisonEmptyHydration : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.CursorPartitionContract;

    protected override int? MaximumPageSizeOverride => OdsComparisonScenario.HostMaximumPageSize;

    protected override bool SuppressHydratedRowsOnce => true;

    [Test]
    public Task It_matches_the_recorded_ods_outcome_for_the_empty_hydration_header() =>
        OdsComparisonScenario.RunGroupAsync(Harness, "empty-hydration");
}
