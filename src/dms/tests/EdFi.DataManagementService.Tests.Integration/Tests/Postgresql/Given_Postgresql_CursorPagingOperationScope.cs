// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// PostgreSQL composed proof of which operations page by cursor and of what the read path answers for
/// a cursor request it accepts. Which pipeline recognizes the cursor parameters, and the read path's
/// cursor guard, are both provider-neutral, so there is no SQL Server twin; the leased database is
/// required only because query validation sits behind fingerprint, resource key seed, and mapping set
/// resolution.
/// </summary>
public sealed class Given_Postgresql_CursorPagingOperationScope : PostgresqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    public Task It_rejects_a_page_token_on_a_deletes_request() =>
        CursorPagingOperationScopeScenario.It_rejects_a_page_token_on_a_deletes_request(Harness);

    [Test]
    public Task It_rejects_a_page_size_on_a_key_changes_request() =>
        CursorPagingOperationScopeScenario.It_rejects_a_page_size_on_a_key_changes_request(Harness);

    [Test]
    public Task It_carries_an_accepted_cursor_request_to_the_read_path() =>
        CursorPagingOperationScopeScenario.It_carries_an_accepted_cursor_request_to_the_read_path(Harness);
}
