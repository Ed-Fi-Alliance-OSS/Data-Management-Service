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
        var plan = ContainerSetupBase.BuildResetPlan(
            "postgresql",
            "host=localhost;port=5435;database=db;",
            "db"
        );

        plan.Provider.Should().Be(DatabaseResetProvider.Postgres);
        plan.ConnectionString.Should().Be("host=localhost;port=5435;database=db;");
        plan.Sql.Should().Contain("DO $$").And.Contain("TRUNCATE TABLE");
        // The PostgreSQL reset must not be the SQL Server reset.
        plan.Sql.Should().NotContain("DBCC CHECKIDENT");
    }

    [Test]
    public void It_builds_a_sql_server_reset_plan_reusing_the_shared_reset_sql_for_the_mssql_engine()
    {
        var plan = ContainerSetupBase.BuildResetPlan("mssql", "Server=127.0.0.1,1435;Database=db;", "db");

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
        var plan = ContainerSetupBase.BuildResetPlan("mssql", "Server=127.0.0.1,1435;Database=db;", "db");

        plan.Sql.Should()
            .Contain("EffectiveSchema")
            .And.Contain("ResourceKey")
            .And.Contain("SchemaComponent");
    }

    [Test]
    public void It_selects_the_engine_case_insensitively()
    {
        ContainerSetupBase
            .BuildResetPlan("MSSQL", "Server=s;Database=db;", "db")
            .Provider.Should()
            .Be(DatabaseResetProvider.SqlServer);
        ContainerSetupBase
            .BuildResetPlan("PostgreSQL", "host=h;database=db;", "db")
            .Provider.Should()
            .Be(DatabaseResetProvider.Postgres);
    }

    [Test]
    public void It_defaults_to_postgres_for_an_unrecognized_engine()
    {
        ContainerSetupBase
            .BuildResetPlan("", "host=h;database=db;", "db")
            .Provider.Should()
            .Be(DatabaseResetProvider.Postgres);
    }

    [Test]
    public void It_passes_a_custom_postgresql_admin_connection_string_verbatim_to_the_reset_plan()
    {
        // The PostgreSQL reset consumes the opaque admin/reset connection string verbatim (custom host,
        // port, user, password, database, and NoResetOnClose=true) rather than re-deriving them.
        const string customAdmin =
            "host=custom-host;port=6543;username=customuser;password=custompass;database=custom_e2e;NoResetOnClose=true;";

        var plan = ContainerSetupBase.BuildResetPlan("postgresql", customAdmin, "custom_e2e");

        plan.Provider.Should().Be(DatabaseResetProvider.Postgres);
        plan.ConnectionString.Should().Be(customAdmin);
    }

    [Test]
    public void It_passes_a_custom_mssql_admin_connection_string_verbatim_to_the_reset_plan()
    {
        const string customAdmin =
            "Server=custom-host,14333;Database=custom_e2e;User Id=sa;Password=Custom!Pass9;TrustServerCertificate=true;";

        var plan = ContainerSetupBase.BuildResetPlan("mssql", customAdmin, "custom_e2e");

        plan.Provider.Should().Be(DatabaseResetProvider.SqlServer);
        plan.ConnectionString.Should().Be(customAdmin);
    }

    [Test]
    public void It_matches_the_expected_database_name_case_insensitively()
    {
        // A case-only difference between the connection string database and the configured name is
        // accepted (SQL Server is case-insensitive; the E2E database is the same database).
        var action = () => ContainerSetupBase.BuildResetPlan("mssql", "Server=s;Database=DB;", "db");

        action.Should().NotThrow();
    }

    [Test]
    [TestCase("postgresql", "host=h;database=primary_dms;")]
    [TestCase("mssql", "Server=s;Database=primary_dms;")]
    public void It_refuses_to_reset_a_database_other_than_the_configured_e2e_database(
        string engine,
        string connectionString
    )
    {
        var action = () =>
            ContainerSetupBase.BuildResetPlan(engine, connectionString, "edfi_datamanagementservice_e2e");

        action
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*refused*primary_dms*edfi_datamanagementservice_e2e*");
    }

    [Test]
    [TestCase("postgresql", "host=h;port=5435;")]
    [TestCase("mssql", "Server=s,1435;User Id=sa;")]
    public void It_refuses_when_the_admin_connection_string_specifies_no_database(
        string engine,
        string connectionString
    )
    {
        var action = () => ContainerSetupBase.BuildResetPlan(engine, connectionString, "db");

        action.Should().Throw<InvalidOperationException>().WithMessage("*does not specify a database*");
    }

    [Test]
    [TestCase("postgresql")]
    [TestCase("mssql")]
    public void It_refuses_when_the_expected_e2e_database_name_is_empty(string engine)
    {
        var action = () => ContainerSetupBase.BuildResetPlan(engine, "host=h;database=db;", "   ");

        action.Should().Throw<InvalidOperationException>().WithMessage("*database name is empty*");
    }

    [Test]
    [TestCase("postgresql", "host=h;NotARealKeyword=SUPER_SECRET_VALUE;")]
    [TestCase("mssql", "Server=s;NotARealKeyword=SUPER_SECRET_VALUE;")]
    public void It_refuses_a_malformed_connection_string_without_leaking_it(
        string engine,
        string connectionString
    )
    {
        var action = () => ContainerSetupBase.BuildResetPlan(engine, connectionString, "db");

        var assertion = action.Should().Throw<InvalidOperationException>().WithMessage("*malformed*");
        // The safe message must never echo the offending connection string or any value it contains.
        assertion.Which.Message.Should().NotContain("SUPER_SECRET_VALUE");
    }

    [Test]
    public async Task It_dispatches_only_the_sql_server_provider_for_the_mssql_engine()
    {
        DatabaseResetProvider? executedProvider = null;
        int executorInvocations = 0;

        await ContainerSetupBase.ResetDatabaseAsync(
            "mssql",
            "Server=s;Database=db;",
            "db",
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
            "host=h;database=db;",
            "db",
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

    [Test]
    public async Task It_does_not_execute_a_reset_when_the_target_database_is_wrong()
    {
        int executorInvocations = 0;

        var action = async () =>
            await ContainerSetupBase.ResetDatabaseAsync(
                "postgresql",
                "host=h;database=primary_dms;",
                "edfi_datamanagementservice_e2e",
                _ =>
                {
                    executorInvocations++;
                    return Task.CompletedTask;
                }
            );

        await action.Should().ThrowAsync<InvalidOperationException>();
        executorInvocations.Should().Be(0);
    }
}
