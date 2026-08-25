// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using EdFi.DmsConfigurationService.Backend.Deploy;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// Exercises the DataStoreDerivative invariants upgrade against a journaled pre-upgrade database,
/// which is the state a real deployment upgrades from. Each test reverts the isolated database to
/// that state, seeds legacy rows directly with SQL so the API validator cannot filter them, and runs
/// the deploy again so only the invariants script executes. Replaying every script instead would let
/// a non-replay-safe earlier script decide the outcome.
/// </summary>
[TestFixture]
[Category("MssqlIntegration")]
public class Given_a_legacy_DataStoreDerivative_SqlServer_upgrade
{
    private const string JournalPattern = "%0028_Add_DataStoreDerivative_Invariants%";
    private const string RemediationPut = "PUT /v3/dataStoreDerivatives/{id}";
    private const string RemediationDelete = "DELETE /v3/dataStoreDerivatives/{id}";
    private const string AllowedValues = "Allowed values are exactly 'Snapshot' and 'ReadReplica'.";
    private const int CheckOrForeignKeyViolation = 547;
    private const int UniqueConstraintViolation = 2627;

    private string _databaseName = string.Empty;
    private string _connectionString = string.Empty;
    private int _firstDataStoreId;
    private int _secondDataStoreId;
    private int _thirdDataStoreId;
    private int _fourthDataStoreId;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        MssqlTestConfiguration.RequireConfiguredForCiOrSkipLocally(
            "SQL Server integration tests require the ConnectionStrings__MssqlAdmin environment variable."
        );

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

