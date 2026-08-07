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
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("MssqlCdcArtifacts")]
public class Given_MssqlCdcHeartbeatDatabase_Provider_Setup
{
    private const string ConnectorPassword = "EdFi_Dms1!";
    private string _databaseName = null!;
    private string _connectionString = null!;
    private string _connectorPrincipalName = null!;

    [SetUp]
    public void SetUp()
    {
        AssumeMssqlAvailable();

        _databaseName = MssqlTestDatabaseHelper.GenerateUniqueDatabaseName();
        _connectionString = MssqlTestDatabaseHelper.BuildConnectionString(_databaseName);
        _connectorPrincipalName = $"cdc_connector_{Guid.NewGuid():N}";

        MssqlTestDatabaseHelper.CreateDatabase(_databaseName);

        var (exitCode, output, error) = ProvisionTestHelper.RunProvision("mssql", _connectionString);
        exitCode
            .Should()
            .Be(0, $"ordinary SQL Server provisioning must succeed. Output: {output} Error: {error}");

        CreateConnectorLoginAndUser(_databaseName, _connectorPrincipalName);
    }

    [TearDown]
    public void TearDown()
    {
        if (!string.IsNullOrWhiteSpace(_databaseName))
        {
            MssqlTestDatabaseHelper.DropDatabaseIfExists(_databaseName);
        }

        if (!string.IsNullOrWhiteSpace(_connectorPrincipalName))
        {
            DropConnectorLoginIfExists(_connectorPrincipalName);
        }
    }

