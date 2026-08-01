// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Integration.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Mssql.Tests.Integration;

[TestFixture]
[Category("DatabaseIntegration")]
[Category("MssqlIntegration")]
[Category("CdcProviderArtifacts")]
public class Given_MssqlCdcProviderArtifacts
{
    private const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/stable-key-extension-child-collections";
    private const string ConnectorPassword = "EdFi_Dms1!";
    private const string GatingRoleName = "dms_binding_gate";
    private const string DocumentCaptureInstanceName = "dms_binding_document";
    private const string DocumentCacheCaptureInstanceName = "dms_binding_document_cache";
    private const string HeartbeatCaptureInstanceName = "dms_binding_cdc_heartbeat";

    private MssqlGeneratedDdlFixture _fixture = null!;
    private MssqlGeneratedDdlTestDatabase _database = null!;
    private string _connectorPrincipalName = null!;

    [SetUp]
    public async Task SetUp()
    {
        MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally(
            "SQL Server CDC artifact integration tests require a MssqlAdmin connection string."
        );

        _fixture = MssqlGeneratedDdlFixtureLoader.LoadFromRepositoryRelativePath(FixtureRelativePath);
        _database = await MssqlGeneratedDdlTestDatabase.CreateProvisionedAsync(_fixture.GeneratedDdl);
        _connectorPrincipalName = $"cdc_connector_{Guid.NewGuid():N}";

        CreateConnectorLoginAndUser(_database.DatabaseName, _connectorPrincipalName);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }

