// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// PostgreSQL proof that a page whose rows vanished before its body was built still advances a cursor
/// walk. The selected maximum the header is gated on comes from real PostgreSQL page selection, so the
/// answer is provider-specific and is observed on both engines.
/// </summary>
/// <remarks>
/// This is the only fixture that opts into hydrated-row suppression, and it is scoped to this fixture
/// alone: the base default leaves every other fixture's hydration untouched.
/// </remarks>
public sealed class Given_Postgresql_CursorEmptyHydration : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.DescriptorRuntime;

    protected override bool SuppressHydratedRowsOnce => true;

    [Test]
    public Task It_advances_past_a_page_whose_rows_vanished_before_hydration() =>
        CursorEmptyHydrationScenario.It_advances_past_a_page_whose_rows_vanished_before_hydration(Harness);
}