    [Test]
    [Category("MssqlCdcAccessRetry")]
    public async Task MssqlCdcAccessRetry_MssqlCdcArtifacts_and_MssqlCdcCaptureInstances_should_enable_database_cdc_heartbeat_and_capture_instances_only_when_opted_in()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var projectionPrerequisitesBefore = ReadProjectionPrerequisites(connection);
        IsDatabaseCdcEnabled(connection).Should().BeFalse("ordinary provisioning must not enable CDC");
        TableExists(connection, "CdcHeartbeat")
            .Should()
            .BeFalse("ordinary provisioning must not create CDC heartbeat");
        AssertNoCaptureInstances(connection);

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.CreatedOrMatched, DescribeDiagnostics(result.Diagnostics));
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
            );
        result
            .ArtifactInventory.Where(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            )
            .Should()
            .HaveCount(3)
            .And.OnlyContain(observation => observation.State == CdcProviderArtifactState.Created);
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
        AssertCaptureInstances(connection);

        ExecuteNonQuery(connection, result.HeartbeatActionQuery.Sql);
        ReadHeartbeatSnapshot(connection).Should().Be(new HeartbeatSnapshot(1, 1));
    }

    [Test]
    [Category("MssqlCdcAccessRetry")]
    public async Task MssqlCdcPrincipalAccess_MssqlCdcAccessRetry_should_grant_and_validate_connector_principal_boundaries()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        setupResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.CreatedOrMatched, DescribeDiagnostics(setupResult.Diagnostics));
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        setupResult
            .GrantInventory.Should()
            .ContainSingle(grant =>
                grant.SafePrincipalName.Value == _connectorPrincipalName
                && grant.SafeObjectName.Value == "role.dms_binding_gate"
                && grant.Privileges.SequenceEqual(new[] { "MEMBER" })
            );
        setupResult
            .GrantInventory.Should()
            .ContainSingle(grant =>
                grant.SafePrincipalName.Value == _connectorPrincipalName
                && grant.SafeObjectName.Value == "dms.CdcHeartbeat"
                && grant.Privileges.SequenceEqual(new[] { "UPDATE" })
                && grant
                    .Columns.Select(column => column.Value)
                    .SequenceEqual(new[] { "HeartbeatSequence", "HeartbeatAt" })
            );
        setupResult.ManifestPayload!.Json.Should().NotContain(ConnectorPassword);
        setupResult.ManifestPayload.Json.Should().NotContain(_connectionString);

        AssertConnectorPrincipalAccess(connection);

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);
        validateResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.ExactMatch, DescribeDiagnostics(validateResult.Diagnostics));
        validateResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
    }

    [Test]
    public async Task MssqlCdcProviderMetadata_should_report_source_fingerprint_database_history_and_capture_inventory()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var expectedSourceIdentity = ReadDataStoreIdentity(connection);
        var expectedSourceFingerprint = CdcSourceFingerprintMetadata.Compute(
            CdcProvider.SqlServer,
            expectedSourceIdentity
        );
        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.CreatedOrMatched, DescribeDiagnostics(result.Diagnostics));
        result.ObservedSourceFingerprint.Should().Be(expectedSourceFingerprint);
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SourceFingerprint
                && observation.SafeArtifactName.Value == "dms.DataStoreIdentity"
                && observation.State == CdcProviderArtifactState.Matched
            );
        result
            .ProviderHistoryObservations.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.SafeObservedValues["database_cdc_enabled"] == "True"
                && observation.SafeObservedValues["capture_instance_count"] == "3"
                && observation.SafeObservedValues["retained_lsn_gap_evaluation"]
                    == "not_evaluated_without_committed_offset"
            );
        result
            .ProviderHistoryObservations.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == "dms_binding_cdc_heartbeat"
                && observation.SafeObservedValues["heartbeat_capture_visible"] == "True"
            );
        result.ManifestPayload!.Json.Should().Contain(expectedSourceFingerprint.Value);
        result.ManifestPayload.Json.Should().NotContain(expectedSourceIdentity);
        result.ManifestPayload.Json.Should().NotContain(_connectionString);
    }

    [Test]
    public async Task CdcWorkTableExclusion_should_exclude_projection_work_from_result_and_raw_cdc()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var documentId = InsertDocumentBeforeProviderCapture(connection);

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);

        result
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.CreatedOrMatched, DescribeDiagnostics(result.Diagnostics));
        result
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        result
            .SourceTableInventory.Select(table => $"{table.TableName.Schema.Value}.{table.TableName.Name}")
            .Should()
            .BeEquivalentTo("dms.Document", "dms.DocumentCache", "dms.CdcHeartbeat")
            .And.NotContain("dms.DocumentProjectionWork");
        result
            .ArtifactInventory.Where(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            )
            .Should()
            .HaveCount(3)
            .And.NotContain(observation =>
                observation.SafeObservedValues.Values.Any(value =>
                    value.Contains("DocumentProjectionWork", StringComparison.Ordinal)
                )
            );
        result
            .GrantInventory.Should()
            .NotContain(grant => grant.SafeObjectName.Value == "dms.DocumentProjectionWork");
        result.ManifestPayload!.Json.Should().NotContain("DocumentProjectionWork");

        ExecuteProjectionWorkDml(connection, documentId);

        AssertNoProjectionWorkCapture(connection);
    }

    [Test]
    [Category("MssqlCdcAccessRetry")]
    public async Task MssqlCdcAccessRetry_should_exact_match_existing_database_cdc_and_heartbeat_without_mutating_heartbeat()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var setupResult = await RunSetupAsync(connection, CdcProviderSetupMode.InitialCreateOrExactMatch);
        setupResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        var beforeValidate = ReadHeartbeatSnapshot(connection);

        var validateResult = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        validateResult
            .Outcome.Should()
            .Be(CdcProviderSetupOutcome.ExactMatch, DescribeDiagnostics(validateResult.Diagnostics));
        validateResult
            .Diagnostics.Should()
            .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
        validateResult
            .ArtifactInventory.Where(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
            )
            .Should()
            .HaveCount(3)
            .And.OnlyContain(observation => observation.State == CdcProviderArtifactState.Matched);
        ReadHeartbeatSnapshot(connection).Should().Be(beforeValidate);
    }

    [Test]
    [Category("MssqlCdcAccessRetry")]
    public async Task MssqlCdcAccessRetry_should_report_missing_database_cdc_in_validate_only_without_creating_it()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var result = await RunSetupAsync(connection, CdcProviderSetupMode.ValidateOnly);

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_SQLSERVER_DATABASE_CDC_MISSING"
                && diagnostic.Category == CdcProviderDiagnosticCategory.ProviderHistoryLossEvidence
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.SourceHistoryLost
            );
        IsDatabaseCdcEnabled(connection).Should().BeFalse();
        TableExists(connection, "CdcHeartbeat").Should().BeFalse();
    }

    [Test]
    [Category("MssqlCdcAccessRetry")]
    public async Task MssqlCdcBindingAwareValidation_MssqlCdcAccessRetry_should_fail_before_enabling_cdc_when_source_fingerprint_mismatches()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var result = await RunSetupAsync(
            connection,
            CdcProviderSetupMode.InitialCreateOrExactMatch,
            boundSourceIdentity: "11111111-1111-1111-1111-111111111111"
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_BINDING_SOURCE_FINGERPRINT_MISMATCH");
        IsDatabaseCdcEnabled(connection).Should().BeFalse();
        TableExists(connection, "CdcHeartbeat").Should().BeFalse();
    }

    private CdcProviderSetupRequest BuildRequest(
        ICdcProviderDatabaseExecutor databaseExecutor,
        CdcProviderSetupMode mode,
        string boundSourceIdentity
    )
    {
        var emission = CdcSchemaToolsTestMetadata.BuildMinimalDdlEmission(SqlDialect.Mssql);

        return new(
            provider: CdcProvider.SqlServer,
            mode: mode,
            boundPhysicalSourceFingerprint: CdcSourceFingerprintMetadata.Compute(
                CdcProvider.SqlServer,
                boundSourceIdentity
            ),
            setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("sa")),
            connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName(_connectorPrincipalName)),
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
            expectedSourceInventory: emission.CdcSourceInventory,
            dmsManagedTableInventory: emission.CdcDmsManagedTableInventory,
            databaseExecutor: databaseExecutor
        );
    }

    private async Task<CdcProviderSetupResult> RunSetupAsync(
        SqlConnection connection,
        CdcProviderSetupMode mode,
        string? boundSourceIdentity = null
    )
    {
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);
        var executor = new DbConnectionCdcProviderDatabaseExecutor(connection);

        return await service.SetupAsync(
            BuildRequest(executor, mode, boundSourceIdentity ?? ReadDataStoreIdentity(connection))
        );
    }

    private static string ReadDataStoreIdentity(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(nvarchar(36), [SourceIdentity])
            FROM [dms].[DataStoreIdentity]
            WHERE [DataStoreIdentitySingletonId] = 1;
            """;
        return command.ExecuteScalar()!.ToString()!;
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

    private static void CreateConnectorLoginAndUser(string databaseName, string connectorPrincipalName)
    {
        using var connection = new SqlConnection(DatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        var quotedDatabase = QuoteIdentifier(databaseName);
        var quotedPrincipal = QuoteIdentifier(connectorPrincipalName);
        command.CommandText = $"""
            IF SUSER_ID(N'{connectorPrincipalName}') IS NULL
            BEGIN
                CREATE LOGIN {quotedPrincipal} WITH PASSWORD = '{ConnectorPassword}', CHECK_POLICY = OFF;
            END;

            USE {quotedDatabase};

            IF USER_ID(N'{connectorPrincipalName}') IS NULL
            BEGIN
                CREATE USER {quotedPrincipal} FOR LOGIN {quotedPrincipal};
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static void DropConnectorLoginIfExists(string connectorPrincipalName)
    {
        SqlConnection.ClearAllPools();

        using var connection = new SqlConnection(DatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF SUSER_ID(N'{connectorPrincipalName}') IS NOT NULL
            BEGIN
                DROP LOGIN {QuoteIdentifier(connectorPrincipalName)};
            END;
            """;
        command.ExecuteNonQuery();
    }

    private void AssertConnectorPrincipalAccess(SqlConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT member_principal.name
                FROM sys.database_role_members role_member
                INNER JOIN sys.database_principals database_role
                    ON database_role.principal_id = role_member.role_principal_id
                INNER JOIN sys.database_principals member_principal
                    ON member_principal.principal_id = role_member.member_principal_id
                WHERE database_role.name = N'dms_binding_gate'
                ORDER BY member_principal.name;
                """;

            using var reader = command.ExecuteReader();
            List<string> members = [];
            while (reader.Read())
            {
                members.Add(reader.GetString(0));
            }

            members.Should().Equal(_connectorPrincipalName);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT_BIG(*)
                FROM sys.database_permissions permission_info
                LEFT JOIN sys.objects object_info
                    ON object_info.object_id = permission_info.major_id
                LEFT JOIN sys.schemas schema_info
                    ON schema_info.schema_id = object_info.schema_id
                WHERE permission_info.grantee_principal_id = DATABASE_PRINCIPAL_ID(N'dms_binding_gate')
                AND NOT (
                    permission_info.class = 1
                    AND permission_info.permission_name = N'SELECT'
                    AND schema_info.name = N'cdc'
                );
                """;

            Convert.ToInt64(command.ExecuteScalar()).Should().Be(0);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    schema_info.name,
                    object_info.name,
                    permission_info.permission_name,
                    COALESCE(column_info.name, N'') AS column_name
                FROM sys.database_permissions permission_info
                INNER JOIN sys.objects object_info
                    ON object_info.object_id = permission_info.major_id
                INNER JOIN sys.schemas schema_info
                    ON schema_info.schema_id = object_info.schema_id
                LEFT JOIN sys.columns column_info
                    ON column_info.object_id = permission_info.major_id
                    AND column_info.column_id = permission_info.minor_id
                WHERE permission_info.grantee_principal_id = DATABASE_PRINCIPAL_ID(@connector_principal)
                AND permission_info.state IN (N'G', N'W')
                AND permission_info.class = 1
                ORDER BY schema_info.name, object_info.name, permission_info.permission_name, column_info.name;
                """;
            command.Parameters.AddWithValue("connector_principal", _connectorPrincipalName);

            using var reader = command.ExecuteReader();
            List<PermissionRow> permissions = [];
            while (reader.Read())
            {
                permissions.Add(
                    new PermissionRow(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3)
                    )
                );
            }

            permissions
                .Should()
                .ContainSingle(permission =>
                    permission.ObjectName == "Document"
                    && permission.PermissionName == "SELECT"
                    && permission.ColumnName == ""
                );
            permissions
                .Should()
                .ContainSingle(permission =>
                    permission.ObjectName == "DocumentCache"
                    && permission.PermissionName == "SELECT"
                    && permission.ColumnName == ""
                );
            permissions
                .Should()
                .ContainSingle(permission =>
                    permission.ObjectName == "CdcHeartbeat"
                    && permission.PermissionName == "SELECT"
                    && permission.ColumnName == ""
                );
            permissions
                .Where(permission =>
                    permission.ObjectName == "CdcHeartbeat"
                    && permission.PermissionName == "UPDATE"
                    && permission.ColumnName != ""
                )
                .Select(permission => permission.ColumnName)
                .Should()
                .BeEquivalentTo("HeartbeatSequence", "HeartbeatAt");
            permissions
                .Should()
                .NotContain(permission =>
                    permission.ObjectName == "DocumentProjectionWork"
                    || (
                        (permission.ObjectName == "Document" || permission.ObjectName == "DocumentCache")
                        && permission.PermissionName != "SELECT"
                    )
                    || (
                        permission.ObjectName == "CdcHeartbeat"
                        && permission.PermissionName == "UPDATE"
                        && permission.ColumnName == "HeartbeatId"
                    )
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

    private static void AssertCaptureInstances(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                capture_info.capture_instance,
                source_schema.name AS source_schema,
                source_table.name AS source_name,
                capture_info.role_name,
                capture_info.supports_net_changes,
                COALESCE(capture_info.index_name, N'') AS index_name,
                COALESCE(capture_info.filegroup_name, N'') AS filegroup_name,
                capture_info.partition_switch,
                captured_column.column_name,
                captured_column.column_ordinal
            FROM cdc.change_tables capture_info
            INNER JOIN sys.tables source_table
                ON source_table.object_id = capture_info.source_object_id
            INNER JOIN sys.schemas source_schema
                ON source_schema.schema_id = source_table.schema_id
            INNER JOIN cdc.captured_columns captured_column
                ON captured_column.object_id = capture_info.object_id
            WHERE source_schema.name = N'dms'
            ORDER BY capture_info.capture_instance, captured_column.column_ordinal;
            """;

        using var reader = command.ExecuteReader();
        List<CaptureColumn> rows = [];
        while (reader.Read())
        {
            rows.Add(
                new CaptureColumn(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetBoolean(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetBoolean(7),
                    reader.GetString(8),
                    reader.GetInt32(9)
                )
            );
        }

        var captures = rows.GroupBy(row => row.CaptureInstance).ToDictionary(group => group.Key);
        captures
            .Keys.Should()
            .BeEquivalentTo(
                "dms_binding_document_cache",
                "dms_binding_document",
                "dms_binding_cdc_heartbeat"
            );
        rows.Select(row => row.SourceName).Should().NotContain("DocumentProjectionWork");

        AssertCapture(
            captures["dms_binding_document_cache"],
            sourceName: "DocumentCache",
            expectedColumns:
            [
                "DocumentId",
                "DocumentUuid",
                "ProjectName",
                "ResourceName",
                "ResourceVersion",
                "ContentVersion",
                "StreamEtag",
                "LastModifiedAt",
                "DocumentJson",
                "ComputedAt",
            ]
        );
        AssertCapture(
            captures["dms_binding_document"],
            sourceName: "Document",
            expectedColumns:
            [
                "DocumentId",
                "DocumentUuid",
                "ResourceKeyId",
                "CreatedByOwnershipTokenId",
                "ContentVersion",
                "IdentityVersion",
                "ContentLastModifiedAt",
                "IdentityLastModifiedAt",
                "CreatedAt",
            ]
        );
        AssertCapture(
            captures["dms_binding_cdc_heartbeat"],
            sourceName: "CdcHeartbeat",
            expectedColumns: ["HeartbeatId", "HeartbeatSequence", "HeartbeatAt"]
        );
    }

    private static void AssertCapture(
        IEnumerable<CaptureColumn> captureRows,
        string sourceName,
        IReadOnlyList<string> expectedColumns
    )
    {
        var rows = captureRows.OrderBy(row => row.ColumnOrdinal).ToArray();
        var first = rows[0];

        first.SourceSchema.Should().Be("dms");
        first.SourceName.Should().Be(sourceName);
        first.RoleName.Should().Be("dms_binding_gate");
        first.SupportsNetChanges.Should().BeFalse();
        first.FilegroupName.Should().BeEmpty();
        rows.Select(row => row.ColumnName).Should().Equal(expectedColumns);
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

    private static long InsertDocumentBeforeProviderCapture(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @Inserted TABLE ([DocumentId] bigint NOT NULL);

            INSERT INTO [dms].[Document] ([DocumentUuid], [ResourceKeyId])
            OUTPUT INSERTED.[DocumentId] INTO @Inserted ([DocumentId])
            VALUES (@documentUuid, 1);

            SELECT [DocumentId]
            FROM @Inserted;
            """;
        command.Parameters.AddWithValue("documentUuid", Guid.NewGuid());

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void ExecuteProjectionWorkDml(SqlConnection connection, long documentId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM [dms].[DocumentProjectionWork]
            WHERE [DocumentId] = @documentId;

            INSERT INTO [dms].[DocumentProjectionWork] (
                [DocumentId],
                [RequiredContentVersion],
                [FirstEnqueuedAt],
                [LastEnqueuedAt]
            )
            VALUES (@documentId, 1, SYSUTCDATETIME(), SYSUTCDATETIME());

            UPDATE [dms].[DocumentProjectionWork]
            SET [RequiredContentVersion] = [RequiredContentVersion] + 1,
                [LastEnqueuedAt] = SYSUTCDATETIME()
            WHERE [DocumentId] = @documentId;

            DELETE FROM [dms].[DocumentProjectionWork]
            WHERE [DocumentId] = @documentId;
            """;
        command.Parameters.AddWithValue("documentId", documentId);
        command.ExecuteNonQuery();
    }

    private static void AssertNoProjectionWorkCapture(SqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM cdc.change_tables capture_info
            INNER JOIN sys.tables source_table
                ON source_table.object_id = capture_info.source_object_id
            INNER JOIN sys.schemas source_schema
                ON source_schema.schema_id = source_table.schema_id
            WHERE source_schema.name = N'dms'
            AND source_table.name = N'DocumentProjectionWork';
            """;

        Convert
            .ToInt64(command.ExecuteScalar())
            .Should()
            .Be(0, "DocumentProjectionWork must not have a CDC capture instance or change table");
    }

    private static string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    private static string DescribeDiagnostics(IReadOnlyList<CdcProviderDiagnostic> diagnostics) =>
        string.Join(
            "; ",
            diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}:{diagnostic.ArtifactKind}:{diagnostic.SafeName.Value}:{diagnostic.ExpectedValue}->{diagnostic.ObservedValue}:{diagnostic.ProviderErrorClass}"
            )
        );

    private sealed record ProjectionPrerequisites(bool ReadCommittedSnapshotOn, int NestedTriggersValue);

    private sealed record HeartbeatColumn(string Name, string DataType, bool IsNullable, byte Scale);

    private sealed record HeartbeatSnapshot(short HeartbeatId, long HeartbeatSequence);

    private sealed record PermissionRow(
        string SchemaName,
        string ObjectName,
        string PermissionName,
        string ColumnName
    );

    private sealed record CaptureColumn(
        string CaptureInstance,
        string SourceSchema,
        string SourceName,
        string RoleName,
        bool SupportsNetChanges,
        string IndexName,
        string FilegroupName,
        bool PartitionSwitch,
        string ColumnName,
        int ColumnOrdinal
    );
}
