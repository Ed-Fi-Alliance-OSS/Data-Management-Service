// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

[TestFixture]
[Category("ApiIntegration")]
[Category("MssqlIntegration")]
public sealed class Given_Mssql_SchemaRestampMigration
{
    [OneTimeSetUp]
    public void RequireMssqlConfiguration()
    {
        if (BaselineDatabaseConfiguration.MssqlAdminConnectionString is null)
        {
            Assert.Ignore("MssqlAdmin is not configured for SQL Server API integration tests.");
        }
    }

    [Test]
    public async Task It_admits_a_physically_migrated_database_and_preserves_existing_data()
    {
        SchemaRestampMigrationScenario.Evidence evidence = await RunAsync(applyPhysicalMigration: true);
        evidence.BeforeStampStatus.Should().Be(HttpStatusCode.ServiceUnavailable);
        evidence.RestampExitCode.Should().Be(0, evidence.RestampError);
        evidence.ProvisionExitCode.Should().Be(0);
        evidence.ExistingWidgetStatus.Should().Be(HttpStatusCode.OK);
        evidence.ExistingWidget!["widgetId"]!.GetValue<int>().Should().Be(1);
        evidence.ExistingWidget["widgetName"]!.GetValue<string>().Should().Be("before-migration");
        evidence.ExistingWidget["widgetNote"]?.GetValue<string>().Should().BeNull();
        evidence.AfterDocumentRow.Should().Equal(evidence.BeforeDocumentRow);
        evidence.CreateStatus.Should().Be(HttpStatusCode.Created);
        evidence.CreatedWidget!["widgetNote"]!.GetValue<string>().Should().Be("migration-proof");
    }

    [Test]
    public async Task It_does_not_make_an_unmigrated_physical_shape_usable()
    {
        SchemaRestampMigrationScenario.Evidence evidence = await RunAsync(applyPhysicalMigration: false);
        evidence.BeforeStampStatus.Should().Be(HttpStatusCode.ServiceUnavailable);
        evidence.RestampExitCode.Should().Be(0);
        evidence.ProvisionExitCode.Should().Be(-1, "the physical-shape negative case does not run provision");
        evidence.CreateStatus.Should().Be(HttpStatusCode.InternalServerError);
    }

    private static async Task<SchemaRestampMigrationScenario.Evidence> RunAsync(bool applyPhysicalMigration)
    {
        FixtureContext source = FixtureContextLoader.Load(FixtureKey.SchemaRestampSource);
        FixtureContext target = FixtureContextLoader.Load(FixtureKey.SchemaRestampTarget);
        var baseline = await MssqlBaselineCache.CreateOrGetAsync(source);
        await using var lease = await baseline.AcquireRestoredDatabaseAsync();
        return await SchemaRestampMigrationScenario.RunAsync(
            SqlDialect.Mssql,
            "mssql",
            source,
            target,
            lease.Database.ConnectionString,
            applyPhysicalMigration
        );
    }
}
