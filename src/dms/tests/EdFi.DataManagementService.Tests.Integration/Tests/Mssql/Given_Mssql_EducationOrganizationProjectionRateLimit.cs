// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// The projection on SQL Server behind a host rate limit of a few requests, so the limiter's
/// answer is reached without seeding.
/// </summary>
public sealed class Given_Mssql_EducationOrganizationProjectionRateLimit
    : MssqlEducationOrganizationProjectionTestBase
{
    private const int Permits = 2;

    protected override int? RateLimitPermits => Permits;

    [Test]
    public Task It_answers_the_rate_limit_with_the_inherited_problem() =>
        EducationOrganizationProjectionScenario.It_answers_the_rate_limit_with_the_inherited_problem(
            Context,
            Permits
        );
}
