// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DataStoreCanonicalUniquenessUpgrade")]
[Category("MssqlIntegration")]
public class Given_the_canonical_DataStore_uniqueness_migration
{
    private JobUpgradeTestDatabase _database = null!;
    private const string ScriptName =
        "EdFi.DmsConfigurationService.Backend.Mssql.Deploy.Scripts.0040_Replace_DataStore_Name_Uniqueness.sql";
    private const string Rows =
        """SELECT Id, Name, CONVERT(varchar(max), NameComparisonKey, 2) FROM dmscs.DataStore ORDER BY Id;""";
    private const string ConstraintDefinition =
        """SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) FROM sys.key_constraints k JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE k.parent_object_id = OBJECT_ID(N'dmscs.DataStore') AND k.name = 'UX_DataStore_TenantId_Name';""";

    [SetUp]
    public void Setup()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require ConnectionStrings__MssqlAdmin."
        );
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
            await using SqlConnection seed = new(_database.ConnectionString);
            await InsertAsync(seed, " Display CAFÉ ");
            legacyRows = (await seed.QueryAsync<(int Id, string Name, string Key)>(Rows)).ToArray();
            apiDefinitions = (
                await seed.QueryAsync<string>(
                    """SELECT CONCAT(k.name, ': ', STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)) FROM sys.key_constraints k JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE k.parent_object_id = OBJECT_ID(N'dmscs.ApiClient') GROUP BY k.name ORDER BY k.name;"""
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
        await using SqlConnection connection = new(_database.ConnectionString);
        string definition = await connection.QuerySingleAsync<string>(ConstraintDefinition);
        definition.Should().Be("TenantId,NameComparisonKey");
        var rows = (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).ToArray();
        if (upgrade)
        {
            rows.Should().Equal(legacyRows);
            (
                await connection.QueryAsync<string>(
                    """SELECT CONCAT(k.name, ': ', STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)) FROM sys.key_constraints k JOIN sys.index_columns ic ON ic.object_id = k.parent_object_id AND ic.index_id = k.unique_index_id JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE k.parent_object_id = OBJECT_ID(N'dmscs.ApiClient') GROUP BY k.name ORDER BY k.name;"""
                )
            )
                .Should()
                .Equal(apiDefinitions);
        }
        string[] apiObjects = await _database.ConstraintAndIndexNamesAsync("ApiClient");
        apiObjects.Should().Contain("UX_ApiClient_ApplicationId_Name");
        await connection.ExecuteAsync(
            """DELETE FROM dbo.dmscs_SchemaVersions WHERE ScriptName = @ScriptName;""",
            new { ScriptName }
        );
        _database.DeployThrough(40);
        _database.DeployThrough(40);
        (await _database.JournalAsync()).Should().Equal(after);
        (await connection.QuerySingleAsync<string>(ConstraintDefinition)).Should().Be(definition);
        (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).Should().Equal(rows);
        (await _database.ConstraintAndIndexNamesAsync("ApiClient")).Should().Equal(apiObjects);
    }

    [TestCase("edges")]
    public async Task It_blocks_legacy_collisions_before_drop_with_bounded_ID_only_diagnostics(
        string collision
    )
    {
        _database.DeployThrough(38);
        await using SqlConnection connection = new(_database.ConnectionString);
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
        await using SqlConnection connection = new(_database.ConnectionString);
        long? tenantId = tenant
            ? await connection.ExecuteScalarAsync<long>(
                """INSERT INTO dmscs.Tenant (Name) OUTPUT inserted.Id VALUES (@Name);""",
                new { Name = "one" }
            )
            : null;
        long otherTenant = await connection.ExecuteScalarAsync<long>(
            """INSERT INTO dmscs.Tenant (Name) OUTPUT inserted.Id VALUES (@Name);""",
            new { Name = "two" }
        );
        int first = await InsertAsync(connection, "Store", tenantId);
        int second = await InsertAsync(connection, "other", tenantId);
        await InsertAsync(connection, "store", otherTenant);
        foreach (string duplicate in new[] { "store", "\u3000STORE\u00a0" })
        {
            Func<Task> insert = () => InsertAsync(connection, duplicate, tenantId);
            (await insert.Should().ThrowAsync<SqlException>())
                .Which.Message.Should()
                .Contain("UX_DataStore_TenantId_Name");
            Func<Task> rename = () =>
                connection.ExecuteAsync(
                    """UPDATE dmscs.DataStore SET Name = @Name WHERE Id = @Id;""",
                    new { Id = second, Name = duplicate }
                );
            (await rename.Should().ThrowAsync<SqlException>())
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
        await using SqlConnection connection = new(_database.ConnectionString);
        string script = DataStoreNameComparisonKeyLegacyMigration.Script;
        script = corruption switch
        {
            "without_fold" => script.Replace(
                "N''abcdefghijklmnopqrstuvwxyz''",
                "N''ABCDEFGHIJKLMNOPQRSTUVWXYZ''"
            ),
            "constant" => script.Replace(
                "AS dmscs.DataStoreNameComparisonKey(Name) PERSISTED",
                "AS CONVERT(varbinary(516), 0x1234) PERSISTED"
            ),
            "unprobed_exception" => script.Replace(
                "IF @Name IS NULL RETURN NULL;",
                "IF @Name IS NULL RETURN NULL; IF @Name = N''outside the probe corpus'' RETURN 0x1234;"
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };
        await connection.ExecuteAsync(
            """ALTER TABLE dmscs.DataStore DROP COLUMN NameComparisonKey; DROP FUNCTION dmscs.DataStoreNameComparisonKey;"""
        );
        await connection.ExecuteAsync(script);
        await InsertAsync(connection, "ordinary");
        await InsertAsync(connection, " Display CAFÉ ");
        string definitions = await connection.QuerySingleAsync<string>(
            """SELECT m.definition + c.definition FROM sys.sql_modules m CROSS JOIN sys.computed_columns c WHERE m.object_id = OBJECT_ID(N'dmscs.DataStoreNameComparisonKey') AND c.object_id = OBJECT_ID(N'dmscs.DataStore') AND c.name = 'NameComparisonKey';"""
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
                """SELECT m.definition + c.definition FROM sys.sql_modules m CROSS JOIN sys.computed_columns c WHERE m.object_id = OBJECT_ID(N'dmscs.DataStoreNameComparisonKey') AND c.object_id = OBJECT_ID(N'dmscs.DataStore') AND c.name = 'NameComparisonKey';"""
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
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.ExecuteAsync(
            """ALTER TABLE dmscs.DataStore DROP CONSTRAINT UX_DataStore_TenantId_Name;"""
        );
        if (!missing)
        {
            await connection.ExecuteAsync(
                """ALTER TABLE dmscs.DataStore ADD CONSTRAINT UX_DataStore_TenantId_Name UNIQUE (Name);"""
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
        await using SqlConnection connection = new(_database.ConnectionString);
        await InsertAsync(connection, "original");
        string definition = await connection.QuerySingleAsync<string>(ConstraintDefinition);
        string[] journal = await _database.JournalAsync();
        var rows = (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).ToArray();
        await connection.ExecuteAsync(
            """
            CREATE TRIGGER fail_after_name_constraint_drop ON DATABASE FOR ALTER_TABLE AS
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM sys.key_constraints
                    WHERE parent_object_id = OBJECT_ID(N'dmscs.DataStore') AND name = 'UX_DataStore_TenantId_Name')
                    THROW 50001, 'injected after constraint drop', 1;
            END;
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
        await connection.ExecuteAsync("DROP TRIGGER fail_after_name_constraint_drop ON DATABASE;");
        _database.DeployThrough(40);
        (await connection.QuerySingleAsync<string>(ConstraintDefinition))
            .Should()
            .Be("TenantId,NameComparisonKey");
    }

    private static Task<int> InsertAsync(SqlConnection connection, string name, long? tenantId = null) =>
        connection.ExecuteScalarAsync<int>(
            """INSERT INTO dmscs.DataStore (Name, DataStoreType, TenantId) OUTPUT inserted.Id VALUES (@Name, 'SQL', @TenantId);""",
            new { Name = name, TenantId = tenantId }
        );
}
