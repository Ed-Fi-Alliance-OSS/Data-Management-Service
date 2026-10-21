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

/// <summary>
/// Upgrades an isolated database deployed and journaled through script 0036 to script 0037, which
/// widens the VendorNamespacePrefix NamespacePrefix column from 128 to 255 characters. Each fixture
/// shapes the column directly with SQL before running the upgrade. Every concrete fixture carries
/// <see cref="Category"/> so a step-level filter selects exactly this group.
/// </summary>
public abstract class VendorNamespacePrefixWideningUpgradeFixture
{
    public const string Category = "VendorNamespacePrefixWideningUpgrade";

    private readonly JobUpgradeTestDatabase _database = new();

    protected DatabaseDeployResult UpgradeResult { get; private set; } = null!;
    protected string[] JournalBeforeUpgrade { get; private set; } = [];
    protected string[] JournalAfterUpgrade { get; private set; } = [];
    protected string ColumnTypeAfterUpgrade { get; private set; } = "";

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        _database.DeployThrough(JobUpgradeTestDatabase.IdentifierUniquenessScript);
        JournalBeforeUpgrade = await _database.JournalAsync();

        await using NpgsqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();

        await SeedAsync(connection);

        UpgradeResult = _database.Deploy(JobUpgradeTestDatabase.NamespacePrefixWideningScript);

        JournalAfterUpgrade = await _database.JournalAsync();
        ColumnTypeAfterUpgrade = await ColumnTypeAsync(connection);

        await AfterUpgradeAsync(connection);
    }

    [OneTimeTearDown]
    public Task OneTimeTeardown() => _database.DropAsync();

    protected virtual Task SeedAsync(NpgsqlConnection connection) => Task.CompletedTask;

    protected virtual Task AfterUpgradeAsync(NpgsqlConnection connection) => Task.CompletedTask;

    protected DatabaseDeployResult Redeploy() =>
        _database.Deploy(JobUpgradeTestDatabase.NamespacePrefixWideningScript);

    protected Task<string[]> JournalAsync() => _database.JournalAsync();

    protected static async Task<string> ColumnTypeAsync(NpgsqlConnection connection) =>
        await connection.ExecuteScalarAsync<string>(
            """
            SELECT format_type(attribute.atttypid, attribute.atttypmod)
            FROM pg_attribute attribute
            WHERE attribute.attrelid = '"dmscs"."VendorNamespacePrefix"'::regclass
              AND attribute.attname = 'NamespacePrefix';
            """
        ) ?? "";

    protected static async Task<int> InsertVendorAsync(NpgsqlConnection connection) =>
        await connection.ExecuteScalarAsync<int>(
            """INSERT INTO "dmscs"."Vendor" ("Company") VALUES (@Company) RETURNING "Id";""",
            new { Company = $"Vendor {Guid.NewGuid():N}" }
        );

    protected static Task InsertPrefixAsync(NpgsqlConnection connection, int vendorId, string prefix) =>
        connection.ExecuteAsync(
            """INSERT INTO "dmscs"."VendorNamespacePrefix" ("VendorId", "NamespacePrefix") VALUES (@VendorId, @Prefix);""",
            new { VendorId = vendorId, Prefix = prefix }
        );

    /// <summary>A namespace-shaped prefix of exactly <paramref name="length"/> characters.</summary>
    protected static string PrefixOfLength(int length) => "uri://vendor.example.org/".PadRight(length, 'x');
}

[TestFixture]
[Category(Category)]
public class Given_a_database_deployed_through_0036_with_a_128_character_prefix
    : VendorNamespacePrefixWideningUpgradeFixture
{
    private readonly string _seededPrefix = PrefixOfLength(128);
    private int _vendorId;
    private string _isNullableAfterUpgrade = "";
    private string[] _primaryKeyColumns = [];
    private string[] _prefixesAfterUpgrade = [];
    private string[] _journalAfterRepeatDeploy = [];
    private Exception? _insert255Failure;
    private PostgresException? _insert256Failure;

    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        _seededPrefix.Length.Should().Be(128);

        _vendorId = await InsertVendorAsync(connection);
        await InsertPrefixAsync(connection, _vendorId, _seededPrefix);
    }

    protected override async Task AfterUpgradeAsync(NpgsqlConnection connection)
    {
        _isNullableAfterUpgrade =
            await connection.ExecuteScalarAsync<string>(
                """
                SELECT is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'dmscs'
                  AND table_name = 'VendorNamespacePrefix'
                  AND column_name = 'NamespacePrefix';
                """
            ) ?? "";

        _primaryKeyColumns = (
            await connection.QueryAsync<string>(
                """
                SELECT attribute.attname
                FROM pg_constraint constraint_row
                CROSS JOIN LATERAL unnest(constraint_row.conkey) WITH ORDINALITY AS key_column(attnum, position)
                JOIN pg_attribute attribute
                    ON attribute.attrelid = constraint_row.conrelid AND attribute.attnum = key_column.attnum
                WHERE constraint_row.conname = 'PK_VendorNamespacePrefix'
                  AND constraint_row.conrelid = '"dmscs"."VendorNamespacePrefix"'::regclass
                ORDER BY key_column.position;
                """
            )
        ).ToArray();

        _prefixesAfterUpgrade = (
            await connection.QueryAsync<string>(
                """SELECT "NamespacePrefix" FROM "dmscs"."VendorNamespacePrefix" ORDER BY "NamespacePrefix";"""
            )
        ).ToArray();

        _insert255Failure = await TryInsertAsync(connection, PrefixOfLength(255));
        _insert256Failure = await TryInsertAsync(connection, PrefixOfLength(256)) as PostgresException;

        Redeploy().Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();
        _journalAfterRepeatDeploy = await JournalAsync();
    }

    private async Task<Exception?> TryInsertAsync(NpgsqlConnection connection, string prefix)
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
    public void It_widens_the_column_to_255() => ColumnTypeAfterUpgrade.Should().Be("character varying(255)");

    [Test]
    public void It_keeps_the_column_not_null() => _isNullableAfterUpgrade.Should().Be("NO");

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
        _insert256Failure!.SqlState.Should().Be("22001");
    }

    [Test]
    public void It_adds_no_journal_rows_on_a_repeat_deploy() =>
        _journalAfterRepeatDeploy.Should().Equal(JournalAfterUpgrade);
}

[TestFixture]
[Category(Category)]
public class Given_a_column_already_wider_than_255 : VendorNamespacePrefixWideningUpgradeFixture
{
    protected override async Task SeedAsync(NpgsqlConnection connection) =>
        await connection.ExecuteAsync(
            """ALTER TABLE "dmscs"."VendorNamespacePrefix" ALTER COLUMN "NamespacePrefix" TYPE VARCHAR(300);"""
        );

    [Test]
    public void It_upgrades_successfully() =>
        UpgradeResult.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

    [Test]
    public void It_does_not_shrink_the_column() =>
        ColumnTypeAfterUpgrade.Should().Be("character varying(300)");
}
