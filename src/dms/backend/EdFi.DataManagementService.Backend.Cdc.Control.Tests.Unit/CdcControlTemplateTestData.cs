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

    private const string SourceIdentity = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";

    public static CdcSourceFingerprint SourceFingerprint(CdcProvider provider) =>
        CdcSourceFingerprintMetadata.Compute(provider, SourceIdentity);

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
                "broker-1:9092,broker-2:9092",
                maxRecordBytes: 67_108_864,
                heartbeatInterval: TimeSpan.FromSeconds(5),
                sqlServerPollInterval: provider == CdcProvider.SqlServer ? TimeSpan.FromSeconds(2) : null
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
            GrantInventory: [],
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
            ];
        }

        return
        [
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
        if (provider != CdcProvider.SqlServer)
        {
            return [];
        }

        CdcSqlServerProviderArtifactNames names = BuildProviderArtifactNames(provider).SqlServer!;

        return
        [
            .. new[] { CdcSourceTableKind.DocumentCache, CdcSourceTableKind.Document }.Select(
                tableKind => new CdcProviderHistoryObservation(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    names.CaptureInstanceNames[tableKind],
                    new Dictionary<string, string>
                    {
                        ["capture_instance"] = names.CaptureInstanceNames[tableKind].Value,
                    },
                    CdcProviderRetryContinuityClassification.None
                )
            ),
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

    private static IReadOnlyList<CdcSourceTableInventory> BuildSourceTableInventory(CdcProvider provider) =>
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
