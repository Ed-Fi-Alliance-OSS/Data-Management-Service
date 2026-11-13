// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

[TestFixture]
[Category("DataStoreCanonicalUniquenessUpgrade")]
public class Given_the_canonical_DataStore_uniqueness_migration
{
    private JobUpgradeTestDatabase _database = null!;
    private const string ScriptName =
        "EdFi.DmsConfigurationService.Backend.Postgresql.Deploy.Scripts.0040_Replace_DataStore_Name_Uniqueness.sql";
    private const string Rows =
        """SELECT "Id", "Name", "NameComparisonKey" FROM dmscs."DataStore" ORDER BY "Id";""";
    private const string ConstraintDefinition =
        """SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'dmscs."DataStore"'::regclass AND conname = 'UX_DataStore_TenantId_Name';""";

    [SetUp]
    public void Setup()
    {
        _database = new();
    }

    [TearDown]
    public Task Teardown() => _database is null ? Task.CompletedTask : _database.DropAsync();

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_replaces_only_DataStore_uniqueness_and_recovers_the_0040_journal(bool upgrade)
    {
        (int Id, string Name, string Key)[] legacyRows = [];
        string[] apiDefinitions = [];
        if (upgrade)
        {
            _database.DeployThrough(39);
            await using NpgsqlConnection seed = new(_database.ConnectionString);
            await InsertAsync(seed, " Display CAFÉ ");
            legacyRows = (await seed.QueryAsync<(int Id, string Name, string Key)>(Rows)).ToArray();
            apiDefinitions = (
                await seed.QueryAsync<string>(
                    """SELECT conname || ': ' || pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'dmscs."ApiClient"'::regclass ORDER BY conname;"""
                )
            ).ToArray();
        }
        string[] before = upgrade ? await _database.JournalAsync() : [];
        _database.DeployThrough(40);
        string[] after = await _database.JournalAsync();
        after.Should().Contain(ScriptName);
        if (upgrade)
        {
            after.Except(before).Should().Equal(ScriptName);
        }
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        string definition = await connection.QuerySingleAsync<string>(ConstraintDefinition);
        definition.Should().Be("""UNIQUE NULLS NOT DISTINCT ("TenantId", "NameComparisonKey")""");
        var rows = (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).ToArray();
        if (upgrade)
        {
            rows.Should().Equal(legacyRows);
            (
                await connection.QueryAsync<string>(
                    """SELECT conname || ': ' || pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'dmscs."ApiClient"'::regclass ORDER BY conname;"""
                )
            )
                .Should()
                .Equal(apiDefinitions);
        }
        string[] apiObjects = await _database.ConstraintAndIndexNamesAsync("ApiClient");
        apiObjects.Should().Contain("UX_ApiClient_ApplicationId_Name");
        await connection.ExecuteAsync(
            """DELETE FROM public."dmscs_SchemaVersions" WHERE scriptname = @ScriptName;""",
            new { ScriptName }
        );
        _database.DeployThrough(40);
        _database.DeployThrough(40);
        (await _database.JournalAsync()).Should().Equal(after);
        (await connection.QuerySingleAsync<string>(ConstraintDefinition)).Should().Be(definition);
        (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).Should().Equal(rows);
        (await _database.ConstraintAndIndexNamesAsync("ApiClient")).Should().Equal(apiObjects);
    }

