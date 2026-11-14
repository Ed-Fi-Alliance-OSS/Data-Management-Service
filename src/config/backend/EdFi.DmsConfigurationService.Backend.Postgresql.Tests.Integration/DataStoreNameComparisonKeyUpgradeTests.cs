// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration.Jobs;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

[TestFixture(false)]
[TestFixture(true)]
[Category("DataStoreNameComparisonKeyUpgrade")]
public class Given_a_DataStore_name_comparison_key_migration(bool upgrade)
{
    private JobUpgradeTestDatabase _database = null!;
    private string[] _before = [];
    private string[] _after = [];
    private int? _existingId;
    private const string OriginalName = "\u3000Existing CAFÉ 😀\u00a0";

    [OneTimeSetUp]
    public async Task Setup()
    {
        _database = new();
        if (upgrade)
        {
            _database.DeployThrough(38);
            _before = await _database.JournalAsync();
            await using NpgsqlConnection connection = new(_database.ConnectionString);
            _existingId = await connection.ExecuteScalarAsync<int>(
                """INSERT INTO dmscs."DataStore" ("Name", "DataStoreType") VALUES (@Name, 'SQL') RETURNING "Id";""",
                new { Name = OriginalName }
            );
        }
        _database.DeployThrough(39);
        _after = await _database.JournalAsync();
    }

    [OneTimeTearDown]
    public Task Teardown() => _database is null ? Task.CompletedTask : _database.DropAsync();

    [Test]
    public async Task It_journals_0039_once_and_preserves_the_legacy_constraint()
    {
        _after
            .Should()
            .Contain(
                "EdFi.DmsConfigurationService.Backend.Postgresql.Deploy.Scripts.0039_Add_DataStore_NameComparisonKey.sql"
            );
        if (upgrade)
        {
            _after.Except(_before).Should().ContainSingle();
        }
        _database.DeployThrough(39);
        (await _database.JournalAsync()).Should().Equal(_after);
        (await _database.ConstraintAndIndexNamesAsync("DataStore"))
            .Should()
            .Contain("UX_DataStore_TenantId_Name");
    }

    [Test]
    public async Task It_recovers_when_the_committed_migration_is_missing_its_journal_entry()
    {
        const string script =
            "EdFi.DmsConfigurationService.Backend.Postgresql.Deploy.Scripts.0039_Add_DataStore_NameComparisonKey.sql";
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        var before = await connection.QuerySingleAsync<(int Id, string Name, string Key)>(
            """INSERT INTO dmscs."DataStore" ("Name", "DataStoreType") VALUES (@Name, 'SQL') RETURNING "Id", "Name", "NameComparisonKey";""",
            new { Name = "\u3000Journal recovery CAFÉ 😀\u00a0" }
        );
        string[] objectsBefore = await _database.ConstraintAndIndexNamesAsync("DataStore");
        await connection.ExecuteAsync(
            """DELETE FROM public."dmscs_SchemaVersions" WHERE scriptname = @Script;""",
            new { Script = script }
        );

        _database.DeployThrough(39);

        (await _database.JournalAsync()).Should().Equal(_after);
        (await _database.JournalAsync()).Count(name => name == script).Should().Be(1);
        var after = await connection.QuerySingleAsync<(int Id, string Name, string Key)>(
            """SELECT "Id", "Name", "NameComparisonKey" FROM dmscs."DataStore" WHERE "Id" = @Id;""",
            new { before.Id }
        );
        after.Should().Be(before);
        (await _database.ConstraintAndIndexNamesAsync("DataStore")).Should().Equal(objectsBefore);
    }

    [Test]
    public async Task It_uses_a_stored_generated_text_key_with_deterministic_C_collation()
    {
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        var column = await connection.QuerySingleAsync<(
            string Generated,
            string Type,
            string Collation,
            bool Deterministic
        )>(
            """
            SELECT a.attgenerated::text, format_type(a.atttypid, a.atttypmod), c.collname, c.collisdeterministic
            FROM pg_attribute a JOIN pg_collation c ON c.oid = a.attcollation
            WHERE a.attrelid = 'dmscs."DataStore"'::regclass AND a.attname = 'NameComparisonKey';
            """
        );
        column.Should().Be(("s", "text", "C", true));
        if (_existingId is not null)
        {
            var row = await connection.QuerySingleAsync<(string Name, string Key)>(
                """SELECT "Name", "NameComparisonKey" FROM dmscs."DataStore" WHERE "Id" = @Id;""",
                new { Id = _existingId }
            );
            row.Should().Be((OriginalName, CatalogName.Canonicalize(OriginalName)));
        }
    }

