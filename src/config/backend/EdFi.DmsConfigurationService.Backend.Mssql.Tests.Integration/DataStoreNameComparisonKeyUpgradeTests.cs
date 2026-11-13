// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Buffers.Binary;
using System.Text;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration.Jobs;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

[TestFixture(false)]
[TestFixture(true)]
[Category("MssqlIntegration")]
[Category("DataStoreNameComparisonKeyUpgrade")]
public class Given_a_DataStore_name_comparison_key_migration(bool upgrade)
{
    private JobUpgradeTestDatabase _database = null!;
    private string[] _before = [];
    private string[] _after = [];
    private int? _existingId;
    private string _displayCollation = "Latin1_General_100_CI_AI";
    private const string OriginalName = "\u3000Existing CAFÉ 😀\u00a0";
    private const string IndexSettings = """
        SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET ARITHABORT ON;
        SET CONCAT_NULL_YIELDS_NULL ON; SET QUOTED_IDENTIFIER ON; SET NUMERIC_ROUNDABORT OFF;
        """;

    [OneTimeSetUp]
    public async Task Setup()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require the ConnectionStrings__MssqlAdmin environment variable."
        );
        _database = new();
        if (upgrade)
        {
            _database.DeployThrough(38);
            _before = await _database.JournalAsync();
            await using SqlConnection connection = new(_database.ConnectionString);
            string databaseCollation = await connection.QuerySingleAsync<string>(
                "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));"
            );
            // Only these fixed accent-insensitive literals can enter the DDL; never metadata text.
            _displayCollation = databaseCollation.Equals(
                "Latin1_General_100_CI_AI",
                StringComparison.OrdinalIgnoreCase
            )
                ? "Latin1_General_CI_AI"
                : "Latin1_General_100_CI_AI";
            await connection.ExecuteAsync(
                $"""
                ALTER TABLE dmscs.DataStore DROP CONSTRAINT UX_DataStore_TenantId_Name;
                ALTER TABLE dmscs.DataStore ALTER COLUMN Name nvarchar(256) COLLATE {_displayCollation} NOT NULL;
                ALTER TABLE dmscs.DataStore ADD CONSTRAINT UX_DataStore_TenantId_Name UNIQUE (TenantId, Name);
                """
            );
            _existingId = await connection.ExecuteScalarAsync<int>(
                "INSERT INTO dmscs.DataStore (Name, DataStoreType) OUTPUT inserted.Id VALUES (@Name, 'SQL');",
                new { Name = OriginalName }
            );
        }
        _database.DeployThrough(39);
        _after = await _database.JournalAsync();
    }

    [OneTimeTearDown]
    public Task Teardown() => _database is null ? Task.CompletedTask : _database.DropAsync();

    private static byte[] ExpectedKey(string value)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value);
        byte[] result = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(result, bytes.Length);
        bytes.CopyTo(result, 4);
        return result;
    }

    [Test]
    public async Task It_journals_0039_once_and_preserves_the_legacy_constraint()
    {
        _after
            .Should()
            .Contain(
                "EdFi.DmsConfigurationService.Backend.Mssql.Deploy.Scripts.0039_Add_DataStore_NameComparisonKey.sql"
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
            "EdFi.DmsConfigurationService.Backend.Mssql.Deploy.Scripts.0039_Add_DataStore_NameComparisonKey.sql";
        await using SqlConnection connection = new(_database.ConnectionString);
        var before = await connection.QuerySingleAsync<(int Id, string Name, byte[] Key)>(
            "INSERT INTO dmscs.DataStore (Name, DataStoreType) OUTPUT inserted.Id, inserted.Name, inserted.NameComparisonKey VALUES (@Name, 'SQL');",
            new { Name = "\u3000Journal recovery CAFÉ 😀\u00a0" }
        );
        string[] objectsBefore = await _database.ConstraintAndIndexNamesAsync("DataStore");
        await connection.ExecuteAsync(
            "DELETE FROM dbo.dmscs_SchemaVersions WHERE ScriptName = @Script;",
            new { Script = script }
        );

        _database.DeployThrough(39);

        (await _database.JournalAsync()).Should().Equal(_after);
        (await _database.JournalAsync()).Count(name => name == script).Should().Be(1);
        var after = await connection.QuerySingleAsync<(int Id, string Name, byte[] Key)>(
            "SELECT Id, Name, NameComparisonKey FROM dmscs.DataStore WHERE Id = @Id;",
            new { before.Id }
        );
        after.Id.Should().Be(before.Id);
        after.Name.Should().Be(before.Name);
        after.Key.Should().Equal(before.Key);
        (await _database.ConstraintAndIndexNamesAsync("DataStore")).Should().Equal(objectsBefore);
    }

    [Test]
    public async Task It_uses_a_persisted_bounded_binary_key_and_preserves_existing_display_names()
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        var column = await connection.QuerySingleAsync<(
            bool Persisted,
            short Length,
            string Type,
            int Deterministic,
            int Precise
        )>(
            """
            SELECT is_persisted, max_length, TYPE_NAME(system_type_id),
                COLUMNPROPERTY(object_id, name, 'IsDeterministic'), COLUMNPROPERTY(object_id, name, 'IsPrecise')
            FROM sys.computed_columns WHERE object_id = OBJECT_ID(N'dmscs.DataStore') AND name = 'NameComparisonKey';
            """
        );
        column.Should().Be((true, (short)516, "varbinary", 1, 1));
        if (_existingId is not null)
        {
            var row = await connection.QuerySingleAsync<(string Name, byte[] Key)>(
                "SELECT Name, NameComparisonKey FROM dmscs.DataStore WHERE Id = @Id;",
                new { Id = _existingId }
            );
            row.Name.Should().Be(OriginalName);
            row.Key.Should().Equal(ExpectedKey(CatalogName.Canonicalize(OriginalName)));
            string displayCollation = await connection.QuerySingleAsync<string>(
                "SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID(N'dmscs.DataStore') AND name = 'Name';"
            );
            displayCollation.Should().Be(_displayCollation);
            displayCollation
                .Should()
                .NotBe(
                    await connection.ExecuteScalarAsync<string>(
                        "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));"
                    )
                );
        }
    }

    [TestCaseSource(typeof(CatalogNameComparisonCases), nameof(CatalogNameComparisonCases.Vectors))]
    public async Task It_computes_keys_from_direct_inserts_and_renames(string input, string expected)
    {
        CatalogName.Canonicalize(input).Should().Be(expected);
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var row = await connection.QuerySingleAsync<(int Id, string Name, byte[] Key)>(
            "INSERT INTO dmscs.DataStore (Name, DataStoreType) OUTPUT inserted.Id, inserted.Name, inserted.NameComparisonKey VALUES (@Name, 'SQL');",
            new { Name = input },
            transaction
        );
        row.Name.Should().Be(input);
        row.Key.Should().Equal(ExpectedKey(expected));
        byte[] temporaryKey = await connection.QuerySingleAsync<byte[]>(
            "UPDATE dmscs.DataStore SET Name = 'temporary rename' OUTPUT inserted.NameComparisonKey WHERE Id = @Id;",
            new { row.Id },
            transaction
        );
        temporaryKey.Should().Equal(ExpectedKey("temporary rename"));
        var renamed = await connection.QuerySingleAsync<(string Name, byte[] Key)>(
            "UPDATE dmscs.DataStore SET Name = @Name OUTPUT inserted.Name, inserted.NameComparisonKey WHERE Id = @Id;",
            new { Name = input, row.Id },
            transaction
        );
        renamed.Name.Should().Be(input);
        renamed.Key.Should().Equal(ExpectedKey(expected));
    }

    [TestCaseSource(typeof(CatalogNameComparisonCases), nameof(CatalogNameComparisonCases.Pairs))]
    public async Task It_compares_generated_keys_with_the_shared_equality_policy(
        string left,
        string right,
        bool equal
    )
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        byte[] leftKey = await connection.QuerySingleAsync<byte[]>(
            "INSERT INTO dmscs.DataStore (Name, DataStoreType) OUTPUT inserted.NameComparisonKey VALUES (@Name, 'SQL');",
            new { Name = left },
            transaction
        );
        await connection.ExecuteAsync(
            "DELETE FROM dmscs.DataStore WHERE Name = @Name;",
            new { Name = left },
            transaction
        );
        bool actual = await connection.ExecuteScalarAsync<bool>(
            "INSERT INTO dmscs.DataStore (Name, DataStoreType) OUTPUT CAST(CASE WHEN inserted.NameComparisonKey = @Key THEN 1 ELSE 0 END AS bit) VALUES (@Name, 'SQL');",
            new { Name = right, Key = leftKey },
            transaction
        );
        actual.Should().Be(equal);
    }

    [Test]
    public async Task It_keeps_null_rejection_and_legacy_name_uniqueness()
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        (await connection.ExecuteScalarAsync<byte[]?>("SELECT dmscs.DataStoreNameComparisonKey(NULL);"))
            .Should()
            .BeNull();
        Func<Task> insertNull = () =>
            connection.ExecuteAsync(
                "INSERT INTO dmscs.DataStore (Name, DataStoreType) VALUES (NULL, 'SQL');"
            );
        (await insertNull.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(515);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        Func<Task> insertDuplicate = () =>
            connection.ExecuteAsync(
                "INSERT INTO dmscs.DataStore (Name, DataStoreType) VALUES ('duplicate', 'SQL'), ('duplicate', 'SQL');",
                transaction: transaction
            );
        (await insertDuplicate.Should().ThrowAsync<SqlException>())
            .Which.Message.Should()
            .Contain("UX_DataStore_TenantId_Name");
        if (upgrade)
        {
            Func<Task> insertAccentPair = () =>
                connection.ExecuteAsync(
                    "INSERT INTO dmscs.DataStore (Name, DataStoreType) VALUES (N'café', 'SQL'), (N'cafe', 'SQL');",
                    transaction: transaction
                );
            (await insertAccentPair.Should().ThrowAsync<SqlException>())
                .Which.Message.Should()
                .Contain("UX_DataStore_TenantId_Name");
        }
    }

    [Test]
    public async Task It_supports_a_future_computed_key_index_and_requires_the_index_SET_options()
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            IndexSettings + "CREATE INDEX IX_Test_NameComparisonKey ON dmscs.DataStore (NameComparisonKey);",
            transaction: transaction
        );
        await connection.ExecuteAsync(
            "INSERT INTO dmscs.DataStore (Name, DataStoreType) VALUES ('indexed write', 'SQL'); UPDATE dmscs.DataStore SET Name = 'indexed rename' WHERE Name = 'indexed write';",
            transaction: transaction
        );
        (
            await connection.ExecuteScalarAsync<byte[]>(
                "SELECT NameComparisonKey FROM dmscs.DataStore WHERE Name = 'indexed rename';",
                transaction: transaction
            )
        )
            .Should()
            .Equal(ExpectedKey("indexed rename"));
        await connection.ExecuteAsync("SET NUMERIC_ROUNDABORT ON;", transaction: transaction);
        Func<Task> badSettings = () =>
            connection.ExecuteAsync(
                "INSERT INTO dmscs.DataStore (Name, DataStoreType) VALUES ('invalid settings', 'SQL');",
                transaction: transaction
            );
        (await badSettings.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(1934);
    }

    [Test]
    public async Task It_has_lossless_UTF8_conversion_for_future_query_ordering()
    {
        await using SqlConnection connection = new(_database.ConnectionString);
        int majorVersion = await connection.ExecuteScalarAsync<int>(
            "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));"
        );
        majorVersion.Should().BeGreaterThanOrEqualTo(15);
        foreach (
            string value in new[]
            {
                "café",
                "\ue000",
                "\U00010000",
                new string('界', 256),
                new string('A', 254) + "😀",
            }
        )
        {
            byte[] bytes = await connection.QuerySingleAsync<byte[]>(
                "SELECT CONVERT(varbinary(1024), CONVERT(varchar(1024), @Value COLLATE Latin1_General_100_BIN2_UTF8));",
                new { Value = value }
            );
            bytes.Should().Equal(Encoding.UTF8.GetBytes(value));
        }
    }
}