    [TestCase("case")]
    [TestCase("edges")]
    public async Task It_blocks_legacy_collisions_before_drop_with_bounded_ID_only_diagnostics(
        string collision
    )
    {
        _database.DeployThrough(38);
        await using NpgsqlConnection connection = new(_database.ConnectionString);

        _database.DeployThrough(39);
        List<int> ids = [];
        for (int i = 0; i < 12; i++)
        {
            string name = $"private-store-{i}";
            ids.Add(await InsertAsync(connection, name));
            ids.Add(
                await InsertAsync(connection, collision == "case" ? name.ToUpperInvariant() : "\t" + name)
            );
        }
        string[] journal = await _database.JournalAsync();
        string definition = await connection.QuerySingleAsync<string>(ConstraintDefinition);
        var rows = (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).ToArray();

        var failure = _database
            .Deploy(40)
            .Should()
            .BeOfType<DatabaseDeployResult.DatabaseDeployFailure>()
            .Subject;

        failure
            .Error.Message.Should()
            .Contain("24")
            .And.Contain("ids: " + string.Join(", ", ids.Take(20)))
            .And.Contain("4 more")
            .And.Contain("Rename")
            .And.NotContain("private-store")
            .And.NotContain("PRIVATE-STORE");
        (await _database.JournalAsync()).Should().Equal(journal);
        (await connection.QuerySingleAsync<string>(ConstraintDefinition)).Should().Be(definition);
        (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).Should().Equal(rows);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_enforces_canonical_uniqueness_for_direct_inserts_and_renames_including_NULL_tenants(
        bool tenant
    )
    {
        _database.DeployThrough(40);
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        long? tenantId = tenant
            ? await connection.ExecuteScalarAsync<long>(
                """INSERT INTO dmscs."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""",
                new { Name = "one" }
            )
            : null;
        long otherTenant = await connection.ExecuteScalarAsync<long>(
            """INSERT INTO dmscs."Tenant" ("Name") VALUES (@Name) RETURNING "Id";""",
            new { Name = "two" }
        );
        int first = await InsertAsync(connection, "Store", tenantId);
        int second = await InsertAsync(connection, "other", tenantId);
        await InsertAsync(connection, "store", otherTenant);
        foreach (string duplicate in new[] { "store", "\u3000STORE\u00a0" })
        {
            Func<Task> insert = () => InsertAsync(connection, duplicate, tenantId);
            (await insert.Should().ThrowAsync<PostgresException>())
                .Which.Message.Should()
                .Contain("UX_DataStore_TenantId_Name");
            Func<Task> rename = () =>
                connection.ExecuteAsync(
                    """UPDATE dmscs."DataStore" SET "Name" = @Name WHERE "Id" = @Id;""",
                    new { Id = second, Name = duplicate }
                );
            (await rename.Should().ThrowAsync<PostgresException>())
                .Which.Message.Should()
                .Contain("UX_DataStore_TenantId_Name");
        }
        foreach (string distinct in new[] { "café", "cafe", "CAFÉ", "cafe\u0301", "ストア", "\u200Bstore" })
        {
            await InsertAsync(connection, distinct, tenantId);
        }
        (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows))
            .Should()
            .Contain(row => row.Id == first && row.Name == "Store")
            .And.Contain(row => row.Id == second && row.Name == "other");
    }

