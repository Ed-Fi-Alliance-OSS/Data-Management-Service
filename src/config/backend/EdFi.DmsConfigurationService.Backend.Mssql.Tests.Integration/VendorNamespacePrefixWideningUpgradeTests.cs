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

/// <summary>
/// Upgrades an isolated database deployed and journaled through script 0036 to script 0037, which
/// widens the VendorNamespacePrefix NamespacePrefix column from 128 to 255 characters. Each fixture
/// shapes the column directly with SQL before running the upgrade. Every concrete fixture carries
/// <see cref="Category"/> and the MssqlIntegration category so a step-level filter selects exactly
/// this group.
/// </summary>
public abstract class VendorNamespacePrefixWideningUpgradeFixture
{
    public const string Category = "VendorNamespacePrefixWideningUpgrade";

    /// <summary>SQL Server error for a string or binary value that would be truncated.</summary>
    protected const int StringTruncation = 2628;

    protected sealed record ColumnShape(int MaxLengthBytes, bool IsNullable, string? Collation);

    private JobUpgradeTestDatabase _database = null!;

    protected DatabaseDeployResult UpgradeResult { get; private set; } = null!;
    protected string[] JournalBeforeUpgrade { get; private set; } = [];
    protected string[] JournalAfterUpgrade { get; private set; } = [];
    protected ColumnShape ColumnBeforeUpgrade { get; private set; } = null!;
    protected ColumnShape ColumnAfterUpgrade { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require the ConnectionStrings__MssqlAdmin environment variable."
        );
        _database = new JobUpgradeTestDatabase();

        _database.DeployThrough(JobUpgradeTestDatabase.IdentifierUniquenessScript);
        JournalBeforeUpgrade = await _database.JournalAsync();

        await using SqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();

        await SeedAsync(connection);
        ColumnBeforeUpgrade = await ColumnShapeAsync(connection);

        UpgradeResult = _database.Deploy(JobUpgradeTestDatabase.NamespacePrefixWideningScript);

        JournalAfterUpgrade = await _database.JournalAsync();
        ColumnAfterUpgrade = await ColumnShapeAsync(connection);

        await AfterUpgradeAsync(connection);
    }

    [OneTimeTearDown]
    public Task OneTimeTeardown() => _database is null ? Task.CompletedTask : _database.DropAsync();

    protected virtual Task SeedAsync(SqlConnection connection) => Task.CompletedTask;

    protected virtual Task AfterUpgradeAsync(SqlConnection connection) => Task.CompletedTask;

    protected DatabaseDeployResult Redeploy() =>
        _database.Deploy(JobUpgradeTestDatabase.NamespacePrefixWideningScript);

    protected Task<string[]> JournalAsync() => _database.JournalAsync();

    protected static async Task<ColumnShape> ColumnShapeAsync(SqlConnection connection) =>
        await connection.QuerySingleAsync<ColumnShape>(
            """
            SELECT CAST(max_length AS int) AS MaxLengthBytes, is_nullable AS IsNullable, collation_name AS Collation
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dmscs.VendorNamespacePrefix') AND name = N'NamespacePrefix';
            """
        );

    /// <summary>Replaces the column definition, dropping and re-adding the primary key around it.</summary>
    protected static Task ReshapeColumnAsync(SqlConnection connection, string columnDefinition) =>
        connection.ExecuteAsync(
            $"""
            ALTER TABLE dmscs.VendorNamespacePrefix DROP CONSTRAINT PK_VendorNamespacePrefix;
            ALTER TABLE dmscs.VendorNamespacePrefix ALTER COLUMN NamespacePrefix {columnDefinition};
            ALTER TABLE dmscs.VendorNamespacePrefix ADD CONSTRAINT PK_VendorNamespacePrefix PRIMARY KEY (VendorId, NamespacePrefix);
            """
        );

    protected static async Task<int> InsertVendorAsync(SqlConnection connection) =>
        await connection.ExecuteScalarAsync<int>(
            "INSERT INTO dmscs.Vendor (Company) OUTPUT INSERTED.Id VALUES (@Company);",
            new { Company = $"Vendor {Guid.NewGuid():N}" }
        );

    protected static Task InsertPrefixAsync(SqlConnection connection, int vendorId, string prefix) =>
        connection.ExecuteAsync(
            "INSERT INTO dmscs.VendorNamespacePrefix (VendorId, NamespacePrefix) VALUES (@VendorId, @Prefix);",
            new { VendorId = vendorId, Prefix = prefix }
        );

    /// <summary>A namespace-shaped prefix of exactly <paramref name="length"/> characters.</summary>
    protected static string PrefixOfLength(int length) => "uri://vendor.example.org/".PadRight(length, 'x');
}

