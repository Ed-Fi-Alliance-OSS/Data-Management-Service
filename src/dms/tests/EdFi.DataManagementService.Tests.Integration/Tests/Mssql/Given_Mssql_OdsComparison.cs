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
/// provider work: partition sizing and its boundaries in both the supplied-count and omitted-count
/// forms, the filter-versus-count collision over a real candidate set, the range a token names, and the
/// identity maximum the provider's own identity column reaches.
/// </summary>
/// <remarks>
/// The validation, published-metadata, and profile groups are deliberately absent. Request validation,
/// document assembly, and profile resolution all answer before a provider is involved and return the
/// same response from the same code on both engines, so binding them here would duplicate a PostgreSQL
/// answer rather than test SQL Server.
/// </remarks>
public sealed class Given_Mssql_OdsComparison : MssqlApiIntegrationTestBase
{
    /// <summary>
    /// The groups this binding executes, deliberately a subset of the PostgreSQL set: only the groups
    /// whose observation is produced by provider work. Declared as data so the case-definition guardrail
    /// can hold every bound group against the declared group set.
    /// </summary>
    internal static readonly string[] BoundGroups =
    [
        "sizing",
        "sizing-default-count",
        "number-collision",
        "int64-bounds",
        "identity-maximum",
    ];

    protected override FixtureKey Fixture => FixtureKey.CursorPartitionContract;

    protected override int? MaximumPageSizeOverride => OdsComparisonScenario.HostMaximumPageSize;

    [TestCaseSource(nameof(BoundGroups))]
    public Task It_matches_the_recorded_ods_outcomes_for_the_group(string group) =>
        OdsComparisonScenario.RunGroupAsync(Harness, group, OdsComparisonScenario.HostMaximumPageSize);
}

/// <summary>
/// SQL Server twin of the empty-hydration comparison. The selected maximum the header is gated on is
/// carried out of SQL Server's hydration batch through its <c>OUTPUT</c> clause, so the engine matters.
/// </summary>
public sealed class Given_Mssql_OdsComparisonEmptyHydration : MssqlApiIntegrationTestBase
{
    /// <summary>The group this binding executes. See <see cref="Given_Mssql_OdsComparison.BoundGroups" />.</summary>
    internal static readonly string[] BoundGroups = ["empty-hydration"];

    protected override FixtureKey Fixture => FixtureKey.CursorPartitionContract;

    protected override int? MaximumPageSizeOverride => OdsComparisonScenario.HostMaximumPageSize;

    protected override bool SuppressHydratedRowsOnce => true;

    [TestCaseSource(nameof(BoundGroups))]
    public Task It_matches_the_recorded_ods_outcomes_for_the_group(string group) =>
        OdsComparisonScenario.RunGroupAsync(Harness, group, OdsComparisonScenario.HostMaximumPageSize);
}

/// <summary>
/// SQL Server twin of the omitted-limit boundary, on a host left at its deployed maximum page size so
/// the observation is a real page rather than a test override.
/// </summary>
public sealed class Given_Mssql_OdsComparisonDefaultPageSize : MssqlApiIntegrationTestBase
{
    /// <summary>The group this binding executes. See <see cref="Given_Mssql_OdsComparison.BoundGroups" />.</summary>
    internal static readonly string[] BoundGroups = ["omitted-limit-default"];

    protected override FixtureKey Fixture => FixtureKey.CursorPartitionContract;

    [TestCaseSource(nameof(BoundGroups))]
    public Task It_matches_the_recorded_ods_outcomes_for_the_group(string group) =>
        OdsComparisonScenario.RunGroupAsync(Harness, group, OdsComparisonScenario.DeployedMaximumPageSize);
}
