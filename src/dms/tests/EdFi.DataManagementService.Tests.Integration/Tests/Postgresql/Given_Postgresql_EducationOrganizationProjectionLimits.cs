// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// The projection's row cap and lock timeout on PostgreSQL, at the smallest values the settings allow
/// so they are reached quickly.
/// </summary>
public sealed class Given_Postgresql_EducationOrganizationProjectionLimits
    : PostgresqlEducationOrganizationProjectionTestBase
{
    private const int RowCap = 1000;

    protected override int ReadLockTimeoutSeconds => 1;

    protected override int? MaxProjectionRows => RowCap;

    [Test]
    public Task It_refuses_a_set_larger_than_the_row_cap() =>
        EducationOrganizationProjectionScenario.It_refuses_a_set_larger_than_the_row_cap(Context, RowCap);

    [Test]
    public Task It_answers_a_lock_timeout_as_target_unavailable() =>
        EducationOrganizationProjectionScenario.It_answers_a_lock_timeout_as_target_unavailable(Context);
}