        await using SqlConnection connection = new(CreateMasterConnectionString());
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            $"""
            IF DB_ID('{_databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_databaseName}];
            END;
            """
        );
    }

    [SetUp]
    public async Task Setup()
    {
        await RevertToPreUpgradeStateAsync();

        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        _firstDataStoreId = await InsertDataStoreAsync(connection, "Legacy Data Store A");
        _secondDataStoreId = await InsertDataStoreAsync(connection, "Legacy Data Store B");
        _thirdDataStoreId = await InsertDataStoreAsync(connection, "Legacy Data Store C");
        _fourthDataStoreId = await InsertDataStoreAsync(connection, "Legacy Data Store D");
    }

    [Test]
    public async Task It_should_upgrade_a_conforming_legacy_database_and_replace_the_redundant_index()
    {
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        await InsertDerivativeAsync(connection, _firstDataStoreId, "Snapshot");
        await InsertDerivativeAsync(connection, _firstDataStoreId, "ReadReplica");
        await InsertDerivativeAsync(connection, _secondDataStoreId, "Snapshot");

        DatabaseDeployResult result = Deploy();

        result.Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

        int journaledUpgrades = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.dmscs_SchemaVersions WHERE ScriptName LIKE @Pattern;",
            new { Pattern = JournalPattern }
        );
        journaledUpgrades.Should().Be(1, "the upgrade script is journaled exactly once");

        (await ObjectExistsAsync(connection, "UX_DataStoreDerivative_DataStoreId_DerivativeType"))
            .Should()
            .BeTrue();
        (await ObjectExistsAsync(connection, "CK_DataStoreDerivative_DerivativeType")).Should().BeTrue();

        string[] uniqueKeyColumns = (
            await connection.QueryAsync<string>(UniqueConstraintColumnsSql)
        ).ToArray();
        uniqueKeyColumns.Should().Equal("DataStoreId", "DerivativeType");

        int redundantIndexes = await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM sys.indexes
            WHERE name = 'IX_DataStoreDerivative_DataStoreId'
              AND object_id = OBJECT_ID('dmscs.DataStoreDerivative');
            """
        );
        redundantIndexes.Should().Be(0, "the unique constraint's backing index leads with DataStoreId");
    }

    [Test]
    public async Task It_should_accept_the_exact_allowed_values_and_reject_variants_after_the_upgrade()
    {
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        Deploy().Should().BeOfType<DatabaseDeployResult.DatabaseDeploySuccess>();

        await InsertDerivativeAsync(connection, _firstDataStoreId, "Snapshot");
        await InsertDerivativeAsync(connection, _firstDataStoreId, "ReadReplica");

        SqlException caseVariant = await ExpectInsertFailureAsync(connection, _secondDataStoreId, "SNAPSHOT");
        caseVariant
            .Number.Should()
            .Be(CheckOrForeignKeyViolation, "the binary collation makes the comparison ordinal");
        caseVariant.Message.Should().Contain("CK_DataStoreDerivative_DerivativeType");

        SqlException padded = await ExpectInsertFailureAsync(connection, _secondDataStoreId, "Snapshot ");
        padded
            .Number.Should()
            .Be(CheckOrForeignKeyViolation, "DATALENGTH rejects the padded value that = and IN would accept");
        padded.Message.Should().Contain("CK_DataStoreDerivative_DerivativeType");

        SqlException unknownType = await ExpectInsertFailureAsync(connection, _secondDataStoreId, "Replica");
        unknownType.Number.Should().Be(CheckOrForeignKeyViolation);

        SqlException duplicate = await ExpectInsertFailureAsync(connection, _firstDataStoreId, "Snapshot");
        duplicate
            .Number.Should()
            .Be(UniqueConstraintViolation, "a data store holds at most one derivative per type");
        duplicate.Message.Should().Contain("UX_DataStoreDerivative_DataStoreId_DerivativeType");
    }

    [Test]
    public async Task It_should_block_the_upgrade_when_duplicate_rows_exist()
    {
        await using SqlConnection connection = new(_connectionString);
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
        await using SqlConnection connection = new(_connectionString);
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
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        int caseVariantId = await InsertDerivativeAsync(connection, _firstDataStoreId, "SNAPSHOT");

        string message = DeployFailureMessage();

        message
            .Should()
            .Contain(
                "1 row(s) with an invalid DerivativeType",
                "a naive IN scan would miss this row under a case-insensitive collation"
            );
        message.Should().Contain($"({caseVariantId}, {_firstDataStoreId}, 'SNAPSHOT')");
    }

    [Test]
    public async Task It_should_block_the_upgrade_when_a_trailing_whitespace_variant_exists()
    {
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        int paddedId = await InsertDerivativeAsync(connection, _firstDataStoreId, "Snapshot ");

        string message = DeployFailureMessage();

        message
            .Should()
            .Contain(
                "1 row(s) with an invalid DerivativeType",
                "SQL-92 padding would let = and IN accept this row at any collation"
            );
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
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        // Seeded first so it leads the invalid-type ordering and is therefore one of the tuples the
        // capped list must still carry. Hosted alone on a data store that holds no other row, so
        // SQL Server's padded, case-insensitive comparison cannot make it a duplicate of a Snapshot
        // sibling and both providers assert the same totals.
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
        await using SqlConnection connection = new(_connectionString);
        await connection.OpenAsync();

        await connection.ExecuteAsync(
            """
            ALTER TABLE dmscs.DataStoreDerivative
                DROP CONSTRAINT IF EXISTS UX_DataStoreDerivative_DataStoreId_DerivativeType;
            ALTER TABLE dmscs.DataStoreDerivative
                DROP CONSTRAINT IF EXISTS CK_DataStoreDerivative_DerivativeType;
            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE name = 'IX_DataStoreDerivative_DataStoreId'
                  AND object_id = OBJECT_ID('dmscs.DataStoreDerivative')
            )
                CREATE INDEX IX_DataStoreDerivative_DataStoreId ON dmscs.DataStoreDerivative (DataStoreId);
            """
        );

        int removedJournalEntries = await connection.ExecuteAsync(
            "DELETE FROM dbo.dmscs_SchemaVersions WHERE ScriptName LIKE @Pattern;",
            new { Pattern = JournalPattern }
        );
        removedJournalEntries
            .Should()
            .BeLessThanOrEqualTo(1, "the pattern must identify only the invariants upgrade script");

        await connection.ExecuteAsync(
            """
            DELETE FROM dmscs.DataStoreDerivative;
            DELETE FROM dmscs.DataStore;
            """
        );
    }

    private static async Task<int> InsertDataStoreAsync(SqlConnection connection, string name) =>
        await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.DataStore (DataStoreType, Name)
            OUTPUT INSERTED.Id
            VALUES ('Production', @Name);
            """,
            new { Name = name }
        );

    private static async Task<int> InsertDerivativeAsync(
        SqlConnection connection,
        int dataStoreId,
        string derivativeType
    ) =>
        await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO dmscs.DataStoreDerivative (DataStoreId, DerivativeType)
            OUTPUT INSERTED.Id
            VALUES (@DataStoreId, @DerivativeType);
            """,
            new { DataStoreId = dataStoreId, DerivativeType = derivativeType }
        );

    private static async Task<SqlException> ExpectInsertFailureAsync(
        SqlConnection connection,
        int dataStoreId,
        string derivativeType
    )
    {
        try
        {
            await InsertDerivativeAsync(connection, dataStoreId, derivativeType);
        }
        catch (SqlException exception)
        {
            return exception;
        }

        throw new AssertionException(
            $"Expected the database to reject DerivativeType '{derivativeType}' but the insert succeeded."
        );
    }

    private static async Task<bool> ObjectExistsAsync(SqlConnection connection, string name) =>
        await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM sys.objects
            WHERE name = @Name
              AND parent_object_id = OBJECT_ID('dmscs.DataStoreDerivative');
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

        return FindSqlException(failure.Error).Message;
    }

    private static SqlException FindSqlException(Exception exception)
    {
        for (Exception? candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is SqlException sqlException)
            {
                return sqlException;
            }
        }

        throw new AssertionException($"Expected a SqlException but found: {exception}");
    }

    private string CreateIsolatedDatabaseConnectionString()
    {
        SqlConnectionStringBuilder builder = new(MssqlTestConfiguration.AdminConnectionString)
        {
            InitialCatalog = $"dms1366_upgrade_{Guid.NewGuid():N}",
            Pooling = false,
        };

        _databaseName = builder.InitialCatalog;
        return builder.ConnectionString;
    }

    private static string CreateMasterConnectionString() =>
        new SqlConnectionStringBuilder(MssqlTestConfiguration.AdminConnectionString)
        {
            InitialCatalog = "master",
            Pooling = false,
        }.ConnectionString;

    private const string UniqueConstraintColumnsSql = """
        SELECT column_info.name
        FROM sys.key_constraints constraint_info
        JOIN sys.indexes index_info
            ON index_info.object_id = constraint_info.parent_object_id
           AND index_info.index_id = constraint_info.unique_index_id
        JOIN sys.index_columns index_column_info
            ON index_column_info.object_id = index_info.object_id
           AND index_column_info.index_id = index_info.index_id
        JOIN sys.columns column_info
            ON column_info.object_id = index_column_info.object_id
           AND column_info.column_id = index_column_info.column_id
        WHERE constraint_info.name = 'UX_DataStoreDerivative_DataStoreId_DerivativeType'
          AND index_column_info.is_included_column = 0
        ORDER BY index_column_info.key_ordinal;
        """;
}
