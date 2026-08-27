// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Parallelizable]
public sealed class Given_PostgresqlProviderSetupToConnectorTemplateHandoff
{
    private const long BindingGeneration = 11;
    private const string SourceIdentity = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";
    private const string DeploymentKey = "dms";
    private const string InstanceKey = "handoff";
    private const string TopicPrefix = "edfi.handoff.documents";
    private static readonly CoreCdc.CdcArtifactInventory BindingArtifacts = BuildCoreArtifactInventory();
    private static readonly string ConnectorName = BindingArtifacts.ConnectorName;
    private static readonly string PublicationName = BindingArtifacts.PostgresqlPublicationName!;
    private static readonly string ReplicationSlotName = BindingArtifacts.PostgresqlLogicalSlotName!;

    [Test]
    public async Task It_renders_and_validates_from_the_real_provider_setup_result()
    {
        CdcProviderSetupRequest providerSetupRequest = BuildProviderSetupRequest();
        CdcProviderSetupResult providerSetupResult = await new CdcProviderSetupService([
            new CdcPostgresqlHeartbeatPublicationProvider(),
        ]).SetupAsync(providerSetupRequest);
        CdcConnectorTemplateRequest templateRequest = BuildTemplateRequest(
            providerSetupRequest,
            providerSetupResult
        );

        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddCdcConnectorTemplates()
            .BuildServiceProvider();
        ICdcConnectorTemplateService templateService =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        CdcConnectorTemplateResult rendered = templateService.Render(templateRequest);
        CdcConnectorTemplateResult preflight = templateService.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                templateRequest,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(BindingGeneration, providerSetupResult)
            )
        );
        CdcConnectorTemplateResult liveReadBack = templateService.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                templateRequest,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(BindingGeneration, providerSetupResult),
                new CdcConnectorTemplateSourcePartitionEvidence(
                    new Dictionary<string, string> { ["server"] = ConnectorName }
                )
            )
        );

        using var _ = new AssertionScope();
        providerSetupResult.Mode.Should().Be(CdcProviderSetupMode.ValidateOnly);
        providerSetupResult.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
        providerSetupResult.Diagnostics.Should().BeEmpty();
        providerSetupResult
            .ArtifactInventory.Should()
            .ContainSingle(artifact =>
                artifact.ArtifactKind == CdcProviderArtifactKind.PostgresqlPublication
                && artifact.SafeArtifactName.Value == PublicationName
            );
        providerSetupResult
            .ArtifactInventory.Should()
            .ContainSingle(artifact =>
                artifact.ArtifactKind == CdcProviderArtifactKind.PostgresqlReplicationSlot
                && artifact.SafeArtifactName.Value == ReplicationSlotName
            );
        providerSetupResult
            .SourceTableInventory.Should()
            .BeEquivalentTo(providerSetupRequest.ExpectedSourceInventory);
        providerSetupResult
            .ExpectedMessageKeyColumns.Select(columns => (columns.TableKind, ColumnNames(columns.KeyColumns)))
            .Should()
            .BeEquivalentTo([
                (CdcSourceTableKind.DocumentCache, "DocumentUuid"),
                (CdcSourceTableKind.Document, "DocumentUuid"),
            ]);
        providerSetupResult.HeartbeatActionQuery.Should().NotBeNull();

        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        rendered.Diagnostics.Should().BeEmpty();
        rendered.Config["publication.name"].Should().Be(PublicationName);
        rendered.Config["slot.name"].Should().Be(ReplicationSlotName);
        rendered
            .Config["table.include.list"]
            .Should()
            .Be(@"dms\.DocumentCache,dms\.Document,dms\.CdcHeartbeat");
        rendered
            .Config["message.key.columns"]
            .Should()
            .Be(@"dms\.DocumentCache:DocumentUuid;dms\.Document:DocumentUuid");
        rendered.Config["heartbeat.action.query"].Should().Be(providerSetupResult.HeartbeatActionQuery!.Sql);
        preflight.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflight.Diagnostics.Should().BeEmpty();
        liveReadBack.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        liveReadBack.Diagnostics.Should().BeEmpty();
    }

    private static CdcProviderSetupRequest BuildProviderSetupRequest()
    {
        ISqlDialect dialect = SqlDialectFactory.Create(SqlDialect.Pgsql);
        IReadOnlyList<CdcSourceTableInventory> sourceInventory = new CoreDdlEmitter(dialect)
            .EmitWithMetadata()
            .CdcSourceInventory;
        CdcSourceFingerprint sourceFingerprint = CdcSourceFingerprintMetadata.Compute(
            CdcProvider.Postgresql,
            SourceIdentity
        );
        CdcProviderArtifactNames artifactNames = CdcProviderArtifactNames.ForPostgresql(
            new CdcSafeName(PublicationName),
            new CdcSafeName(ReplicationSlotName)
        );

        return new CdcProviderSetupRequest(
            provider: CdcProvider.Postgresql,
            mode: CdcProviderSetupMode.ValidateOnly,
            boundPhysicalSourceFingerprint: sourceFingerprint,
            setupPrincipal: new CdcSetupPrincipalContext(new CdcSafeName("handoff_setup_principal")),
            connectorPrincipal: new CdcConnectorPrincipal(new CdcSafeName("handoff_connector_principal")),
            artifactNames: artifactNames,
            artifactOutput: new CdcProviderArtifactOutputRequest(IncludeManifestPayload: false),
            expectedSourceInventory: sourceInventory,
            dmsManagedTableInventory: BuildDmsManagedTableInventory(dialect, sourceInventory),
            databaseExecutor: new HandoffPostgresqlCdcExecutor(sourceInventory)
        );
    }

    private static CdcConnectorTemplateRequest BuildTemplateRequest(
        CdcProviderSetupRequest providerSetupRequest,
        CdcProviderSetupResult providerSetupResult
    )
    {
        var binding = new CoreCdc.CdcBinding(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            DeploymentKey,
            CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
            "1",
            InstanceKey,
            BindingGeneration,
            CoreCdc.CdcProvider.Postgresql,
            providerSetupRequest.BoundPhysicalSourceFingerprint.Value,
            BindingArtifacts.ConnectorName,
            BindingArtifacts.TopicName,
            PartitionCount: 1,
            CoreCdc.CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CoreCdc.CdcJsonContract.CurrentContractVersion
        );

        return new CdcConnectorTemplateRequest(
            binding,
            new CdcConnectorProviderSetupEvidence(BindingGeneration, providerSetupResult),
            new CdcConnectorTemplateDeploymentPolicy(
                "broker:9092",
                maxRecordBytes: 67_108_864,
                heartbeatInterval: TimeSpan.FromSeconds(5)
            ),
            new CdcProviderConnectionProperties(
                CdcProvider.Postgresql,
                new Dictionary<string, string>
                {
                    ["database.hostname"] = "postgresql.internal",
                    ["database.port"] = "5432",
                    ["database.user"] = "connector_user",
                    ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                    ["database.dbname"] = "edfi_datastore",
                }
            ),
            CdcKafkaClientSecurityProperties.Empty
        );
    }

    private static CoreCdc.CdcArtifactInventory BuildCoreArtifactInventory()
    {
        CoreCdc.CdcArtifactNameResult result = CoreCdc.CdcArtifactNameGenerator.Render(
            new CoreCdc.CdcArtifactNameInput(
                DeploymentKey,
                TopicPrefix,
                InstanceKey,
                BindingGeneration,
                CoreCdc.CdcProvider.Postgresql
            )
        );

        return result.Inventory ?? throw new InvalidOperationException("Invalid handoff CDC artifact input.");
    }

    private static IReadOnlyList<CdcDmsManagedTableInventory> BuildDmsManagedTableInventory(
        ISqlDialect dialect,
        IReadOnlyList<CdcSourceTableInventory> sourceInventory
    )
    {
        var sourceTablesByName = sourceInventory.ToDictionary(table => table.TableName);
        DbTableName[] coreTables =
        [
            DmsTableNames.DataStoreIdentity,
            DmsTableNames.CdcHeartbeat,
            DmsTableNames.Descriptor,
            DmsTableNames.Document,
            DmsTableNames.DocumentCache,
            DmsTableNames.DocumentCacheState,
            DmsTableNames.DocumentProjectionWork,
            DmsTableNames.ReferentialIdentity,
            DmsTableNames.ResourceKey,
            DmsTableNames.SchemaComponent,
        ];

        return coreTables
            .Select(table => new CdcDmsManagedTableInventory(
                CdcDmsManagedTableKind.Core,
                table,
                sourceTablesByName.TryGetValue(table, out CdcSourceTableInventory? sourceTable)
                    ? sourceTable.EmittedQuotedTableName
                    : dialect.QualifyTable(table)
            ))
            .ToArray();
    }

    private static string ColumnNames(IReadOnlyList<DbColumnName> columns) =>
        string.Join(",", columns.Select(column => column.Value));

    private sealed class HandoffPostgresqlCdcExecutor(IReadOnlyList<CdcSourceTableInventory> sourceInventory)
        : ICdcProviderDatabaseExecutor
    {
        private const string CurrentDatabaseName = "dms_test";

        public Task ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Validate-only handoff setup should not execute SQL: {sql}");

        public Task<IReadOnlyList<IReadOnlyDictionary<string, string?>>> QueryAsync(
            string sql,
            CancellationToken cancellationToken
        )
        {
            IReadOnlyList<IReadOnlyDictionary<string, string?>> rows = sql switch
            {
                var text when text.Contains("cdc:postgresql:source-fingerprint", StringComparison.Ordinal) =>
                [
                    Row(("source_identity", SourceIdentity)),
                ],
                var text when text.Contains("cdc:postgresql:table-exists", StringComparison.Ordinal) =>
                [
                    Row(("table_exists", true.ToString())),
                ],
                var text when text.Contains("cdc:postgresql:heartbeat-shape", StringComparison.Ordinal) =>
                [
                    Row(
                        ("primary_key_matches", true.ToString()),
                        ("singleton_check_matches", true.ToString()),
                        ("sequence_check_matches", true.ToString())
                    ),
                ],
                var text when text.Contains("cdc:postgresql:heartbeat-singleton", StringComparison.Ordinal) =>

                    [
                        Row(
                            ("row_count", "1"),
                            ("singleton_row_count", "1"),
                            ("extra_row_count", "0"),
                            ("heartbeat_sequence", "0")
                        ),
                    ],
                var text when text.Contains("cdc:postgresql:source-inventory", StringComparison.Ordinal) =>
                    SourceInventoryRows(),
                var text
                    when text.Contains(
                        "cdc:postgresql:document-replica-identity",
                        StringComparison.Ordinal
                    ) => [Row(("relreplident", "f"))],
                var text when text.Contains("cdc:postgresql:server-version", StringComparison.Ordinal) =>
                [
                    Row(("server_version_num", "160000")),
                ],
                var text
                    when text.Contains("cdc:postgresql:publication-properties", StringComparison.Ordinal) =>
                [
                    Row(
                        ("publishes_insert", true.ToString()),
                        ("publishes_update", true.ToString()),
                        ("publishes_delete", true.ToString()),
                        ("publishes_truncate", false.ToString()),
                        ("publishes_all_tables", false.ToString()),
                        ("publish_via_partition_root", false.ToString())
                    ),
                ],
                var text when text.Contains("cdc:postgresql:publication-tables", StringComparison.Ordinal) =>
                    PublicationTableRows(),
                var text when text.Contains("cdc:postgresql:publication-schemas", StringComparison.Ordinal) =>
                    [],
                var text when text.Contains("cdc:postgresql:replication-slot", StringComparison.Ordinal) =>
                [
                    Row(
                        ("slot_name", ReplicationSlotName),
                        ("plugin", "pgoutput"),
                        ("slot_type", "logical"),
                        ("database", CurrentDatabaseName),
                        ("expected_database", CurrentDatabaseName),
                        ("temporary", false.ToString()),
                        ("active", false.ToString()),
                        ("two_phase", false.ToString()),
                        ("restart_lsn", "0/16B6C50"),
                        ("confirmed_flush_lsn", "0/16B6C50"),
                        ("wal_status", "reserved"),
                        ("invalidation_reason", "")
                    ),
                ],
                var text
                    when text.Contains(
                        "cdc:postgresql:connector-principal-access",
                        StringComparison.Ordinal
                    ) =>
                [
                    Row(
                        ("role_exists", true.ToString()),
                        ("can_login", true.ToString()),
                        ("can_replicate", true.ToString()),
                        ("disallowed_role_attributes", ""),
                        ("ownership", ""),
                        ("database_connect", true.ToString()),
                        ("schema_usage", true.ToString()),
                        ("document_select", true.ToString()),
                        ("document_cache_select", true.ToString()),
                        ("heartbeat_select", true.ToString()),
                        ("heartbeat_sequence_update", true.ToString()),
                        ("heartbeat_at_update", true.ToString()),
                        ("heartbeat_id_update", false.ToString()),
                        ("heartbeat_forbidden_table_privileges", ""),
                        ("heartbeat_unexpected_update_columns", ""),
                        ("document_write_privileges", ""),
                        ("document_cache_write_privileges", ""),
                        ("work_table_privileges", ""),
                        ("extra_dms_select_tables", ""),
                        ("extra_dms_forbidden_privileges", "")
                    ),
                ],
                _ => throw new InvalidOperationException($"Unexpected PostgreSQL CDC query: {sql}"),
            };

            return Task.FromResult(rows);
        }

        private IReadOnlyList<IReadOnlyDictionary<string, string?>> SourceInventoryRows() =>
            sourceInventory
                .SelectMany(table =>
                    table.Columns.Select(column =>
                        Row(
                            ("table_schema", table.TableName.Schema.Value),
                            ("table_name", table.TableName.Name),
                            ("column_name", column.ColumnName.Value),
                            ("ordinal", column.Ordinal.ToString()),
                            ("provider_data_type", column.ProviderDataType),
                            ("is_nullable", column.IsNullable.ToString())
                        )
                    )
                )
                .ToArray();

        private IReadOnlyList<IReadOnlyDictionary<string, string?>> PublicationTableRows() =>
            sourceInventory
                .OrderBy(table => table.TableName.Schema.Value, StringComparer.Ordinal)
                .ThenBy(table => table.TableName.Name, StringComparer.Ordinal)
                .Select(table =>
                    Row(
                        ("schema_name", table.TableName.Schema.Value),
                        ("table_name", table.TableName.Name),
                        ("publishes_all_columns", true.ToString()),
                        ("row_filter_absent", true.ToString())
                    )
                )
                .ToArray();

        private static IReadOnlyDictionary<string, string?> Row(
            params (string Key, string? Value)[] values
        ) => values.ToDictionary(value => value.Key, value => value.Value);
    }
}
