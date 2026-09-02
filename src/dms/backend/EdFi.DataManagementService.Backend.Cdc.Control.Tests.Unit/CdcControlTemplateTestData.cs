// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Builds the rendered-connector inputs the control plane feeds to the connector-template service:
/// a valid binding, the provider setup evidence rendering consumed, and the fresh validate-only
/// evidence a live read-back is validated against.
/// </summary>
internal static class CdcControlTemplateTestData
{
    public const long BindingGeneration = 7;
    public const string DeploymentKey = "dms";
    public const string InstanceKey = "binding";
    public const string TopicPrefix = "edfi.documents";
    public const string HeartbeatSql = "select 1";
    public const string KafkaBootstrapServers = "broker-1:9092,broker-2:9092";
    public const int MaxRecordBytes = 67_108_864;

    /// <summary>
    /// The retained WAL range a PostgreSQL replication slot reports. The values are safe-encoded, which
    /// is how provider history evidence carries a WAL LSN.
    /// </summary>
    public const string PostgresqlRetainedRangeStart = "0_16B6C50";

    public const string PostgresqlRetainedRangeEnd = "0_16B6C60";

    /// <summary>
    /// The retained change range SQL Server capture instances report, as the 20 hex digits the provider
    /// encodes a 10-byte LSN with. The range brackets the connector's committed position, which is what
    /// makes the retained range cover it.
    /// </summary>
    public const string SqlServerRetainedRangeStart = "0x00000027000000000000";

    public const string SqlServerRetainedRangeEnd = "0x0000002700000c790000";

    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan SqlServerPollInterval = TimeSpan.FromSeconds(2);

    private const string SourceIdentity = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";

    /// <summary>
    /// The identity of an independently provisioned database: the source a generation being replaced
    /// was bound to, which is never the identity the replacing source carries.
    /// </summary>
    private const string ReplacedSourceIdentity = "0f8fad5b-d9cb-469f-a165-70867728950e";

    public static CdcSourceFingerprint SourceFingerprint(CdcProvider provider) =>
        CdcSourceFingerprintMetadata.Compute(provider, SourceIdentity);

    /// <summary>The fingerprint of the physical source a source replacement replaces.</summary>
    public static CdcSourceFingerprint ReplacedSourceFingerprint(CdcProvider provider) =>
        CdcSourceFingerprintMetadata.Compute(provider, ReplacedSourceIdentity);

    public static CoreCdc.CdcBinding BuildBinding(CdcProvider provider)
    {
        CoreCdc.CdcArtifactInventory inventory = BuildInventory(provider);

        return new CoreCdc.CdcBinding(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            DeploymentKey,
            CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
            "1",
            InstanceKey,
            BindingGeneration,
            ToCoreProvider(provider),
            SourceFingerprint(provider).Value,
            inventory.ConnectorName,
            inventory.TopicName,
            PartitionCount: 1,
            CoreCdc.CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
            CoreCdc.CdcJsonContract.CurrentContractVersion
        );
    }

    public static CoreCdc.CdcArtifactInventory BuildInventory(CdcProvider provider) =>
        CoreCdc
            .CdcArtifactNameGenerator.Render(
                new CoreCdc.CdcArtifactNameInput(
                    DeploymentKey,
                    TopicPrefix,
                    InstanceKey,
                    BindingGeneration,
                    ToCoreProvider(provider)
                )
            )
            .Inventory
        ?? throw new InvalidOperationException("Invalid test CDC binding artifact input.");

    public static CoreCdc.CdcTargetIdentity BuildTargetIdentity(CdcProvider provider) =>
        new(
            DeploymentKey,
            CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
            "1",
            InstanceKey,
            BindingGeneration,
            ToCoreProvider(provider)
        );

    public static CdcConnectorTemplateRequest BuildTemplateRequest(CdcProvider provider)
    {
        CoreCdc.CdcBinding binding = BuildBinding(provider);

        return new CdcConnectorTemplateRequest(
            binding,
            new CdcConnectorProviderSetupEvidence(
                BindingGeneration,
                BuildProviderSetupResult(
                    provider,
                    CdcProviderSetupMode.InitialCreateOrExactMatch,
                    CdcProviderSetupOutcome.CreatedOrMatched
                )
            ),
            new CdcConnectorTemplateDeploymentPolicy(
                KafkaBootstrapServers,
                maxRecordBytes: MaxRecordBytes,
                heartbeatInterval: HeartbeatInterval,
                sqlServerPollInterval: provider == CdcProvider.SqlServer ? SqlServerPollInterval : null
            ),
            new CdcProviderConnectionProperties(provider, BuildConnectionProperties(provider)),
            new CdcKafkaClientSecurityProperties(new Dictionary<string, string>())
        );
    }

