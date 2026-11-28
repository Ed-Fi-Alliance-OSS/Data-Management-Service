// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Postgresql;

namespace EdFi.DataManagementService.Performance.Harness;

/// <summary>
/// Disposes every cached per-fixture baseline database after the harness's tests finish, as
/// the integration assembly's own teardown does for its tests. NUnit applies a setup fixture
/// only to tests in its own assembly, so without this every harness process (each benchmark
/// side is one) leaves its template and leased databases on the server; they accumulate and
/// keep the database busy between sides, which the benchmark's idle-CPU rule then rejects.
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