    [TestCaseSource(typeof(CatalogNameComparisonCases), nameof(CatalogNameComparisonCases.Vectors))]
    public async Task It_computes_keys_from_direct_inserts_and_renames(string input, string expected)
    {
        CatalogName.Canonicalize(input).Should().Be(expected);
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var row = await connection.QuerySingleAsync<(int Id, string Name, string Key)>(
            """INSERT INTO dmscs."DataStore" ("Name", "DataStoreType") VALUES (@Name, 'SQL') RETURNING "Id", "Name", "NameComparisonKey";""",
            new { Name = input },
            transaction
        );
        row.Name.Should().Be(input);
        row.Key.Should().Be(expected);
        string temporaryKey = await connection.QuerySingleAsync<string>(
            """UPDATE dmscs."DataStore" SET "Name" = 'temporary rename' WHERE "Id" = @Id RETURNING "NameComparisonKey";""",
            new { row.Id },
            transaction
        );
        temporaryKey.Should().Be("temporary rename");
        var renamed = await connection.QuerySingleAsync<(string Name, string Key)>(
            """UPDATE dmscs."DataStore" SET "Name" = @Name WHERE "Id" = @Id RETURNING "Name", "NameComparisonKey";""",
            new { Name = input, row.Id },
            transaction
        );
        renamed.Should().Be((input, expected));
    }

    [TestCaseSource(typeof(CatalogNameComparisonCases), nameof(CatalogNameComparisonCases.Pairs))]
    public async Task It_compares_generated_keys_with_the_shared_equality_policy(
        string left,
        string right,
        bool equal
    )
    {
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        string leftKey = await connection.QuerySingleAsync<string>(
            """INSERT INTO dmscs."DataStore" ("Name", "DataStoreType") VALUES (@Name, 'SQL') RETURNING "NameComparisonKey";""",
            new { Name = left },
            transaction
        );
        await connection.ExecuteAsync(
            """DELETE FROM dmscs."DataStore" WHERE "Name" = @Name;""",
            new { Name = left },
            transaction
        );
        bool actual = await connection.ExecuteScalarAsync<bool>(
            """INSERT INTO dmscs."DataStore" ("Name", "DataStoreType") VALUES (@Name, 'SQL') RETURNING "NameComparisonKey" = @Key COLLATE "C";""",
            new { Name = right, Key = leftKey },
            transaction
        );
        actual.Should().Be(equal);
    }

    [Test]
    public async Task It_preserves_PostgreSQL_character_capacity()
    {
        string name = string.Concat(Enumerable.Repeat("😀", 256));
        await It_computes_keys_from_direct_inserts_and_renames(name, name);
    }

    [Test]
    public async Task It_keeps_null_rejection_and_legacy_name_uniqueness()
    {
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            """INSERT INTO dmscs."DataStore" ("Name", "DataStoreType") VALUES ('Store', 'SQL'), ('store', 'SQL');""",
            transaction: transaction
        );
        (
            await connection.ExecuteScalarAsync<int>(
                """SELECT count(DISTINCT "NameComparisonKey") FROM dmscs."DataStore" WHERE "Name" IN ('Store', 'store');""",
                transaction: transaction
            )
        )
            .Should()
            .Be(1);
        await transaction.RollbackAsync();
        Func<Task> insertNull = () =>
            connection.ExecuteAsync(
                """INSERT INTO dmscs."DataStore" ("Name", "DataStoreType") VALUES (NULL, 'SQL');"""
            );
        (await insertNull.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23502");
        await using var duplicateTransaction = await connection.BeginTransactionAsync();
        Func<Task> insertDuplicate = () =>
            connection.ExecuteAsync(
                """INSERT INTO dmscs."DataStore" ("Name", "DataStoreType") VALUES ('duplicate', 'SQL'), ('duplicate', 'SQL');""",
                transaction: duplicateTransaction
            );
        (await insertDuplicate.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should()
            .Be("UX_DataStore_TenantId_Name");
    }
}

[TestFixture]
[Category("DataStoreNameComparisonKeyUpgrade")]
public class Given_a_non_UTF8_database_before_the_name_key_migration
{
    private JobUpgradeTestDatabase _database = null!;

    [OneTimeTearDown]
    public Task Teardown() => _database is null ? Task.CompletedTask : _database.DropAsync();