    /// <summary>
    /// The validate-only evidence a live read-back is checked against. Live read-back validation accepts
    /// only this mode and outcome, and compares the source, message-key, and heartbeat evidence against
    /// what rendering consumed.
    /// </summary>
    public static CdcConnectorProviderSetupEvidence BuildFreshProviderSetupEvidence(
        CdcProvider provider,
        CdcProviderSetupMode mode = CdcProviderSetupMode.ValidateOnly,
        CdcProviderSetupOutcome outcome = CdcProviderSetupOutcome.ExactMatch,
        string heartbeatSql = HeartbeatSql
    ) => new(BindingGeneration, BuildProviderSetupResult(provider, mode, outcome, heartbeatSql));

    public static CdcConnectorTemplateSourcePartitionEvidence BuildSourcePartitionEvidence(
        CdcConnectorTemplateRequest request,
        string? server = null
    )
    {
        Dictionary<string, string> properties = new(StringComparer.Ordinal)
        {
            ["server"] = server ?? request.ConnectorName.Value,
        };

        if (request.Provider == CdcProvider.SqlServer)
        {
            properties["database"] = request.ProviderConnectionProperties.Properties["database.names"];
        }

        return new(properties);
    }

    public static IReadOnlyDictionary<string, string> BuildConnectionProperties(CdcProvider provider) =>
        provider == CdcProvider.Postgresql
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["database.hostname"] = "postgresql.internal",
                ["database.port"] = "5432",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.dbname"] = "edfi_datastore",
            }
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["database.hostname"] = "sqlserver.internal",
                ["database.port"] = "1433",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.names"] = "edfi_datastore",
            };

    private static CdcProviderSetupResult BuildProviderSetupResult(
        CdcProvider provider,
        CdcProviderSetupMode mode,
        CdcProviderSetupOutcome outcome,
        string heartbeatSql = HeartbeatSql
    ) =>
        new(
            Provider: provider,
            Mode: mode,
            Outcome: outcome,
            BoundPhysicalSourceFingerprint: SourceFingerprint(provider),
            ObservedSourceFingerprint: SourceFingerprint(provider),
            ArtifactInventory: BuildArtifactInventory(provider),
            GrantInventory: BuildGrantInventory(provider),
            SourceTableInventory: BuildSourceTableInventory(provider),
            ExpectedMessageKeyColumns:
            [
                new(CdcSourceTableKind.DocumentCache, [new DbColumnName("DocumentUuid")]),
                new(CdcSourceTableKind.Document, [new DbColumnName("DocumentUuid")]),
            ],
            HeartbeatActionQuery: new CdcHeartbeatActionQuery(heartbeatSql, "sha256-safe"),
            ProviderHistoryObservations: BuildProviderHistoryObservations(provider),
            ManifestPayload: null,
            Diagnostics: []
        );

    /// <summary>
    /// The heartbeat table the connector's own heartbeat action query writes to. It is part of every
    /// provider's artifact inventory, because heartbeat evidence is what proves the connector is reading
    /// a source that is still advancing.
    /// </summary>
    private static CdcProviderArtifactObservation BuildHeartbeatTableArtifact() =>
        new(
            CdcProviderArtifactKind.HeartbeatTable,
            new CdcSafeName("dms_cdcheartbeat"),
            CdcProviderArtifactState.Matched,
            new Dictionary<string, string>()
        );

    /// <summary>
    /// The privileges the setup pass granted the connector principal on the source it reads.
    /// </summary>
    private static IReadOnlyList<CdcGrantObservation> BuildGrantInventory(CdcProvider provider) =>
        [
            .. BuildSourceTableInventory(provider)
                .Select(sourceTable => new CdcGrantObservation(
                    CdcPrincipalKind.ConnectorPrincipal,
                    new CdcSafeName("connector_principal"),
                    CdcProviderArtifactKind.SourceTable,
                    new CdcSafeName(sourceTable.TableName.Name.ToLowerInvariant()),
                    ["SELECT"],
                    [.. sourceTable.Columns.Select(column => column.ColumnName)]
                )),
        ];

    private static IReadOnlyList<CdcProviderArtifactObservation> BuildArtifactInventory(CdcProvider provider)
    {
        CdcProviderArtifactNames names = BuildProviderArtifactNames(provider);

        if (provider == CdcProvider.Postgresql)
        {
            return
            [
                new(
                    CdcProviderArtifactKind.PostgresqlPublication,
                    names.Postgresql!.PublicationName,
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
                new(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    names.Postgresql.ReplicationSlotName,
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
                BuildHeartbeatTableArtifact(),
            ];
        }

        return
        [
            BuildHeartbeatTableArtifact(),
            new(
                CdcProviderArtifactKind.ProviderHistory,
                new CdcSafeName("sqlserver_snapshot_isolation"),
                CdcProviderArtifactState.Matched,
                new Dictionary<string, string>
                {
                    ["allow_snapshot_isolation"] = "True",
                    ["snapshot_isolation_state_desc"] = "ON",
                }
            ),
            new(
                CdcProviderArtifactKind.SqlServerGatingRole,
                names.SqlServer!.GatingRoleName,
                CdcProviderArtifactState.Matched,
                new Dictionary<string, string>()
            ),
            .. new[]
            {
                CdcSourceTableKind.DocumentCache,
                CdcSourceTableKind.Document,
                CdcSourceTableKind.CdcHeartbeat,
            }.Select(tableKind => BuildCaptureInstanceArtifact(names, tableKind)),
        ];
    }

    private static CdcProviderArtifactObservation BuildCaptureInstanceArtifact(
        CdcProviderArtifactNames names,
        CdcSourceTableKind tableKind
    )
    {
        CdcSafeName captureInstance = names.SqlServer!.CaptureInstanceNames[tableKind];

        return new(
            CdcProviderArtifactKind.SqlServerCaptureInstance,
            captureInstance,
            CdcProviderArtifactState.Matched,
            new Dictionary<string, string>
            {
                ["capture_instance"] = captureInstance.Value,
                ["source_table_kind"] = SourceTableKindToken(tableKind),
            }
        );
    }

    private static IReadOnlyList<CdcProviderHistoryObservation> BuildProviderHistoryObservations(
        CdcProvider provider
    )
    {
        if (provider == CdcProvider.Postgresql)
        {
            // The slot's retained WAL range is what source-history continuity is decided against, so a
            // PostgreSQL setup result that omitted it would carry no continuity evidence at all.
            return
            [
                new CdcProviderHistoryObservation(
                    CdcProviderArtifactKind.PostgresqlReplicationSlot,
                    BuildProviderArtifactNames(provider).Postgresql!.ReplicationSlotName,
                    new Dictionary<string, string>
                    {
                        ["restart_lsn"] = PostgresqlRetainedRangeStart,
                        ["confirmed_flush_lsn"] = PostgresqlRetainedRangeEnd,
                        ["wal_status"] = "reserved",
                    },
                    CdcProviderRetryContinuityClassification.None
                ),
            ];
        }

        CdcSqlServerProviderArtifactNames names = BuildProviderArtifactNames(provider).SqlServer!;

        // SQL Server continuity is decided from the database-level CDC job health plus the retained
        // change range every required capture instance reports, so a setup result that omitted either
        // would carry no continuity evidence at all.
        return
        [
            new CdcProviderHistoryObservation(
                CdcProviderArtifactKind.ProviderHistory,
                new CdcSafeName("sqlserver_database_cdc"),
                new Dictionary<string, string>
                {
                    ["database_cdc_enabled"] = "True",
                    ["capture_job_present"] = "True",
                    ["capture_job_name"] = "cdc.edfi_datastore_capture",
                    ["capture_job_enabled"] = "True",
                    ["capture_job_running"] = "True",
                    ["capture_job_last_run_status"] = "1",
                    ["cleanup_job_present"] = "True",
                    ["cleanup_job_name"] = "cdc.edfi_datastore_cleanup",
                    ["cleanup_job_enabled"] = "True",
                    ["cleanup_job_running"] = "True",
                    ["cleanup_job_last_run_status"] = "1",
                    ["retained_max_lsn"] = SqlServerRetainedRangeEnd,
                },
                CdcProviderRetryContinuityClassification.None
            ),
            .. new[]
            {
                CdcSourceTableKind.DocumentCache,
                CdcSourceTableKind.Document,
                CdcSourceTableKind.CdcHeartbeat,
            }.Select(tableKind => new CdcProviderHistoryObservation(
                CdcProviderArtifactKind.SqlServerCaptureInstance,
                names.CaptureInstanceNames[tableKind],
                new Dictionary<string, string>
                {
                    ["capture_instance"] = names.CaptureInstanceNames[tableKind].Value,
                    ["retained_min_lsn"] = SqlServerRetainedRangeStart,
                    ["retained_max_lsn"] = SqlServerRetainedRangeEnd,
                },
                CdcProviderRetryContinuityClassification.None
            )),
        ];
    }

    private static CdcProviderArtifactNames BuildProviderArtifactNames(CdcProvider provider)
    {
        CoreCdc.CdcArtifactInventory inventory = BuildInventory(provider);

        return provider == CdcProvider.Postgresql
            ? CdcProviderArtifactNames.ForPostgresql(
                new CdcSafeName(inventory.PostgresqlPublicationName!),
                new CdcSafeName(inventory.PostgresqlLogicalSlotName!)
            )
            : CdcProviderArtifactNames.ForSqlServer(
                new CdcSafeName(inventory.SqlServerCdcGatingRoleName!),
                new Dictionary<CdcSourceTableKind, CdcSafeName>
                {
                    [CdcSourceTableKind.DocumentCache] = new(
                        inventory.SqlServerCaptureInstanceDocumentCacheName!
                    ),
                    [CdcSourceTableKind.Document] = new(inventory.SqlServerCaptureInstanceDocumentName!),
                    [CdcSourceTableKind.CdcHeartbeat] = new(
                        inventory.SqlServerCaptureInstanceCdcHeartbeatName!
                    ),
                }
            );
    }

    public static IReadOnlyList<CdcSourceTableInventory> BuildSourceTableInventory(CdcProvider provider) =>
        [
            BuildSourceTable(provider, CdcSourceTableKind.DocumentCache, "DocumentCache", ["DocumentUuid"]),
            BuildSourceTable(provider, CdcSourceTableKind.Document, "Document", ["DocumentUuid"]),
            BuildSourceTable(
                provider,
                CdcSourceTableKind.CdcHeartbeat,
                "CdcHeartbeat",
                ["HeartbeatId", "HeartbeatSequence", "HeartbeatAt"]
            ),
        ];

    private static CdcSourceTableInventory BuildSourceTable(
        CdcProvider provider,
        CdcSourceTableKind tableKind,
        string tableName,
        IReadOnlyList<string> columnNames
    ) =>
        new(
            tableKind,
            new DbTableName(new DbSchemaName("dms"), tableName),
            provider == CdcProvider.Postgresql ? $"\"dms\".\"{tableName}\"" : $"[dms].[{tableName}]",
            [
                .. columnNames.Select(
                    (columnName, index) =>
                        new CdcSourceColumnInventory(
                            new DbColumnName(columnName),
                            provider == CdcProvider.Postgresql ? $"\"{columnName}\"" : $"[{columnName}]",
                            index + 1,
                            provider == CdcProvider.Postgresql ? "text" : "nvarchar(max)",
                            IsNullable: false
                        )
                ),
            ]
        );

    private static string SourceTableKindToken(CdcSourceTableKind tableKind) =>
        tableKind switch
        {
            CdcSourceTableKind.DocumentCache => "document_cache",
            CdcSourceTableKind.Document => "document",
            CdcSourceTableKind.CdcHeartbeat => "cdc_heartbeat",
            _ => throw new ArgumentOutOfRangeException(
                nameof(tableKind),
                tableKind,
                "Unsupported CDC source table kind."
            ),
        };

    private static CoreCdc.CdcProvider ToCoreProvider(CdcProvider provider) =>
        provider == CdcProvider.Postgresql ? CoreCdc.CdcProvider.Postgresql : CoreCdc.CdcProvider.SqlServer;
}
