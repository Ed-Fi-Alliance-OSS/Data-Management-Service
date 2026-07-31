// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture]
[Category("MssqlIntegration")]
public class Given_MssqlCdcHeartbeatDatabase_Provider_Setup
{
    private string _databaseName = null!;
    private string _connectionString = null!;

    [SetUp]
    public void SetUp()
    {
        AssumeMssqlAvailable();

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);

        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);

        var (exitCode, output, error) = ProvisionTestHelper.RunProvision("mssql", _connectionString);
        exitCode
            .Should()
            .Be(0, $"ordinary SQL Server provisioning must succeed. Output: {output} Error: {error}");
    }

    [TearDown]
    public void TearDown()
    {
        if (!string.IsNullOrWhiteSpace(_databaseName))
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }
    }

    [Test]
    public async Task It_should_enable_database_cdc_and_create_heartbeat_only_when_opted_in()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var projectionPrerequisitesBefore = ReadProjectionPrerequisites(connection);
        IsDatabaseCdcEnabled(connection).Should().BeFalse("ordinary provisioning must not enable CDC");
        TableExists(connection, "CdcHeartbeat")
            .Should()
            .BeFalse("ordinary provisioning must not create CDC heartbeat");

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        result
            .HeartbeatActionQuery!.Sql.Should()
            .Be(
                "UPDATE [dms].[CdcHeartbeat] SET [HeartbeatSequence] = [HeartbeatSequence] + 1, [HeartbeatAt] = sysutcdatetime() WHERE [HeartbeatId] = 1"
            );
        result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.SafeObservedValues["database_cdc_enabled"] == "True"
                && observation.SafeObservedValues["capture_instance_count"] == "0"
            );
        result
            .ExpectedMessageKeyColumns.Should()
            .ContainSingle(key =>
                key.TableKind == CdcSourceTableKind.Document
                && key.KeyColumns.Select(column => column.Value).SequenceEqual(new[] { "DocumentUuid" })
            );
        result
            .ExpectedMessageKeyColumns.Should()
            .ContainSingle(key =>
                key.TableKind == CdcSourceTableKind.DocumentCache
                && key.KeyColumns.Select(column => column.Value).SequenceEqual(new[] { "DocumentUuid" })
            );

        IsDatabaseCdcEnabled(connection).Should().BeTrue();
        ReadProjectionPrerequisites(connection).Should().Be(projectionPrerequisitesBefore);
        AssertHeartbeatTable(connection);
        AssertNoCaptureInstances(connection);

        ExecuteNonQuery(connection, result.HeartbeatActionQuery.Sql);
        ReadHeartbeatSnapshot(connection).Should().Be(new HeartbeatSnapshot(1, 1));
    }

    [Test]
    public async Task It_should_exact_match_existing_database_cdc_and_heartbeat_without_mutating_heartbeat()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        var beforeValidate = ReadHeartbeatSnapshot(connection);

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        validateResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        ReadHeartbeatSnapshot(connection).Should().Be(beforeValidate);
    }

    [Test]
    public async Task It_should_report_missing_database_cdc_in_validate_only_without_creating_it()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISSING");
        IsDatabaseCdcEnabled(connection).Should().BeFalse();
        TableExists(connection, "CdcHeartbeat").Should().BeFalse();
    }

    private static CdcProviderSetupRequest BuildRequest(
        ICdcProviderDatabaseExecutor databaseExecutor,
        CdcProviderSetupMode mode
    ) =>
        new(
            provider: CdcProvider.SqlServer,
            mode: mode,
            boundPhysicalSourceFingerprint: new CdcSourceFingerprint(
                "dms-source-fingerprint-v1",
                "integration-source"
            ),
            setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("sa")),
            connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName("cdc_connector")),
            artifactNames: CdcProviderArtifactNames.ForSqlServer(
                new CdcSafeName("dms_binding_gate"),
                new Dictionary<CdcSourceTableKind, CdcSafeName>
                {
                    [CdcSourceTableKind.Document] = new("dms_binding_document"),
                    [CdcSourceTableKind.DocumentCache] = new("dms_binding_document_cache"),
                    [CdcSourceTableKind.CdcHeartbeat] = new("dms_binding_cdc_heartbeat"),
                }
            ),
            artifactOutput: new CdcProviderArtifactOutputRequest(IncludeManifestPayload: true),
            expectedSourceInventory: CdcSourceInventoryBuilder.BuildExpectedSourceInventory(
                SqlDialectFactory.Create(SqlDialect.Mssql)
            ),
            databaseExecutor: databaseExecutor
        );

    private static async Task<CdcProviderSetupResult> RunSetupAsync(
        SqlConnection connection,
        CdcProviderSetupMode mode
    )
    {
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);
        var executor = new DbConnectionCdcProviderDatabaseExecutor(connection);

        return await service.SetupAsync(BuildRequest(executor, mode));
    }

    private static void AssumeMssqlAvailable()
    {
        if (!MssqlTestDatabaseHelper.IsConfigured())
        {
            Assert.Ignore("SQL Server integration tests require a MssqlAdmin connection string.");
        }

        try
        {
            using var connection = new SqlConnection(DatabaseConfiguration.MssqlAdminConnectionString);
            connection.Open();
        }
        catch (SqlException exception)
        {
            Assert.Ignore(
                $"SQL Server integration tests require a reachable SQL Server: {exception.Message}"
            );
        }
    }

    private static bool IsDatabaseCdcEnabled(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT is_cdc_enabled
            FROM sys.databases
            WHERE name = DB_NAME();
            """;

        return Convert.ToBoolean(command.ExecuteScalar());
    }

    private static bool TableExists(SqlConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM information_schema.tables
            WHERE table_schema = 'dms'
            AND table_name = @table_name;
            """;
        command.Parameters.AddWithValue("table_name", tableName);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static ProjectionPrerequisites ReadProjectionPrerequisites(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                database_info.is_read_committed_snapshot_on,
                (
                    SELECT configuration_info.value_in_use
                    FROM sys.configurations configuration_info
                    WHERE configuration_info.name = N'nested triggers'
                ) AS nested_triggers_value
            FROM sys.databases database_info
            WHERE database_info.name = DB_NAME();
            """;

        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        var prerequisites = new ProjectionPrerequisites(reader.GetBoolean(0), reader.GetInt32(1));
        reader.Read().Should().BeFalse();
        return prerequisites;
    }

    private static void AssertHeartbeatTable(SqlConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    column_info.name,
                    type_info.name,
                    column_info.is_nullable,
                    column_info.scale
                FROM sys.columns column_info
                INNER JOIN sys.tables table_info
                    ON table_info.object_id = column_info.object_id
                INNER JOIN sys.schemas schema_info
                    ON schema_info.schema_id = table_info.schema_id
                INNER JOIN sys.types type_info
                    ON type_info.user_type_id = column_info.user_type_id
                WHERE schema_info.name = N'dms'
                AND table_info.name = N'CdcHeartbeat'
                ORDER BY column_info.column_id;
                """;

            using var reader = command.ExecuteReader();
            List<HeartbeatColumn> columns = [];
            while (reader.Read())
            {
                columns.Add(
                    new HeartbeatColumn(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetBoolean(2),
                        reader.GetByte(3)
                    )
                );
            }

            columns
                .Should()
                .Equal(
                    new HeartbeatColumn("HeartbeatId", "smallint", false, 0),
                    new HeartbeatColumn("HeartbeatSequence", "bigint", false, 0),
                    new HeartbeatColumn("HeartbeatAt", "datetime2", false, 7)
                );
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT constraint_name
                FROM information_schema.table_constraints
                WHERE table_schema = 'dms'
                AND table_name = 'CdcHeartbeat'
                ORDER BY constraint_name;
                """;

            using var reader = command.ExecuteReader();
            List<string> constraints = [];
            while (reader.Read())
            {
                constraints.Add(reader.GetString(0));
            }

            constraints
                .Should()
                .Contain(["CK_CdcHeartbeat_Sequence", "CK_CdcHeartbeat_Singleton", "PK_CdcHeartbeat"]);
        }

        ReadHeartbeatSnapshot(connection).Should().Be(new HeartbeatSnapshot(1, 0));
    }

    private static void AssertNoCaptureInstances(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'cdc.change_tables', N'U') IS NULL
                SELECT CONVERT(bigint, 0);
            ELSE
                SELECT COUNT_BIG(*) FROM cdc.change_tables;
            """;

        Convert.ToInt64(command.ExecuteScalar()).Should().Be(0);
    }

    private static HeartbeatSnapshot ReadHeartbeatSnapshot(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [HeartbeatId], [HeartbeatSequence]
            FROM [dms].[CdcHeartbeat];
            """;

        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        var snapshot = new HeartbeatSnapshot(reader.GetInt16(0), reader.GetInt64(1));
        reader.Read().Should().BeFalse();
        return snapshot;
    }

    private static void ExecuteNonQuery(SqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed record ProjectionPrerequisites(bool ReadCommittedSnapshotOn, int NestedTriggersValue);

    private sealed record HeartbeatColumn(string Name, string DataType, bool IsNullable, byte Scale);

    private sealed record HeartbeatSnapshot(short HeartbeatId, long HeartbeatSequence);
}