        if (!string.IsNullOrWhiteSpace(_connectorPrincipalName))
        {
            DropConnectorLoginIfExists(_connectorPrincipalName);
        }
    }

    [Test]
    public async Task It_should_apply_MssqlCdcArtifacts_to_generated_ddl_database_only_after_opt_in()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var manifestOutputDirectory = Directory.CreateTempSubdirectory("mssql-cdc-artifacts-");
        var manifestPath = Path.Combine(manifestOutputDirectory.FullName, "cdc-provider.mssql.manifest.json");

        try
        {
            var effectiveSchemaHashBeforeSetup = await ReadEffectiveSchemaHashAsync(connection);

            await AssertOrdinaryProvisioningOmitsProviderArtifactsAsync(connection, manifestPath);

            var result = await RunSetupAsync(
                connection,
                new CdcProviderArtifactOutputRequest(
                    IncludeManifestPayload: true,
                    ManifestOutputDirectoryPath: manifestOutputDirectory.FullName
                )
            );

            result
                .Outcome.Should()
                .Be(CdcProviderSetupOutcome.CreatedOrMatched, DescribeDiagnostics(result.Diagnostics));
            result
                .Diagnostics.Should()
                .NotContain(diagnostic => diagnostic.Severity == CdcProviderDiagnosticSeverity.Error);
            (await ReadEffectiveSchemaHashAsync(connection)).Should().Be(effectiveSchemaHashBeforeSetup);

            await AssertHeartbeatTableAsync(connection);
            await ExecuteNonQueryAsync(connection, result.HeartbeatActionQuery!.Sql);
            (await ReadHeartbeatSnapshotAsync(connection)).Should().Be(new HeartbeatSnapshot(1, 1));
            (await IsDatabaseCdcEnabledAsync(connection)).Should().BeTrue();
            await AssertCaptureInstancesAsync(connection);
            AssertProviderResult(result, await ReadDataStoreIdentityAsync(connection));
            await AssertManifestAsync(result, manifestPath);
        }
        finally
        {
            Directory.Delete(manifestOutputDirectory.FullName, recursive: true);
        }
    }

    [Test]
    public async Task It_should_fail_closed_when_an_extra_dms_schema_capture_instance_exists()
    {
        await using var connection = new SqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        var manifestOutputDirectory = Directory.CreateTempSubdirectory("mssql-cdc-extra-capture-");

        try
        {
            var initialResult = await RunSetupAsync(connection, new CdcProviderArtifactOutputRequest(false));
            initialResult
                .Outcome.Should()
                .Be(CdcProviderSetupOutcome.CreatedOrMatched, DescribeDiagnostics(initialResult.Diagnostics));

            await EnableUnexpectedDescriptorCaptureAsync(connection);

            var validationResult = await RunSetupAsync(
                connection,
                new CdcProviderArtifactOutputRequest(
                    IncludeManifestPayload: true,
                    ManifestOutputDirectoryPath: manifestOutputDirectory.FullName
                ),
                CdcProviderSetupMode.ValidateOnly
            );

            validationResult
                .Outcome.Should()
                .Be(CdcProviderSetupOutcome.Failed, DescribeDiagnostics(validationResult.Diagnostics));
            validationResult
                .Diagnostics.Should()
                .ContainSingle(diagnostic =>
                    diagnostic.Code == "CDC_SQLSERVER_UNEXPECTED_DMS_CAPTURE_INSTANCE"
                    && diagnostic.Category == CdcProviderDiagnosticCategory.ValidationMismatch
                    && diagnostic.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                    && diagnostic.SafeName.Value == "dms_unexpected_descriptor"
                    && diagnostic.ObservedValue == "dms.Descriptor_capture_dms_unexpected_descriptor"
                );
            validationResult
                .ArtifactInventory.Should()
                .Contain(observation =>
                    observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                    && observation.SafeArtifactName.Value == "dms_unexpected_descriptor"
                    && observation.State == CdcProviderArtifactState.Mismatched
                    && observation.SafeObservedValues["source_object"] == "dms.Descriptor"
                    && observation.SafeObservedValues["role_name"] == "other_cdc_gate"
                );
            validationResult
                .ProviderHistoryObservations.Should()
                .Contain(observation =>
                    observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                    && observation.SafeArtifactName.Value == "dms_unexpected_descriptor"
                    && observation.SafeObservedValues["source_object"] == "dms.Descriptor"
                    && observation.Classification == CdcProviderRetryContinuityClassification.FailClosed
                );
            validationResult.ManifestPayload!.Json.Should().Contain("dms_unexpected_descriptor");
            validationResult.ManifestPayload.Json.Should().Contain("dms.Descriptor");
            validationResult.ManifestPayload.Json.Should().Contain("other_cdc_gate");
            validationResult.ManifestPayload.Json.Should().NotContain(_database.ConnectionString);
        }
        finally
        {
            Directory.Delete(manifestOutputDirectory.FullName, recursive: true);
        }
    }

    private async Task<CdcProviderSetupResult> RunSetupAsync(
        SqlConnection connection,
        CdcProviderArtifactOutputRequest artifactOutput,
        CdcProviderSetupMode mode = CdcProviderSetupMode.InitialCreateOrExactMatch
    )
    {
        var service = new CdcProviderSetupService([new CdcSqlServerHeartbeatDatabaseProvider()]);
        var executor = new DbConnectionCdcProviderDatabaseExecutor(connection);

        return await service.SetupAsync(
            new CdcProviderSetupRequest(
                provider: CdcProvider.SqlServer,
                mode: mode,
                boundPhysicalSourceFingerprint: CdcSourceFingerprintMetadata.Compute(
                    CdcProvider.SqlServer,
                    await ReadDataStoreIdentityAsync(connection)
                ),
                setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("sa")),
                connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName(_connectorPrincipalName)),
                artifactNames: CdcProviderArtifactNames.ForSqlServer(
                    new CdcSafeName(GatingRoleName),
                    new Dictionary<CdcSourceTableKind, CdcSafeName>
                    {
                        [CdcSourceTableKind.Document] = new(DocumentCaptureInstanceName),
                        [CdcSourceTableKind.DocumentCache] = new(DocumentCacheCaptureInstanceName),
                        [CdcSourceTableKind.CdcHeartbeat] = new(HeartbeatCaptureInstanceName),
                    }
                ),
                artifactOutput: artifactOutput,
                expectedSourceInventory: CdcSourceInventoryBuilder.BuildExpectedSourceInventory(
                    SqlDialectFactory.Create(SqlDialect.Mssql)
                ),
                databaseExecutor: executor
            )
        );
    }

    private static async Task EnableUnexpectedDescriptorCaptureAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF DATABASE_PRINCIPAL_ID(N'other_cdc_gate') IS NULL
            BEGIN
                CREATE ROLE [other_cdc_gate];
            END;

            EXEC sys.sp_cdc_enable_table
                @source_schema = N'dms',
                @source_name = N'Descriptor',
                @capture_instance = N'dms_unexpected_descriptor',
                @supports_net_changes = 0,
                @role_name = N'other_cdc_gate',
                @index_name = NULL,
                @captured_column_list = NULL,
                @filegroup_name = NULL,
                @allow_partition_switch = 0;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task AssertOrdinaryProvisioningOmitsProviderArtifactsAsync(
        SqlConnection connection,
        string manifestPath
    )
    {
        (await IsDatabaseCdcEnabledAsync(connection))
            .Should()
            .BeFalse("ordinary provisioning must not enable CDC");
        (await TableExistsAsync(connection, "CdcHeartbeat"))
            .Should()
            .BeFalse("ordinary provisioning must not create CDC heartbeat");
        (await CaptureInstanceCountAsync(connection))
            .Should()
            .Be(0, "ordinary provisioning must not create CDC capture instances");
        File.Exists(manifestPath)
            .Should()
            .BeFalse("ordinary provisioning must not emit CDC provider manifests");

        (await HasConnectorObjectPermissionAsync(connection, "Document", "SELECT")).Should().BeFalse();
        (await HasConnectorObjectPermissionAsync(connection, "DocumentCache", "SELECT")).Should().BeFalse();
        (await HasConnectorObjectPermissionAsync(connection, "DocumentProjectionWork", "SELECT"))
            .Should()
            .BeFalse();
        (await RoleExistsAsync(connection, GatingRoleName))
            .Should()
            .BeFalse("ordinary provisioning must not create the CDC gating role");
    }

    private void AssertProviderResult(CdcProviderSetupResult result, string expectedSourceIdentity)
    {
        var expectedSourceFingerprint = CdcSourceFingerprintMetadata.Compute(
            CdcProvider.SqlServer,
            expectedSourceIdentity
        );

        result.Provider.Should().Be(CdcProvider.SqlServer);
        result
            .HeartbeatActionQuery!.Sql.Should()
            .Be(
                "UPDATE [dms].[CdcHeartbeat] SET [HeartbeatSequence] = [HeartbeatSequence] + 1, [HeartbeatAt] = sysutcdatetime() WHERE [HeartbeatId] = 1"
            );
        result.ObservedSourceFingerprint.Should().Be(expectedSourceFingerprint);
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
            .And.OnlyContain(observation =>
                observation.State == CdcProviderArtifactState.Created
                && observation
                    .SafeObservedValues["retained_min_lsn"]
                    .StartsWith("0x", StringComparison.Ordinal)
                && IsSafeLsnObservation(observation.SafeObservedValues["retained_max_lsn"])
                && observation.SafeObservedValues["retained_lsn_gap_evaluation"]
                    == "not_evaluated_without_committed_offset"
            );
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.HeartbeatTable
                && observation.SafeArtifactName.Value == "dms.CdcHeartbeat"
                && observation.State == CdcProviderArtifactState.Created
            );
        result
            .GrantInventory.Should()
            .ContainSingle(grant =>
                grant.SafePrincipalName.Value == _connectorPrincipalName
                && grant.SafeObjectName.Value == $"role.{GatingRoleName}"
                && grant.Privileges.SequenceEqual(new[] { "MEMBER" })
            );
        result
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerGatingRole
                && observation.SafeArtifactName.Value == GatingRoleName
                && observation.State == CdcProviderArtifactState.Created
                && observation.SafeObservedValues["gating_role_exists"] == "True"
                && observation.SafeObservedValues["gating_role_is_normal_role"] == "True"
                && observation.SafeObservedValues["gating_role_direct_members"] == _connectorPrincipalName
                && observation.SafeObservedValues["gating_role_parent_roles"] == "none"
                && observation.SafeObservedValues["gating_role_owned_objects"] == "none"
                && observation.SafeObservedValues["gating_role_explicit_permissions"] == "none"
                && observation.SafeObservedValues["expected_capture_instances_using_role"] == "3"
                && observation.SafeObservedValues["unexpected_capture_instances_using_role"] == "none"
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
            .ArtifactInventory.Should()
            .ContainSingle(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.ProviderHistory
                && observation.SafeArtifactName.Value == "sqlserver_database_cdc"
                && observation.State == CdcProviderArtifactState.Created
                && observation.SafeObservedValues["database_cdc_enabled"] == "True"
                && observation.SafeObservedValues["capture_instance_count"] == "3"
                && observation.SafeObservedValues["retained_lsn_gap_evaluation"]
                    == "not_evaluated_without_committed_offset"
            );
        result
            .ProviderHistoryObservations.Should()
            .Contain(observation =>
                observation.ArtifactKind == CdcProviderArtifactKind.SqlServerCaptureInstance
                && observation.SafeArtifactName.Value == HeartbeatCaptureInstanceName
                && observation.SafeObservedValues["heartbeat_capture_visible"] == "True"
            );
    }

    private async Task AssertManifestAsync(CdcProviderSetupResult result, string manifestPath)
    {
        result.ManifestPayload.Should().NotBeNull();
        result.ManifestPayload!.FileName.Value.Should().Be("cdc-provider.mssql.manifest.json");
        result.ManifestPayload.Json.Should().Be(await File.ReadAllTextAsync(manifestPath));
        result.ManifestPayload.Json.Should().Contain("\"provider\": \"mssql\"");
        result.ManifestPayload.Json.Should().Contain("\"artifact_kind\": \"sqlserver_gating_role\"");
        result.ManifestPayload.Json.Should().Contain($"\"artifact_name\": \"{GatingRoleName}\"");
        result.ManifestPayload.Json.Should().Contain("\"object_name\": \"role.dms_binding_gate\"");
        result.ManifestPayload.Json.Should().Contain("\"artifact_name\": \"dms_binding_document\"");
        result.ManifestPayload.Json.Should().Contain("\"artifact_name\": \"dms_binding_document_cache\"");
        result.ManifestPayload.Json.Should().Contain("\"artifact_name\": \"dms_binding_cdc_heartbeat\"");
        result.ManifestPayload.Json.Should().NotContain(ConnectorPassword);
        result.ManifestPayload.Json.Should().NotContain(_database.ConnectionString);
        result.ManifestPayload.Json.Should().NotContain("EffectiveSchemaHash");
        result.ManifestPayload.Json.Should().NotContain("ResourceKeySeedHash");
        result.ManifestPayload.Json.Should().NotContain("RelationalMappingVersion");
        result.ManifestPayload.Json.Should().NotContain("DocumentProjectionWork");

        using var manifestDocument = JsonDocument.Parse(result.ManifestPayload.Json);
        manifestDocument
            .RootElement.GetProperty("provider_artifacts")
            .EnumerateArray()
            .Should()
            .ContainSingle(artifact =>
                artifact.GetProperty("artifact_kind").GetString() == "provider_history"
                && artifact.GetProperty("artifact_name").GetString() == "sqlserver_database_cdc"
                && artifact.GetProperty("state").GetString() == "created"
                && artifact.GetProperty("observed_values").GetProperty("capture_instance_count").GetString()
                    == "3"
                && artifact.GetProperty("observed_values").GetProperty("database_cdc_enabled").GetString()
                    == "True"
            );
        manifestDocument
            .RootElement.GetProperty("provider_artifacts")
            .EnumerateArray()
            .Where(artifact =>
                artifact.GetProperty("artifact_kind").GetString() == "sqlserver_capture_instance"
            )
            .Should()
            .HaveCount(3)
            .And.OnlyContain(artifact =>
                artifact
                    .GetProperty("observed_values")
                    .GetProperty("retained_min_lsn")
                    .GetString()!
                    .StartsWith("0x", StringComparison.Ordinal)
                && IsSafeLsnObservation(
                    artifact.GetProperty("observed_values").GetProperty("retained_max_lsn").GetString() ?? ""
                )
            );
    }

    private static async Task<string> ReadDataStoreIdentityAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONVERT(nvarchar(36), [SourceIdentity])
            FROM [dms].[DataStoreIdentity]
            WHERE [DataStoreIdentitySingletonId] = 1;
            """;
        return (await command.ExecuteScalarAsync())!.ToString()!;
    }

    private static async Task<string> ReadEffectiveSchemaHashAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [EffectiveSchemaHash]
            FROM [dms].[EffectiveSchema];
            """;
        return (await command.ExecuteScalarAsync())!.ToString()!;
    }

    private static async Task<bool> IsDatabaseCdcEnabledAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [is_cdc_enabled]
            FROM sys.databases
            WHERE [name] = DB_NAME();
            """;
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM information_schema.tables
            WHERE table_schema = 'dms'
            AND table_name = @table_name;
            """;
        command.Parameters.AddWithValue("table_name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<long> CaptureInstanceCountAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'cdc.change_tables', N'U') IS NULL
                SELECT CONVERT(bigint, 0);
            ELSE
                SELECT COUNT_BIG(*) FROM cdc.change_tables;
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<bool> HasConnectorObjectPermissionAsync(
        SqlConnection connection,
        string objectName,
        string permissionName
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM sys.database_permissions permission_info
            INNER JOIN sys.objects object_info
                ON object_info.object_id = permission_info.major_id
            INNER JOIN sys.schemas schema_info
                ON schema_info.schema_id = object_info.schema_id
            WHERE permission_info.grantee_principal_id = DATABASE_PRINCIPAL_ID(@connector_principal)
            AND schema_info.name = N'dms'
            AND object_info.name = @object_name
            AND permission_info.permission_name = @permission_name
            AND permission_info.state IN (N'G', N'W');
            """;
        command.Parameters.AddWithValue("connector_principal", _connectorPrincipalName);
        command.Parameters.AddWithValue("object_name", objectName);
        command.Parameters.AddWithValue("permission_name", permissionName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> RoleExistsAsync(SqlConnection connection, string roleName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT_BIG(*)
            FROM sys.database_principals
            WHERE [type] = N'R'
            AND [name] = @role_name;
            """;
        command.Parameters.AddWithValue("role_name", roleName);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task AssertHeartbeatTableAsync(SqlConnection connection)
    {
        await using (var command = connection.CreateCommand())
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

            await using var reader = await command.ExecuteReaderAsync();
            List<HeartbeatColumn> columns = [];
            while (await reader.ReadAsync())
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

        (await ReadHeartbeatSnapshotAsync(connection)).Should().Be(new HeartbeatSnapshot(1, 0));
    }

    private static async Task<HeartbeatSnapshot> ReadHeartbeatSnapshotAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT [HeartbeatId], [HeartbeatSequence]
            FROM [dms].[CdcHeartbeat];
            """;

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var snapshot = new HeartbeatSnapshot(reader.GetInt16(0), reader.GetInt64(1));
        (await reader.ReadAsync()).Should().BeFalse();
        return snapshot;
    }

    private static async Task AssertCaptureInstancesAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
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

        await using var reader = await command.ExecuteReaderAsync();
        List<CaptureColumn> rows = [];
        while (await reader.ReadAsync())
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
                DocumentCacheCaptureInstanceName,
                DocumentCaptureInstanceName,
                HeartbeatCaptureInstanceName
            );
        rows.Select(row => row.SourceName).Should().NotContain("DocumentProjectionWork");

        AssertCapture(
            captures[DocumentCacheCaptureInstanceName],
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
            captures[DocumentCaptureInstanceName],
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
            captures[HeartbeatCaptureInstanceName],
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
        first.RoleName.Should().Be(GatingRoleName);
        first.SupportsNetChanges.Should().BeFalse();
        first.FilegroupName.Should().BeEmpty();
        rows.Select(row => row.ColumnName).Should().Equal(expectedColumns);
    }

    private static async Task ExecuteNonQueryAsync(SqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static void CreateConnectorLoginAndUser(string databaseName, string connectorPrincipalName)
    {
        using var connection = new SqlConnection(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        var quotedDatabase = QuoteIdentifier(databaseName);
        var quotedPrincipal = QuoteIdentifier(connectorPrincipalName);
        command.CommandText = $"""
            IF SUSER_ID(N'{EscapeSqlLiteral(connectorPrincipalName)}') IS NULL
            BEGIN
                CREATE LOGIN {quotedPrincipal} WITH PASSWORD = '{ConnectorPassword}', CHECK_POLICY = OFF;
            END;

            USE {quotedDatabase};

            IF USER_ID(N'{EscapeSqlLiteral(connectorPrincipalName)}') IS NULL
            BEGIN
                CREATE USER {quotedPrincipal} FOR LOGIN {quotedPrincipal};
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static void DropConnectorLoginIfExists(string connectorPrincipalName)
    {
        SqlConnection.ClearAllPools();

        using var connection = new SqlConnection(BaselineDatabaseConfiguration.MssqlAdminConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF SUSER_ID(N'{EscapeSqlLiteral(connectorPrincipalName)}') IS NOT NULL
            BEGIN
                DROP LOGIN {QuoteIdentifier(connectorPrincipalName)};
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static string QuoteIdentifier(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    private static string EscapeSqlLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static bool IsSafeLsnObservation(string value) =>
        value == "none" || value.StartsWith("0x", StringComparison.Ordinal);

    private static string DescribeDiagnostics(IReadOnlyList<CdcProviderDiagnostic> diagnostics) =>
        string.Join(
            "; ",
            diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}:{diagnostic.ArtifactKind}:{diagnostic.SafeName.Value}:{diagnostic.ExpectedValue}->{diagnostic.ObservedValue}:{diagnostic.ProviderErrorClass}"
            )
        );

    private sealed record HeartbeatColumn(string Name, string DataType, bool IsNullable, byte Scale);

    private sealed record HeartbeatSnapshot(short HeartbeatId, long HeartbeatSequence);

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