[TestFixture]
[Category("MssqlIntegration")]
[Category("DataStoreNameComparisonKeyUpgrade")]
public class Given_a_column_addition_failure_during_the_name_key_migration
{
    private JobUpgradeTestDatabase _database = null!;

    [OneTimeTearDown]
    public Task Teardown() => _database is null ? Task.CompletedTask : _database.DropAsync();

    [Test]
    public async Task It_rolls_back_the_helper_and_leaves_the_journal_and_legacy_schema_unchanged()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require the ConnectionStrings__MssqlAdmin environment variable."
        );
        _database = new();
        _database.DeployThrough(38);
        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.ExecuteAsync("ALTER TABLE dmscs.DataStore ADD NameComparisonKey int NULL;");
        string[] journalBefore = await _database.JournalAsync();
        string[] objectsBefore = await _database.ConstraintAndIndexNamesAsync("DataStore");

        // The migration creates its helper before detecting this deliberately conflicting column shape.
        var failure = _database
            .Deploy(39)
            .Should()
            .BeOfType<DatabaseDeployResult.DatabaseDeployFailure>()
            .Subject;
        failure.Error.Message.Should().Contain("NameComparisonKey");
        (await _database.JournalAsync()).Should().Equal(journalBefore);
        (await _database.ConstraintAndIndexNamesAsync("DataStore")).Should().Equal(objectsBefore);
        (
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM sys.objects WHERE object_id = OBJECT_ID(N'dmscs.DataStoreNameComparisonKey');"
            )
        )
            .Should()
            .Be(0);
        var column = await connection.QuerySingleAsync<(string Type, bool Computed)>(
            "SELECT TYPE_NAME(system_type_id), is_computed FROM sys.columns WHERE object_id = OBJECT_ID(N'dmscs.DataStore') AND name = 'NameComparisonKey';"
        );
        column.Should().Be(("int", false));
    }
}
