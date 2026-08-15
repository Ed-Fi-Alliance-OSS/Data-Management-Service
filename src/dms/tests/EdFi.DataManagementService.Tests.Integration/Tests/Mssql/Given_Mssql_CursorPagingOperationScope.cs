// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SQL Server twin of the cursor operation-scope proof. Parameter recognition is provider-neutral, but
/// the accepted cursor request now selects a page and emits a continuation from real SQL Server page
/// selection, so the answer is provider-specific and is observed on both engines.
/// </summary>
public sealed class Given_Mssql_CursorPagingOperationScope : MssqlApiIntegrationTestBase
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
