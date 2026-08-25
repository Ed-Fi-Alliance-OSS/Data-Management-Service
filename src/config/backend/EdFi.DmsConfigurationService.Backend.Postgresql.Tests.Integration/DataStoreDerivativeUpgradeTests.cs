// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using FluentAssertions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// Exercises the DataStoreDerivative invariants upgrade against a journaled pre-upgrade database,
/// which is the state a real deployment upgrades from. Each test reverts the isolated database to
/// that state, seeds legacy rows directly with SQL so the API validator cannot filter them, and runs
/// the deploy again so only the invariants script executes. Replaying every script instead would let
/// a non-replay-safe earlier script decide the outcome.
/// </summary>
[TestFixture]
public class Given_a_legacy_DataStoreDerivative_PostgreSQL_upgrade
{
    private const string JournalPattern = "%0028_Add_DataStoreDerivative_Invariants%";
    private const string RemediationPut = "PUT /v3/dataStoreDerivatives/{id}";
    private const string RemediationDelete = "DELETE /v3/dataStoreDerivatives/{id}";
    private const string AllowedValues = "Allowed values are exactly 'Snapshot' and 'ReadReplica'.";

    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private int _firstDataStoreId;
    private int _secondDataStoreId;
    private int _thirdDataStoreId;
    private int _fourthDataStoreId;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        _connectionString = CreateIsolatedDatabaseConnectionString();
        DeploySuccessfully();
    }

    [OneTimeTearDown]
    public async Task OneTimeTeardown()
    {
        if (string.IsNullOrEmpty(_databaseName))
        {
            return;
        }

        await using NpgsqlConnection connection = new(CreateMaintenanceConnectionString());
        await connection.OpenAsync();
        await connection.ExecuteAsync($"""DROP DATABASE IF EXISTS "{_databaseName}" WITH (FORCE);""");
    }

    [SetUp]
    public async Task Setup()
    {
        await RevertToPreUpgradeStateAsync();

        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        _firstDataStoreId = await InsertDataStoreAsync(connection, "Legacy Data Store A");
        _secondDataStoreId = await InsertDataStoreAsync(connection, "Legacy Data Store B");
        _thirdDataStoreId = await InsertDataStoreAsync(connection, "Legacy Data Store C");
        _fourthDataStoreId = await InsertDataStoreAsync(connection, "Legacy Data Store D");
    }

    [Test]
    public async Task It_should_upgrade_a_conforming_legacy_database_and_replace_the_redundant_index()
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        await InsertDerivativeAsync(connection, _firstDataStoreId, "Snapshot");
        await InsertDerivativeAsync(connection, _firstDataStoreId, "ReadReplica");
        await InsertDerivativeAsync(connection, _secondDataStoreId, "Snapshot");

        DatabaseDeployResult result = Deploy();

        result.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

        int journaledUpgrades = await connection.ExecuteScalarAsync<int>(
            """SELECT count(*) FROM public."dmscs_SchemaVersions" WHERE scriptname LIKE @Pattern;""",
            new { Pattern = JournalPattern }
        );
        journaledUpgrades.Should().Be(1, "the upgrade script is journaled exactly once");

        (await ConstraintExistsAsync(connection, "UX_DataStoreDerivative_DataStoreId_DerivativeType"))
            .Should()
            .BeTrue();
        (await ConstraintExistsAsync(connection, "CK_DataStoreDerivative_DerivativeType")).Should().BeTrue();

        string[] uniqueKeyColumns = (
            await connection.QueryAsync<string>(UniqueConstraintColumnsSql)
        ).ToArray();
        uniqueKeyColumns.Should().Equal("DataStoreId", "DerivativeType");

        int redundantIndexes = await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)
            FROM pg_index index_catalog
            JOIN pg_class index_info ON index_info.oid = index_catalog.indexrelid
            WHERE index_info.relname = 'IX_DataStoreDerivative_DataStoreId';
            """
        );
        redundantIndexes.Should().Be(0, "the unique constraint's backing index leads with DataStoreId");
    }

    [Test]
    public async Task It_should_accept_the_exact_allowed_values_and_reject_variants_after_the_upgrade()
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        Deploy().Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

        await InsertDerivativeAsync(connection, _firstDataStoreId, "Snapshot");
        await InsertDerivativeAsync(connection, _firstDataStoreId, "ReadReplica");

        (await TryInsertDerivativeAsync(connection, _secondDataStoreId, "SNAPSHOT"))
            .Should()
            .Be(PostgresErrorCodes.CheckViolation, "the check constraint compares ordinally");
        (await TryInsertDerivativeAsync(connection, _secondDataStoreId, "Snapshot "))
            .Should()
            .Be(PostgresErrorCodes.CheckViolation, "the check constraint compares stored length exactly");
        (await TryInsertDerivativeAsync(connection, _secondDataStoreId, "Replica"))
            .Should()
            .Be(PostgresErrorCodes.CheckViolation);
        (await TryInsertDerivativeAsync(connection, _firstDataStoreId, "Snapshot"))
            .Should()
            .Be(PostgresErrorCodes.UniqueViolation, "a data store holds at most one derivative per type");
    }

    [Test]
    public async Task It_should_block_the_upgrade_when_duplicate_rows_exist()
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        int firstId = await InsertDerivativeAsync(connection, _firstDataStoreId, "Snapshot");
        int secondId = await InsertDerivativeAsync(connection, _firstDataStoreId, "Snapshot");
        await InsertDerivativeAsync(connection, _secondDataStoreId, "ReadReplica");

        string message = DeployFailureMessage();

        message.Should().Contain("2 duplicate (DataStoreId, DerivativeType) row(s)");
        message.Should().Contain($"({_firstDataStoreId}, Snapshot, {firstId})");
        message.Should().Contain($"({_firstDataStoreId}, Snapshot, {secondId})");
        message.Should().Contain(RemediationPut);
        message.Should().Contain(RemediationDelete);
        message.Should().Contain(AllowedValues);
        message
            .Should()
            .NotContain(
                "row(s) with an invalid DerivativeType",
                "no invalid-type condition is present; the remediation text names the term either way"
            );
    }

    [Test]
    public async Task It_should_block_the_upgrade_when_an_invalid_type_exists()
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        int invalidId = await InsertDerivativeAsync(connection, _firstDataStoreId, "Replica");

        string message = DeployFailureMessage();

        message.Should().Contain("1 row(s) with an invalid DerivativeType");
        message.Should().Contain($"({invalidId}, {_firstDataStoreId}, 'Replica')");
        message.Should().Contain(RemediationPut);
        message.Should().Contain(RemediationDelete);
        message.Should().Contain(AllowedValues);
    }

    [Test]
    public async Task It_should_block_the_upgrade_when_a_case_variant_exists()
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        int caseVariantId = await InsertDerivativeAsync(connection, _firstDataStoreId, "SNAPSHOT");

        string message = DeployFailureMessage();

        message.Should().Contain("1 row(s) with an invalid DerivativeType");
        message.Should().Contain($"({caseVariantId}, {_firstDataStoreId}, 'SNAPSHOT')");
    }

    [Test]
    public async Task It_should_block_the_upgrade_when_a_trailing_whitespace_variant_exists()
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        int paddedId = await InsertDerivativeAsync(connection, _firstDataStoreId, "Snapshot ");

        string message = DeployFailureMessage();

        message.Should().Contain("1 row(s) with an invalid DerivativeType");
        message
            .Should()
            .Contain(
                $"({paddedId}, {_firstDataStoreId}, 'Snapshot ')",
                "quoting the value is what makes the trailing space visible to an operator"
            );
    }

    [Test]
    public async Task It_should_keep_every_mandatory_diagnostic_section_at_high_cardinality()
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        // Seeded first so it leads the invalid-type ordering and is therefore one of the tuples the
        // capped list must still carry. Hosted alone on a data store that holds no other row, so it
        // is a duplicate under neither engine's own equality - SQL Server's padded, case-insensitive
        // comparison would otherwise make it a duplicate of a Snapshot sibling and the two providers
        // could not assert the same totals.
        int paddedId = await InsertDerivativeAsync(connection, _fourthDataStoreId, "Snapshot ");

        for (int row = 0; row < 30; row++)
        {
            await InsertDerivativeAsync(connection, _firstDataStoreId, WidestInvalidType(row));
        }

        int[] duplicateHosts = [_firstDataStoreId, _secondDataStoreId, _thirdDataStoreId];
        foreach (int dataStoreId in duplicateHosts)
        {
            for (int row = 0; row < 10; row++)
            {
                await InsertDerivativeAsync(connection, dataStoreId, "Snapshot");
            }
        }

        string message = DeployFailureMessage();

        message.Should().Contain("30 duplicate (DataStoreId, DerivativeType) row(s)");
        message.Should().Contain("31 row(s) with an invalid DerivativeType");
        message.Should().Contain($"({_firstDataStoreId}, Snapshot, ");
        message
            .Should()
            .Contain(
                $"({paddedId}, {_fourthDataStoreId}, 'Snapshot ')",
                "the quoted trailing space stays visible even when the list is capped"
            );
        message.Should().Contain($"'{WidestInvalidType(0)}'", "the widest storable value is carried whole");
        message.Should().Contain("... and ", "capped lists state how many offenders were not listed");
        message.Should().Contain(RemediationPut);
        message.Should().Contain(RemediationDelete);
        message.Should().Contain(AllowedValues);
        message
            .Length.Should()
            .BeLessThanOrEqualTo(
                2048,
                "the tuple budget is computed before the lists are built, so the message never needs truncating"
            );
    }

    /// <summary>
    /// A distinct value at the full 50-character width of the column, which is the widest tuple the
    /// diagnostics can be asked to carry.
    /// </summary>
    private static string WidestInvalidType(int ordinal) => $"Legacy{ordinal:D2}".PadRight(50, 'x');

    private async Task RevertToPreUpgradeStateAsync()
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync(
            """
            ALTER TABLE "dmscs"."DataStoreDerivative"
                DROP CONSTRAINT IF EXISTS "UX_DataStoreDerivative_DataStoreId_DerivativeType";
            ALTER TABLE "dmscs"."DataStoreDerivative"
                DROP CONSTRAINT IF EXISTS "CK_DataStoreDerivative_DerivativeType";
            CREATE INDEX IF NOT EXISTS "IX_DataStoreDerivative_DataStoreId"
                ON "dmscs"."DataStoreDerivative" ("DataStoreId");
            """
        );

        int removedJournalEntries = await connection.ExecuteAsync(
            """DELETE FROM public."dmscs_SchemaVersions" WHERE scriptname LIKE @Pattern;""",
            new { Pattern = JournalPattern }
        );
        removedJournalEntries
            .Should()
            .BeLessThanOrEqualTo(1, "the pattern must identify only the invariants upgrade script");

        await connection.ExecuteAsync(
            """
            DELETE FROM "dmscs"."DataStoreDerivative";
            DELETE FROM "dmscs"."DataStore";
            """
        );
    }

    private static async Task<int> InsertDataStoreAsync(NpgsqlConnection connection, string name) =>
        await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO "dmscs"."DataStore" ("DataStoreType", "Name")
            VALUES ('Production', @Name)
            RETURNING "Id";
            """,
            new { Name = name }
        );

    private static async Task<int> InsertDerivativeAsync(
        NpgsqlConnection connection,
        int dataStoreId,
        string derivativeType
    ) =>
        await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO "dmscs"."DataStoreDerivative" ("DataStoreId", "DerivativeType")
            VALUES (@DataStoreId, @DerivativeType)
            RETURNING "Id";
            """,
            new { DataStoreId = dataStoreId, DerivativeType = derivativeType }
        );

    private static async Task<string> TryInsertDerivativeAsync(
        NpgsqlConnection connection,
        int dataStoreId,
        string derivativeType
    )
    {
        try
        {
            await InsertDerivativeAsync(connection, dataStoreId, derivativeType);
            return string.Empty;
        }
        catch (PostgresException exception)
        {
            return exception.SqlState;
        }
    }

    private static async Task<bool> ConstraintExistsAsync(NpgsqlConnection connection, string name) =>
        await connection.ExecuteScalarAsync<int>(
            """
            SELECT count(*)
            FROM pg_constraint
            WHERE conname = @Name
              AND conrelid = '"dmscs"."DataStoreDerivative"'::regclass;
            """,
            new { Name = name }
        ) > 0;

    private DatabaseDeployResult Deploy() => new Deploy.DatabaseDeploy().DeployDatabase(_connectionString);

    private void DeploySuccessfully()
    {
        DatabaseDeployResult result = Deploy();

        if (result is DatabaseDeployResult.DatabaseDeployFailure failure)
        {
            Assert.Fail($"Database deploy failed: {failure.Error}");
        }
    }

    private string DeployFailureMessage()
    {
        DatabaseDeployResult result = Deploy();

        DatabaseDeployResult.DatabaseDeployFailure failure = result
            .Should()
            .BeOfType<DatabaseDeployResult.DatabaseDeployFailure>()
            .Subject;

        PostgresException postgresException = FindPostgresException(failure.Error);
        return postgresException.MessageText;
    }

    private static PostgresException FindPostgresException(Exception exception)
    {
        for (Exception? candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is PostgresException postgresException)
            {
                return postgresException;
            }
        }

        throw new AssertionException($"Expected a PostgresException but found: {exception}");
    }

    private string CreateIsolatedDatabaseConnectionString()
    {
        NpgsqlConnectionStringBuilder builder = new(Configuration.DatabaseOptions.Value.DatabaseConnection)
        {
            Database = $"dms1366_upgrade_{Guid.NewGuid():N}",
            Pooling = false,
        };

        _databaseName = builder.Database!;
        return builder.ConnectionString;
    }

    private static string CreateMaintenanceConnectionString()
    {
        NpgsqlConnectionStringBuilder builder = new(Configuration.DatabaseOptions.Value.DatabaseConnection)
        {
            Database = "postgres",
            Pooling = false,
        };

        return builder.ConnectionString;
    }

    private const string UniqueConstraintColumnsSql = """
        SELECT attribute_info.attname
        FROM pg_constraint constraint_info
        JOIN LATERAL unnest(constraint_info.conkey) WITH ORDINALITY AS key_columns(attnum, ordinality)
            ON true
        JOIN pg_attribute attribute_info
            ON attribute_info.attrelid = constraint_info.conrelid
           AND attribute_info.attnum = key_columns.attnum
        WHERE constraint_info.conname = 'UX_DataStoreDerivative_DataStoreId_DerivativeType'
        ORDER BY key_columns.ordinality;
        """;
}
