// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Postgresql;

namespace EdFi.DataManagementService.Tests.Integration;

/// <summary>
/// Assembly-level NUnit setup fixture that disposes every cached per-fixture
/// baseline database after all tests in this assembly finish. The baselines are
/// retained in process-static dictionaries during the run so they can be reused
/// across tests; without this teardown the underlying template databases,
/// snapshots, and idle slot pools would persist past the test host. CI
/// containers mask this, but local repeated runs accumulate resources.
/// </summary>
[SetUpFixture]
public sealed class HarnessAssemblyTeardown
{
    [OneTimeTearDown]
    public async Task DisposeBaselinesAsync()
    {
        await PostgresqlBaselineCache.DisposeAllAsync();
        await MssqlBaselineCache.DisposeAllAsync();
    }
}
