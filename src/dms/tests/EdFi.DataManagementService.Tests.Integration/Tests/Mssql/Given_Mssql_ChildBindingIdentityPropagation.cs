// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// DMS-1166 MSSQL API regression: on SQL Server the native ON UPDATE CASCADE
/// reference foreign keys must fan an upstream identity update out to stored
/// child-collection bindings, observable through the public DMS HTTP pipeline.
/// Wired only under <c>Tests/Mssql/</c> as part of the MSSQL API regression suite.
/// </summary>
public sealed class Given_Mssql_ChildBindingIdentityPropagation : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    public Task It_propagates_ClassPeriod_identity_update_into_BellSchedule_child_binding() =>
        ChildBindingIdentityPropagationScenario.It_propagates_ClassPeriod_identity_update_into_BellSchedule_child_binding(
            Harness
        );
}
