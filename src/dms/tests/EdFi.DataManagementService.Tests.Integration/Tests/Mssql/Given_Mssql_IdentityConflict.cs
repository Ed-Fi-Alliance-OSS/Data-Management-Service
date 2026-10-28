// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SQL Server API contract for natural-key conflicts on POST and PUT: the 409 problem type and the
/// fully qualified duplicate identity names, produced by the real provider's constraint violation.
/// The POST collides on a shared abstract identity that SQL Server maintains with AFTER triggers.
/// </summary>
public sealed class Given_Mssql_IdentityConflict : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    public Task It_reports_non_unique_identity_with_qualified_names_for_a_post_on_a_shared_abstract_identity() =>
        IdentityConflictScenario.It_reports_non_unique_identity_with_qualified_names_for_a_post_on_a_shared_abstract_identity(
            Harness
        );

    [Test]
    public Task It_reports_non_unique_identity_with_qualified_names_for_a_put_that_collides_on_a_natural_key() =>
        IdentityConflictScenario.It_reports_non_unique_identity_with_qualified_names_for_a_put_that_collides_on_a_natural_key(
            Harness
        );
}