[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_a_database_deployed_through_0036_with_a_128_character_prefix
    : VendorNamespacePrefixWideningUpgradeFixture
{
    private readonly string _seededPrefix = PrefixOfLength(128);
    private int _vendorId;
    private string[] _primaryKeyColumns = [];
    private bool _constraintIsPrimaryKey;
    private string[] _prefixesAfterUpgrade = [];
    private string[] _journalAfterRepeatDeploy = [];
    private ColumnShape _columnAfterScriptRerun = null!;
    private Exception? _insert255Failure;
    private SqlException? _insert256Failure;

    protected override async Task SeedAsync(SqlConnection connection)
    {
        _seededPrefix.Length.Should().Be(128);

        _vendorId = await InsertVendorAsync(connection);
        await InsertPrefixAsync(connection, _vendorId, _seededPrefix);
    }

    protected override async Task AfterUpgradeAsync(SqlConnection connection)
    {
        _constraintIsPrimaryKey = await connection.ExecuteScalarAsync<bool>(
            """
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dmscs.VendorNamespacePrefix')
                  AND name = N'PK_VendorNamespacePrefix'
                  AND is_primary_key = 1
            ) THEN 1 ELSE 0 END AS bit);
            """
        );

        _primaryKeyColumns = (
            await connection.QueryAsync<string>(
                """
                SELECT column_row.name
                FROM sys.indexes index_row
                JOIN sys.index_columns index_column
                    ON index_column.object_id = index_row.object_id AND index_column.index_id = index_row.index_id
                JOIN sys.columns column_row
                    ON column_row.object_id = index_column.object_id AND column_row.column_id = index_column.column_id
                WHERE index_row.object_id = OBJECT_ID(N'dmscs.VendorNamespacePrefix')
                  AND index_row.name = N'PK_VendorNamespacePrefix'
                  AND index_column.is_included_column = 0
                ORDER BY index_column.key_ordinal;
                """
            )
        ).ToArray();

        _prefixesAfterUpgrade = (
            await connection.QueryAsync<string>(
                "SELECT NamespacePrefix FROM dmscs.VendorNamespacePrefix ORDER BY NamespacePrefix;"
            )
        ).ToArray();

        _insert255Failure = await TryInsertAsync(connection, PrefixOfLength(255));
        _insert256Failure = await TryInsertAsync(connection, PrefixOfLength(256)) as SqlException;

        Redeploy().Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();
        _journalAfterRepeatDeploy = await JournalAsync();

        await connection.ExecuteAsync(EmbeddedWideningScript());
        _columnAfterScriptRerun = await ColumnShapeAsync(connection);
    }

    private static string EmbeddedWideningScript()
    {
        using Stream stream =
            typeof(Deploy.DatabaseDeploy).Assembly.GetManifestResourceStream(
                JobUpgradeTestDatabase.NamespacePrefixWideningScriptName
            ) ?? throw new InvalidOperationException("The widening script is not embedded.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private async Task<Exception?> TryInsertAsync(SqlConnection connection, string prefix)
    {
        try
        {
            await InsertPrefixAsync(connection, _vendorId, prefix);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    [Test]
    public void It_upgrades_successfully() =>
        UpgradeResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

    [Test]
    public void It_journals_exactly_the_widening_script() =>
        JournalAfterUpgrade
            .Except(JournalBeforeUpgrade)
            .Should()
            .Equal(JobUpgradeTestDatabase.NamespacePrefixWideningScriptName);

    [Test]
    public void It_starts_with_a_128_character_column() =>
        ColumnBeforeUpgrade.MaxLengthBytes.Should().Be(256);

    [Test]
    public void It_widens_the_column_to_255() => ColumnAfterUpgrade.MaxLengthBytes.Should().Be(510);

    [Test]
    public void It_keeps_the_column_not_null() => ColumnAfterUpgrade.IsNullable.Should().BeFalse();

    [Test]
    public void It_keeps_the_column_collation() =>
        ColumnAfterUpgrade.Collation.Should().Be(ColumnBeforeUpgrade.Collation);

    [Test]
    public void It_keeps_the_primary_key_constraint() => _constraintIsPrimaryKey.Should().BeTrue();

    [Test]
    public void It_keeps_the_composite_primary_key_in_order() =>
        _primaryKeyColumns.Should().Equal("VendorId", "NamespacePrefix");

    [Test]
    public void It_leaves_the_seeded_row_unchanged() => _prefixesAfterUpgrade.Should().Equal(_seededPrefix);

    [Test]
    public void It_accepts_a_255_character_prefix() => _insert255Failure.Should().BeNull();

    [Test]
    public void It_rejects_a_256_character_prefix_as_too_long()
    {
        _insert256Failure.Should().NotBeNull();
        _insert256Failure!.Number.Should().Be(StringTruncation);
    }

    [Test]
    public void It_adds_no_journal_rows_on_a_repeat_deploy() =>
        _journalAfterRepeatDeploy.Should().Equal(JournalAfterUpgrade);

    [Test]
    public void It_leaves_the_column_unchanged_when_the_script_runs_again() =>
        _columnAfterScriptRerun.Should().Be(ColumnAfterUpgrade);
}

[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_a_column_already_wider_than_255 : VendorNamespacePrefixWideningUpgradeFixture
{
    protected override async Task SeedAsync(SqlConnection connection) =>
        await connection.ExecuteAsync(
            "ALTER TABLE dmscs.VendorNamespacePrefix ALTER COLUMN NamespacePrefix NVARCHAR(300) NOT NULL;"
        );

    [Test]
    public void It_upgrades_successfully() =>
        UpgradeResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

    [Test]
    public void It_does_not_shrink_the_column() => ColumnAfterUpgrade.MaxLengthBytes.Should().Be(600);
}

[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_a_column_with_a_non_default_collation : VendorNamespacePrefixWideningUpgradeFixture
{
    private string[] _primaryKeyColumns = [];

    protected override Task SeedAsync(SqlConnection connection) =>
        ReshapeColumnAsync(connection, "NVARCHAR(128) COLLATE Latin1_General_CS_AS NOT NULL");

    protected override async Task AfterUpgradeAsync(SqlConnection connection) =>
        _primaryKeyColumns = (
            await connection.QueryAsync<string>(
                """
                SELECT column_row.name
                FROM sys.indexes index_row
                JOIN sys.index_columns index_column
                    ON index_column.object_id = index_row.object_id AND index_column.index_id = index_row.index_id
                JOIN sys.columns column_row
                    ON column_row.object_id = index_column.object_id AND column_row.column_id = index_column.column_id
                WHERE index_row.object_id = OBJECT_ID(N'dmscs.VendorNamespacePrefix')
                  AND index_row.name = N'PK_VendorNamespacePrefix'
                  AND index_column.is_included_column = 0
                ORDER BY index_column.key_ordinal;
                """
            )
        ).ToArray();

    [Test]
    public void It_upgrades_successfully() =>
        UpgradeResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

    [Test]
    public void It_widens_the_column_to_255() => ColumnAfterUpgrade.MaxLengthBytes.Should().Be(510);

    [Test]
    public void It_keeps_the_Latin1_General_CS_AS_collation() =>
        ColumnAfterUpgrade.Collation.Should().Be("Latin1_General_CS_AS");

    [Test]
    public void It_keeps_the_composite_primary_key_in_order() =>
        _primaryKeyColumns.Should().Equal("VendorId", "NamespacePrefix");
}

[TestFixture]
[Category(Category)]
[Category("MssqlIntegration")]
public class Given_a_column_an_operator_changed_to_varchar : VendorNamespacePrefixWideningUpgradeFixture
{
    private string? _typeName;

    protected override Task SeedAsync(SqlConnection connection) =>
        ReshapeColumnAsync(connection, "VARCHAR(300) NOT NULL");

    protected override async Task AfterUpgradeAsync(SqlConnection connection) =>
        _typeName = await connection.ExecuteScalarAsync<string>(
            """
            SELECT TYPE_NAME(user_type_id)
            FROM sys.columns
            WHERE object_id = OBJECT_ID(N'dmscs.VendorNamespacePrefix') AND name = N'NamespacePrefix';
            """
        );

    [Test]
    public void It_upgrades_successfully() =>
        UpgradeResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

    [Test]
    public void It_leaves_the_column_as_varchar_300()
    {
        _typeName.Should().Be("varchar");
        ColumnAfterUpgrade.MaxLengthBytes.Should().Be(300);
    }
}
