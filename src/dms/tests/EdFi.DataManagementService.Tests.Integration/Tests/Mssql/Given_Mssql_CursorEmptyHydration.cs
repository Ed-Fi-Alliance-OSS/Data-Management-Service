// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SQL Server twin of the empty-hydration proof. The selected maximum the continuation is gated on is
/// produced by SQL Server's own page selection and carried out of the hydration batch through its
/// <c>OUTPUT</c> clause, so the answer is provider-specific.
/// </summary>
/// <remarks>
/// This is the only SQL Server fixture that opts into hydrated-row suppression; the base default leaves
/// every other fixture's hydration untouched.
/// </remarks>
public sealed class Given_Mssql_CursorEmptyHydration : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    protected override bool SuppressHydratedRowsOnce => true;

    [Test]
    public Task It_advances_past_a_page_whose_rows_vanished_before_hydration() =>
        CursorEmptyHydrationScenario.It_advances_past_a_page_whose_rows_vanished_before_hydration(Harness);
}