    [TestCase("without_fold")]
    [TestCase("constant")]
    [TestCase("unprobed_exception")]
    public async Task It_revalidates_already_journaled_0039_definitions_before_replacing_uniqueness(
        string corruption
    )
    {
        _database.DeployThrough(39);
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        string script = DataStoreNameComparisonKeyLegacyMigration.Script;
        script = corruption switch
        {
            "without_fold" => script.Replace("'abcdefghijklmnopqrstuvwxyz'", "'ABCDEFGHIJKLMNOPQRSTUVWXYZ'"),
            "constant" => script.Replace("\"Name\" COLLATE \"C\"", "'constant'::text COLLATE \"C\""),
            "unprobed_exception" => script.Replace(
                "\"Name\" COLLATE \"C\"",
                "(CASE WHEN \"Name\" = 'outside the probe corpus' THEN 'other' ELSE \"Name\" END) COLLATE \"C\""
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };
        await connection.ExecuteAsync("""ALTER TABLE dmscs."DataStore" DROP COLUMN "NameComparisonKey";""");
        await connection.ExecuteAsync(script);
        await InsertAsync(connection, "ordinary");
        await InsertAsync(connection, " Display CAFÉ ");
        string definitions = await connection.QuerySingleAsync<string>(
            """SELECT pg_get_expr(d.adbin, d.adrelid) FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum WHERE a.attrelid = 'dmscs."DataStore"'::regclass AND a.attname = 'NameComparisonKey';"""
        );
        string[] journal = await _database.JournalAsync();
        var rows = (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).ToArray();
        string constraint = await connection.QuerySingleAsync<string>(ConstraintDefinition);

        var failure = _database
            .Deploy(40)
            .Should()
            .BeOfType<DatabaseDeployResult.DatabaseDeployFailure>()
            .Subject;

        failure.Error.Message.Should().Contain("NameComparisonKey").And.Contain("definition");
        (
            await connection.QuerySingleAsync<string>(
                """SELECT pg_get_expr(d.adbin, d.adrelid) FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum WHERE a.attrelid = 'dmscs."DataStore"'::regclass AND a.attname = 'NameComparisonKey';"""
            )
        )
            .Should()
            .Be(definitions);
        (await connection.QuerySingleAsync<string>(ConstraintDefinition)).Should().Be(constraint);
        (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).Should().Equal(rows);
        (await _database.JournalAsync()).Should().Equal(journal);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_refuses_missing_or_custom_uniqueness_without_repair(bool missing)
    {
        _database.DeployThrough(39);
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        await connection.ExecuteAsync(
            """ALTER TABLE dmscs."DataStore" DROP CONSTRAINT "UX_DataStore_TenantId_Name";"""
        );
        if (!missing)
        {
            await connection.ExecuteAsync(
                """ALTER TABLE dmscs."DataStore" ADD CONSTRAINT "UX_DataStore_TenantId_Name" UNIQUE ("Name");"""
            );
        }
        await InsertAsync(connection, "original");
        string[] objects = await _database.ConstraintAndIndexNamesAsync("DataStore");
        string[] journal = await _database.JournalAsync();
        var rows = (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).ToArray();

        var failure = _database
            .Deploy(40)
            .Should()
            .BeOfType<DatabaseDeployResult.DatabaseDeployFailure>()
            .Subject;

        failure.Error.Message.Should().Contain("UX_DataStore_TenantId_Name");
        (await _database.ConstraintAndIndexNamesAsync("DataStore")).Should().Equal(objects);
        (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).Should().Equal(rows);
        (await _database.JournalAsync()).Should().Equal(journal);
    }

    [Test]
    public async Task It_rolls_back_failure_after_dropping_the_constraint_and_can_retry()
    {
        _database.DeployThrough(39);
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        await InsertAsync(connection, "original");
        string definition = await connection.QuerySingleAsync<string>(ConstraintDefinition);
        string[] journal = await _database.JournalAsync();
        var rows = (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).ToArray();
        await connection.ExecuteAsync(
            """
            CREATE FUNCTION public.fail_after_name_constraint_drop() RETURNS event_trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'dmscs."DataStore"'::regclass AND conname = 'UX_DataStore_TenantId_Name') THEN
                    RAISE EXCEPTION 'injected after constraint drop';
                END IF;
            END $$;
            CREATE EVENT TRIGGER fail_after_name_constraint_drop ON ddl_command_end
                WHEN TAG IN ('ALTER TABLE') EXECUTE FUNCTION public.fail_after_name_constraint_drop();
            """
        );

        var failure = _database
            .Deploy(40)
            .Should()
            .BeOfType<DatabaseDeployResult.DatabaseDeployFailure>()
            .Subject;

        failure.Error.Message.Should().Contain("injected after constraint drop");
        (await connection.QuerySingleAsync<string>(ConstraintDefinition)).Should().Be(definition);
        (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).Should().Equal(rows);
        (await _database.JournalAsync()).Should().Equal(journal);
        await connection.ExecuteAsync(
            "DROP EVENT TRIGGER fail_after_name_constraint_drop; DROP FUNCTION public.fail_after_name_constraint_drop();"
        );
        _database.DeployThrough(40);
        (await connection.QuerySingleAsync<string>(ConstraintDefinition))
            .Should()
            .Be("""UNIQUE NULLS NOT DISTINCT ("TenantId", "NameComparisonKey")""");
    }

    private static Task<int> InsertAsync(NpgsqlConnection connection, string name, long? tenantId = null) =>
        connection.ExecuteScalarAsync<int>(
            """INSERT INTO dmscs."DataStore" ("Name", "DataStoreType", "TenantId") VALUES (@Name, 'SQL', @TenantId) RETURNING "Id";""",
            new { Name = name, TenantId = tenantId }
        );
}
