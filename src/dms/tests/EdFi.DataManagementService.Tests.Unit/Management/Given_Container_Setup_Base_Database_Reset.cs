// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using EdFi.DataManagementService.Tests.E2E.Management;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit.Management;

[TestFixture]
public class Given_Container_Setup_Base_Database_Reset
{
    [Test]
    public void It_builds_a_postgres_reset_plan_for_the_postgresql_engine()
    {
        var plan = ContainerSetupBase.BuildResetPlan("postgresql", "host=localhost;port=5435;database=db;");

        plan.Provider.Should().Be(DatabaseResetProvider.Postgres);
        plan.ConnectionString.Should().Be("host=localhost;port=5435;database=db;");
        plan.Sql.Should().Contain("DO $$").And.Contain("TRUNCATE TABLE");
        // The PostgreSQL reset must not be the SQL Server reset.
        plan.Sql.Should().NotContain("DBCC CHECKIDENT");
    }

    [Test]
    public void It_builds_a_sql_server_reset_plan_reusing_the_shared_reset_sql_for_the_mssql_engine()
    {
        var plan = ContainerSetupBase.BuildResetPlan("mssql", "Server=127.0.0.1,1435;Database=db;");

        plan.Provider.Should().Be(DatabaseResetProvider.SqlServer);
        plan.ConnectionString.Should().Be("Server=127.0.0.1,1435;Database=db;");
        // Reuses MssqlDatabaseResetSql verbatim (the SQL is not duplicated or altered in the harness).
        plan.Sql.Should()
            .Be(
                MssqlDatabaseResetSql.Build(
                    ("dms", "EffectiveSchema"),
                    ("dms", "ResourceKey"),
                    ("dms", "SchemaComponent")
                )
            );
    }

    [Test]
    public void It_excludes_exactly_the_three_dms_metadata_tables_on_the_sql_server_reset()
    {
        var plan = ContainerSetupBase.BuildResetPlan("mssql", "Server=127.0.0.1,1435;");

        plan.Sql.Should()
            .Contain("EffectiveSchema")
            .And.Contain("ResourceKey")
            .And.Contain("SchemaComponent");
    }

    [Test]
    public void It_selects_the_engine_case_insensitively()
    {
        ContainerSetupBase
            .BuildResetPlan("MSSQL", "cs")
            .Provider.Should()
            .Be(DatabaseResetProvider.SqlServer);
        ContainerSetupBase
            .BuildResetPlan("PostgreSQL", "cs")
            .Provider.Should()
            .Be(DatabaseResetProvider.Postgres);
    }

    [Test]
    public void It_defaults_to_postgres_for_an_unrecognized_engine()
    {
        ContainerSetupBase.BuildResetPlan("", "cs").Provider.Should().Be(DatabaseResetProvider.Postgres);
    }

    [Test]
    public async Task It_dispatches_only_the_sql_server_provider_for_the_mssql_engine()
    {
        DatabaseResetProvider? executedProvider = null;
        int executorInvocations = 0;

        await ContainerSetupBase.ResetDatabaseAsync(
            "mssql",
            "cs",
            plan =>
            {
                executorInvocations++;
                executedProvider = plan.Provider;
                return Task.CompletedTask;
            }
        );

        executorInvocations.Should().Be(1);
        executedProvider.Should().Be(DatabaseResetProvider.SqlServer);
    }

    [Test]
    public async Task It_dispatches_only_the_postgres_provider_for_the_postgresql_engine()
    {
        DatabaseResetProvider? executedProvider = null;
        int executorInvocations = 0;

        await ContainerSetupBase.ResetDatabaseAsync(
            "postgresql",
            "cs",
            plan =>
            {
                executorInvocations++;
                executedProvider = plan.Provider;
                return Task.CompletedTask;
            }
        );

        executorInvocations.Should().Be(1);
        executedProvider.Should().Be(DatabaseResetProvider.Postgres);
    }
}