    [Test]
    public async Task It_refuses_the_upgrade_before_mutation_or_journaling()
    {
        _database = new();
        NpgsqlConnectionStringBuilder target = new(_database.ConnectionString);
        string databaseName = target.Database!;
        target.Database = "postgres";
        await using (NpgsqlConnection maintenance = new(target.ConnectionString))
        {
            await maintenance.ExecuteAsync(
                $"""CREATE DATABASE "{databaseName}" WITH TEMPLATE template0 ENCODING 'SQL_ASCII' LC_COLLATE 'C' LC_CTYPE 'C';"""
            );
        }
        _database.DeployThrough(38);
        string[] before = await _database.JournalAsync();
        var failure = _database
            .Deploy(39)
            .Should()
            .BeOfType<DatabaseDeployResult.DatabaseDeployFailure>()
            .Subject;
        failure.Error.Message.Should().Contain("UTF8");
        (await _database.JournalAsync()).Should().Equal(before);
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        (
            await connection.ExecuteScalarAsync<int>(
                """
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema = 'dmscs' AND table_name = 'DataStore' AND column_name = 'NameComparisonKey';
                """
            )
        ).Should().Be(0);
        (await _database.ConstraintAndIndexNamesAsync("DataStore"))
            .Should()
            .Contain("UX_DataStore_TenantId_Name");
    }
}

[TestFixture]
[Category("DataStoreNameComparisonKeyUpgrade")]
[Category("DataStoreNameComparisonKeyDefinitionGuard")]
public class Given_existing_name_key_definitions_before_migration_replay
{
    private JobUpgradeTestDatabase _database = null!;
    private const string ScriptName =
        "EdFi.DmsConfigurationService.Backend.Postgresql.Deploy.Scripts.0039_Add_DataStore_NameComparisonKey.sql";
    private const string ColumnDefinition = """
        SELECT pg_get_expr(d.adbin, d.adrelid)
        FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
        WHERE a.attrelid = 'dmscs."DataStore"'::regclass AND a.attname = 'NameComparisonKey';
        """;
    private const string Rows = """
        SELECT "Id", "Name", "NameComparisonKey" FROM dmscs."DataStore" ORDER BY "Id";
        """;

    [SetUp]
    public void Setup()
    {
        _database = new();
        _database.DeployThrough(38);
    }

    [TearDown]
    public Task Teardown() => _database is null ? Task.CompletedTask : _database.DropAsync();

    [TestCase(false)]
    [TestCase(true)]
    public async Task It_replays_the_unmodified_old_0039_definitions_with_either_line_ending(bool crlf)
    {
        await using NpgsqlConnection connection = new(_database.ConnectionString);
        await connection.ExecuteAsync(
            DataStoreNameComparisonKeyLegacyMigration.Script.ReplaceLineEndings(crlf ? "\r\n" : "\n")
        );
        await SeedAsync(connection);
        string columnBefore = await connection.QuerySingleAsync<string>(ColumnDefinition);
        var rowsBefore = (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).ToArray();
        string[] journalBefore = await _database.JournalAsync();
        string[] objectsBefore = await _database.ConstraintAndIndexNamesAsync("DataStore");

        _database.DeployThrough(39);
        _database.DeployThrough(39);

        (await connection.QuerySingleAsync<string>(ColumnDefinition)).Should().Be(columnBefore);
        (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).Should().Equal(rowsBefore);
        (await _database.JournalAsync()).Should().Equal(journalBefore.Append(ScriptName));
        (await _database.ConstraintAndIndexNamesAsync("DataStore")).Should().Equal(objectsBefore);
    }

    [TestCase("without_fold")]
    [TestCase("constant")]
    [TestCase("unprobed_exception")]
    public async Task It_refuses_metadata_valid_incompatible_definitions_without_mutation(string corruption)
    {
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
        await connection.ExecuteAsync(script);
        await SeedAsync(connection);
        // These are stored text columns with C collation: the original gate accepts them.
        var metadata = await connection.QuerySingleAsync<(
            string Generated,
            string Type,
            string Collation,
            bool Deterministic
        )>(
            """
            SELECT a.attgenerated::text, format_type(a.atttypid, a.atttypmod), c.collname, c.collisdeterministic
            FROM pg_attribute a JOIN pg_collation c ON c.oid = a.attcollation
            WHERE a.attrelid = 'dmscs."DataStore"'::regclass AND a.attname = 'NameComparisonKey';
            """
        );
        metadata.Should().Be(("s", "text", "C", true));
        string columnBefore = await connection.QuerySingleAsync<string>(ColumnDefinition);
        var rowsBefore = (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).ToArray();
        string[] journalBefore = await _database.JournalAsync();
        string[] objectsBefore = await _database.ConstraintAndIndexNamesAsync("DataStore");

        var failure = _database
            .Deploy(39)
            .Should()
            .BeOfType<DatabaseDeployResult.DatabaseDeployFailure>()
            .Subject;

        failure.Error.Message.Should().Contain("NameComparisonKey").And.Contain("definition");
        (await connection.QuerySingleAsync<string>(ColumnDefinition)).Should().Be(columnBefore);
        (await connection.QueryAsync<(int Id, string Name, string Key)>(Rows)).Should().Equal(rowsBefore);
        (await _database.JournalAsync()).Should().Equal(journalBefore);
        objectsBefore.Should().Contain("UX_DataStore_TenantId_Name");
        (await _database.ConstraintAndIndexNamesAsync("DataStore")).Should().Equal(objectsBefore);
    }

    private static Task SeedAsync(NpgsqlConnection connection) =>
        connection.ExecuteAsync(
            """INSERT INTO dmscs."DataStore" ("Name", "DataStoreType") VALUES (@Name, 'SQL'), ('ordinary', 'SQL');""",
            new { Name = "\u3000Guard CAFÉ 😀\u00a0" }
        );
}
