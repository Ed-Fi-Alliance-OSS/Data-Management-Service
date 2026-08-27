// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal static class CdcConnectorTemplateTestData
{
    public const long BindingGeneration = 7;
    public const string DefaultDeploymentKey = "dms";
    public const string DefaultInstanceKey = "binding";
    public const string DefaultTopicPrefix = "edfi.documents";
    private const string SourceIdentity = "f81d4fae-7dec-11d0-a765-00a0c91e6bf6";
    private const string OtherSourceIdentity = "86a7cc04-64cf-4b34-b66f-a7b9b4f6b6fd";

    public static readonly CdcSourceFingerprint SourceFingerprint = SourceFingerprintFor(
        CdcProvider.Postgresql
    );

    public static readonly CdcSourceFingerprint OtherPostgresqlSourceFingerprint =
        CdcSourceFingerprintMetadata.Compute(CdcProvider.Postgresql, OtherSourceIdentity);

    public static CdcSourceFingerprint SourceFingerprintFor(CdcProvider provider) =>
        CdcSourceFingerprintMetadata.Compute(provider, SourceIdentity);

    public static CdcConnectorTemplateRequest BuildRequest(
        CdcProvider provider,
        CdcConnectorTemplateDeploymentPolicy? deploymentPolicy = null,
        IReadOnlyDictionary<string, string>? providerConnectionProperties = null,
        IReadOnlyDictionary<string, string>? kafkaSecurityProperties = null,
        CdcConnectorTemplateArtifactOutputRequest? artifactOutput = null,
        IReadOnlyList<CdcProviderArtifactObservation>? artifactInventory = null,
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory = null,
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedMessageKeyColumns = null,
        string heartbeatSql = "select 1",
        CdcProviderSetupOutcome outcome = CdcProviderSetupOutcome.CreatedOrMatched,
        CdcSourceFingerprint? fingerprint = null,
        string deploymentKey = DefaultDeploymentKey,
        string topicPrefix = DefaultTopicPrefix,
        string instanceKey = DefaultInstanceKey
    )
    {
        CdcSourceFingerprint physicalSourceFingerprint = fingerprint ?? SourceFingerprintFor(provider);
        CoreCdc.CdcBinding binding = BuildBinding(
            provider,
            fingerprint: physicalSourceFingerprint,
            deploymentKey: deploymentKey,
            topicPrefix: topicPrefix,
            instanceKey: instanceKey
        );

        return new CdcConnectorTemplateRequest(
            binding,
            new CdcConnectorProviderSetupEvidence(
                BindingGeneration,
                BuildProviderSetupResult(
                    provider,
                    mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
                    outcome: outcome,
                    boundPhysicalSourceFingerprint: physicalSourceFingerprint,
                    artifactInventory: artifactInventory ?? BuildArtifactInventory(provider, binding),
                    sourceTableInventory: sourceTableInventory,
                    expectedMessageKeyColumns: expectedMessageKeyColumns,
                    heartbeatActionQuery: new CdcHeartbeatActionQuery(heartbeatSql, "sha256-safe"),
                    binding: binding
                )
            ),
            deploymentPolicy ?? BuildDeploymentPolicy(provider),
            new CdcProviderConnectionProperties(
                provider,
                providerConnectionProperties ?? BuildProviderConnectionProperties(provider)
            ),
            new CdcKafkaClientSecurityProperties(kafkaSecurityProperties ?? new Dictionary<string, string>()),
            artifactOutput
        );
    }

    public static CdcConnectorTemplateRequest BuildRequest(
        CdcProviderSetupResult providerSetupResult,
        CoreCdc.CdcBinding? binding = null,
        long providerSetupBindingGeneration = BindingGeneration,
        CdcProviderConnectionProperties? providerConnectionProperties = null,
        CdcConnectorTemplateDeploymentPolicy? deploymentPolicy = null,
        CdcKafkaClientSecurityProperties? kafkaClientSecurityProperties = null,
        CdcConnectorTemplateArtifactOutputRequest? artifactOutput = null
    )
    {
        CoreCdc.CdcBinding templateBinding = binding ?? BuildBinding(providerSetupResult.Provider);

        return new CdcConnectorTemplateRequest(
            templateBinding,
            new CdcConnectorProviderSetupEvidence(providerSetupBindingGeneration, providerSetupResult),
            deploymentPolicy ?? BuildDeploymentPolicy(providerSetupResult.Provider),
            providerConnectionProperties
                ?? CdcProviderConnectionProperties.Empty(ToDdlProvider(templateBinding.Provider)),
            kafkaClientSecurityProperties ?? CdcKafkaClientSecurityProperties.Empty,
            artifactOutput
        );
    }

    public static CoreCdc.CdcBinding BuildBinding(
        CdcProvider provider,
        long bindingGeneration = BindingGeneration,
        string partitionerAlgorithm = CoreCdc.CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
        CdcSourceFingerprint? fingerprint = null,
        string deploymentKey = DefaultDeploymentKey,
        string tenantKey = CoreCdc.CdcTargetValidator.DefaultBindingTenantKey,
        string dataStoreId = "1",
        string instanceKey = DefaultInstanceKey,
        string topicPrefix = DefaultTopicPrefix
    )
    {
        CoreCdc.CdcArtifactInventory artifactInventory = BuildCoreArtifactInventory(
            provider,
            bindingGeneration,
            deploymentKey,
            topicPrefix,
            instanceKey
        );

        return new CoreCdc.CdcBinding(
            CoreCdc.CdcJsonContract.CurrentContractVersion,
            deploymentKey,
            tenantKey,
            dataStoreId,
            instanceKey,
            bindingGeneration,
            ToCoreProvider(provider),
            (fingerprint ?? SourceFingerprintFor(provider)).Value,
            artifactInventory.ConnectorName,
            artifactInventory.TopicName,
            PartitionCount: 1,
            partitionerAlgorithm,
            CoreCdc.CdcJsonContract.CurrentContractVersion
        );
    }

    public static CoreCdc.CdcArtifactInventory BuildCoreArtifactInventory(
        CdcProvider provider,
        long bindingGeneration = BindingGeneration,
        string deploymentKey = DefaultDeploymentKey,
        string topicPrefix = DefaultTopicPrefix,
        string instanceKey = DefaultInstanceKey
    )
    {
        CoreCdc.CdcArtifactNameResult result = CoreCdc.CdcArtifactNameGenerator.Render(
            new CoreCdc.CdcArtifactNameInput(
                deploymentKey,
                topicPrefix,
                instanceKey,
                bindingGeneration,
                ToCoreProvider(provider)
            )
        );

        return result.Inventory
            ?? throw new ArgumentException("Invalid test CDC binding artifact input.", nameof(provider));
    }

    public static CoreCdc.CdcArtifactInventory BuildCoreArtifactInventory(CoreCdc.CdcBinding binding)
    {
        CoreCdc.CdcArtifactNameResult result = CoreCdc.CdcArtifactNameGenerator.RecoverFromBinding(binding);

        return result.Inventory
            ?? throw new ArgumentException("Invalid test CDC binding artifact input.", nameof(binding));
    }

    public static CdcProviderArtifactNames BuildProviderArtifactNames(
        CdcProvider provider,
        CoreCdc.CdcBinding? binding = null
    )
    {
        CoreCdc.CdcArtifactInventory artifactInventory = binding is null
            ? BuildCoreArtifactInventory(provider)
            : BuildCoreArtifactInventory(binding);

        return provider switch
        {
            CdcProvider.Postgresql => CdcProviderArtifactNames.ForPostgresql(
                new CdcSafeName(artifactInventory.PostgresqlPublicationName!),
                new CdcSafeName(artifactInventory.PostgresqlLogicalSlotName!)
            ),
            CdcProvider.SqlServer => CdcProviderArtifactNames.ForSqlServer(
                new CdcSafeName(artifactInventory.SqlServerCdcGatingRoleName!),
                new Dictionary<CdcSourceTableKind, CdcSafeName>
                {
                    [CdcSourceTableKind.DocumentCache] = new(
                        artifactInventory.SqlServerCaptureInstanceDocumentCacheName!
                    ),
                    [CdcSourceTableKind.Document] = new(
                        artifactInventory.SqlServerCaptureInstanceDocumentName!
                    ),
                    [CdcSourceTableKind.CdcHeartbeat] = new(
                        artifactInventory.SqlServerCaptureInstanceCdcHeartbeatName!
                    ),
                }
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };
    }

    public static CdcConnectorTemplateDeploymentPolicy BuildDeploymentPolicy(CdcProvider provider) =>
        new(
            "broker-1:9092,broker-2:9092",
            maxRecordBytes: 67_108_864,
            heartbeatInterval: TimeSpan.FromSeconds(5),
            sqlServerPollInterval: provider == CdcProvider.SqlServer ? TimeSpan.FromSeconds(2) : null
        );

    public static CdcConnectorTemplateSourcePartitionEvidence BuildSourcePartitionEvidence(
        CdcConnectorTemplateRequest request
    )
    {
        var properties = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["server"] = request.ConnectorName.Value,
        };

        if (request.Provider == CdcProvider.SqlServer)
        {
            properties["database"] = request.ProviderConnectionProperties.Properties["database.names"];
        }

        return new CdcConnectorTemplateSourcePartitionEvidence(properties);
    }

    public static CdcProviderSetupResult BuildProviderSetupResult(
        CdcProvider provider,
        CdcProviderSetupOutcome outcome = CdcProviderSetupOutcome.ExactMatch,
        CdcProviderSetupMode mode = CdcProviderSetupMode.ValidateOnly,
        CdcSourceFingerprint? boundPhysicalSourceFingerprint = null,
        CdcSourceFingerprint? observedSourceFingerprint = null,
        IReadOnlyList<CdcProviderArtifactObservation>? artifactInventory = null,
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory = null,
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedMessageKeyColumns = null,
        CdcHeartbeatActionQuery? heartbeatActionQuery = null,
        bool omitHeartbeatActionQuery = false,
        IReadOnlyList<CdcProviderHistoryObservation>? providerHistoryObservations = null,
        CoreCdc.CdcBinding? binding = null
    )
    {
        CdcSourceFingerprint boundFingerprint =
            boundPhysicalSourceFingerprint ?? SourceFingerprintFor(provider);

        return new CdcProviderSetupResult(
            Provider: provider,
            Mode: mode,
            Outcome: outcome,
            BoundPhysicalSourceFingerprint: boundFingerprint,
            ObservedSourceFingerprint: observedSourceFingerprint ?? boundFingerprint,
            ArtifactInventory: artifactInventory ?? BuildArtifactInventory(provider, binding),
            GrantInventory: [],
            SourceTableInventory: sourceTableInventory ?? BuildRequiredSourceTableInventory(provider),
            ExpectedMessageKeyColumns: expectedMessageKeyColumns ?? BuildExpectedMessageKeyColumns(),
            HeartbeatActionQuery: omitHeartbeatActionQuery
                ? null
                : heartbeatActionQuery ?? new CdcHeartbeatActionQuery("select 1", "sha256-safe"),
            ProviderHistoryObservations: providerHistoryObservations
                ?? (
                    provider == CdcProvider.SqlServer
                        ? BuildSqlServerProviderHistoryObservations(binding)
                        : []
                ),
            ManifestPayload: null,
            Diagnostics: []
        );
    }

    public static IReadOnlyDictionary<string, string> BuildProviderConnectionProperties(
        CdcProvider provider
    ) =>
        provider switch
        {
            CdcProvider.Postgresql => BuildPostgresqlConnectionProperties(),
            CdcProvider.SqlServer => BuildSqlServerConnectionProperties(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    public static IReadOnlyDictionary<string, string> BuildRequiredProviderConnectionProperties(
        CdcProvider provider
    ) =>
        provider switch
        {
            CdcProvider.Postgresql => new Dictionary<string, string>
            {
                ["database.hostname"] = "postgresql.internal",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.dbname"] = "edfi_datastore",
            },
            CdcProvider.SqlServer => new Dictionary<string, string>
            {
                ["database.hostname"] = "sqlserver.internal",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.names"] = "edfi_datastore",
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    public static IReadOnlyDictionary<string, string> BuildPostgresqlConnectionProperties(
        string passwordReference = "${env:CDC_DATABASE_PASSWORD}"
    ) =>
        new Dictionary<string, string>
        {
            ["database.hostname"] = "postgresql.internal",
            ["database.port"] = "5432",
            ["database.user"] = "connector_user",
            ["database.password"] = passwordReference,
            ["database.dbname"] = "edfi_datastore",
        };

    public static IReadOnlyDictionary<string, string> BuildSqlServerConnectionProperties(
        string passwordReference = "${env:CDC_DATABASE_PASSWORD}"
    ) =>
        new Dictionary<string, string>
        {
            ["database.hostname"] = "sqlserver.internal",
            ["database.port"] = "1433",
            ["database.user"] = "connector_user",
            ["database.password"] = passwordReference,
            ["database.names"] = "edfi_datastore",
        };

    public static IReadOnlyList<CdcProviderArtifactObservation> BuildArtifactInventory(
        CdcProvider provider,
        CoreCdc.CdcBinding? binding = null
    ) =>
        provider switch
        {
            CdcProvider.Postgresql => BuildPostgresqlArtifactInventory(binding),
            CdcProvider.SqlServer => BuildSqlServerArtifactInventory(binding),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    public static IReadOnlyList<CdcProviderArtifactObservation> BuildPostgresqlArtifactInventory(
        CoreCdc.CdcBinding? binding = null
    ) =>
        [
            BuildPostgresqlPublicationArtifact(binding: binding),
            BuildPostgresqlReplicationSlotArtifact(binding: binding),
        ];

    public static CdcProviderArtifactObservation BuildPostgresqlPublicationArtifact(
        CdcProviderArtifactState state = CdcProviderArtifactState.Matched,
        CdcSafeName? safeArtifactName = null,
        CoreCdc.CdcBinding? binding = null
    ) =>
        new(
            CdcProviderArtifactKind.PostgresqlPublication,
            safeArtifactName
                ?? BuildProviderArtifactNames(CdcProvider.Postgresql, binding).Postgresql!.PublicationName,
            state,
            new Dictionary<string, string>()
        );

    public static CdcProviderArtifactObservation BuildPostgresqlReplicationSlotArtifact(
        CdcProviderArtifactState state = CdcProviderArtifactState.Matched,
        CdcSafeName? safeArtifactName = null,
        CoreCdc.CdcBinding? binding = null
    ) =>
        new(
            CdcProviderArtifactKind.PostgresqlReplicationSlot,
            safeArtifactName
                ?? BuildProviderArtifactNames(
                    CdcProvider.Postgresql,
                    binding
                ).Postgresql!.ReplicationSlotName,
            state,
            new Dictionary<string, string>()
        );

    public static IReadOnlyList<CdcProviderArtifactObservation> BuildSqlServerArtifactInventory(
        CoreCdc.CdcBinding? binding = null
    ) =>
        [
            BuildSqlServerSnapshotIsolationArtifact(),
            BuildSqlServerGatingRoleArtifact(binding: binding),
            BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.DocumentCache, binding: binding),
            BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.Document, binding: binding),
            BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.CdcHeartbeat, binding: binding),
        ];

    public static CdcProviderArtifactObservation BuildSqlServerGatingRoleArtifact(
        CdcProviderArtifactState state = CdcProviderArtifactState.Matched,
        CdcSafeName? safeArtifactName = null,
        CoreCdc.CdcBinding? binding = null
    ) =>
        new(
            CdcProviderArtifactKind.SqlServerGatingRole,
            safeArtifactName
                ?? BuildProviderArtifactNames(CdcProvider.SqlServer, binding).SqlServer!.GatingRoleName,
            state,
            new Dictionary<string, string>()
        );

    public static CdcProviderArtifactObservation BuildSqlServerSnapshotIsolationArtifact(
        CdcProviderArtifactState state = CdcProviderArtifactState.Matched,
        string allowSnapshotIsolation = "True"
    ) =>
        new(
            CdcProviderArtifactKind.ProviderHistory,
            new CdcSafeName("sqlserver_snapshot_isolation"),
            state,
            new Dictionary<string, string>
            {
                ["allow_snapshot_isolation"] = allowSnapshotIsolation,
                ["snapshot_isolation_state_desc"] = allowSnapshotIsolation == "True" ? "ON" : "OFF",
            }
        );

    public static CdcProviderArtifactObservation BuildSqlServerCaptureInstanceArtifact(
        CdcSourceTableKind tableKind,
        CdcProviderArtifactState state = CdcProviderArtifactState.Matched,
        CdcSafeName? safeArtifactName = null,
        IReadOnlyDictionary<string, string>? safeObservedValues = null,
        CoreCdc.CdcBinding? binding = null
    )
    {
        CdcSafeName artifactName =
            safeArtifactName ?? DefaultSqlServerCaptureInstanceName(tableKind, binding);
        var observedValues = new Dictionary<string, string>
        {
            ["capture_instance"] = artifactName.Value,
            ["source_table_kind"] = SqlServerCaptureInstanceSourceTableKindToken(tableKind),
        };

        if (safeObservedValues is not null)
        {
            foreach (var value in safeObservedValues)
            {
                observedValues[value.Key] = value.Value;
            }
        }

        return new CdcProviderArtifactObservation(
            CdcProviderArtifactKind.SqlServerCaptureInstance,
            artifactName,
            state,
            observedValues
        );
    }

    private static CdcSafeName DefaultSqlServerCaptureInstanceName(
        CdcSourceTableKind tableKind,
        CoreCdc.CdcBinding? binding
    ) =>
        tableKind switch
        {
            CdcSourceTableKind.DocumentCache => BuildProviderArtifactNames(
                CdcProvider.SqlServer,
                binding
            ).SqlServer!.CaptureInstanceNames[tableKind],
            CdcSourceTableKind.Document => BuildProviderArtifactNames(
                CdcProvider.SqlServer,
                binding
            ).SqlServer!.CaptureInstanceNames[tableKind],
            CdcSourceTableKind.CdcHeartbeat => BuildProviderArtifactNames(
                CdcProvider.SqlServer,
                binding
            ).SqlServer!.CaptureInstanceNames[tableKind],
            _ => throw new ArgumentOutOfRangeException(
                nameof(tableKind),
                tableKind,
                "Unsupported CDC source table kind."
            ),
        };

    private static string SqlServerCaptureInstanceSourceTableKindToken(CdcSourceTableKind tableKind) =>
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

    public static IReadOnlyList<CdcProviderHistoryObservation> BuildProviderHistoryObservations(
        CdcProvider provider
    ) => provider == CdcProvider.SqlServer ? BuildSqlServerProviderHistoryObservations() : [];

    public static IReadOnlyList<CdcProviderHistoryObservation> BuildSqlServerProviderHistoryObservations() =>
        BuildSqlServerProviderHistoryObservations(binding: null);

    public static IReadOnlyList<CdcProviderHistoryObservation> BuildSqlServerProviderHistoryObservations(
        CoreCdc.CdcBinding? binding
    )
    {
        CdcSqlServerProviderArtifactNames sqlServerNames = BuildProviderArtifactNames(
            CdcProvider.SqlServer,
            binding
        ).SqlServer!;
        CdcSafeName documentCacheName = sqlServerNames.CaptureInstanceNames[CdcSourceTableKind.DocumentCache];
        CdcSafeName documentName = sqlServerNames.CaptureInstanceNames[CdcSourceTableKind.Document];

        return
        [
            new(
                CdcProviderArtifactKind.SqlServerCaptureInstance,
                documentCacheName,
                new Dictionary<string, string> { ["capture_instance"] = documentCacheName.Value },
                CdcProviderRetryContinuityClassification.None
            ),
            new(
                CdcProviderArtifactKind.SqlServerCaptureInstance,
                documentName,
                new Dictionary<string, string> { ["capture_instance"] = documentName.Value },
                CdcProviderRetryContinuityClassification.None
            ),
        ];
    }

    public static IReadOnlyList<CdcSourceTableInventory> BuildRequiredSourceTableInventory(
        CdcProvider provider
    ) =>
        [
            BuildSourceTable(
                provider,
                CdcSourceTableKind.DocumentCache,
                "DocumentCache",
                [BuildColumn(provider, "DocumentUuid")]
            ),
            BuildSourceTable(
                provider,
                CdcSourceTableKind.Document,
                "Document",
                [BuildColumn(provider, "DocumentUuid")]
            ),
            BuildSourceTable(
                provider,
                CdcSourceTableKind.CdcHeartbeat,
                "CdcHeartbeat",
                [
                    BuildColumn(provider, "HeartbeatId"),
                    BuildColumn(provider, "HeartbeatSequence", 2),
                    BuildColumn(provider, "HeartbeatAt", 3),
                ]
            ),
        ];

    public static CdcSourceTableInventory BuildSourceTable(
        CdcProvider provider,
        CdcSourceTableKind tableKind,
        string tableName,
        IReadOnlyList<CdcSourceColumnInventory> columns
    ) =>
        new(
            tableKind,
            new DbTableName(new DbSchemaName("dms"), tableName),
            provider == CdcProvider.Postgresql ? $"\"dms\".\"{tableName}\"" : $"[dms].[{tableName}]",
            columns
        );

    public static CdcSourceColumnInventory BuildColumn(
        CdcProvider provider,
        string columnName,
        int ordinal = 1,
        string? providerDataType = null,
        bool isNullable = false
    ) =>
        new(
            new DbColumnName(columnName),
            provider == CdcProvider.Postgresql ? $"\"{columnName}\"" : $"[{columnName}]",
            ordinal,
            providerDataType ?? (provider == CdcProvider.Postgresql ? "text" : "nvarchar(max)"),
            IsNullable: isNullable
        );

    public static IReadOnlyList<CdcSourceTableInventory> BuildSourceInventoryReplacing(
        CdcProvider provider,
        CdcSourceTableInventory replacement
    ) =>
        BuildRequiredSourceTableInventory(provider)
            .Select(table => table.TableKind == replacement.TableKind ? replacement : table)
            .ToArray();

    public static IReadOnlyList<CdcSourceTableInventory> BuildHeartbeatSourceInventory(
        CdcProvider provider,
        IReadOnlyList<CdcSourceColumnInventory> heartbeatColumns
    ) =>
        BuildSourceInventoryReplacing(
            provider,
            BuildSourceTable(provider, CdcSourceTableKind.CdcHeartbeat, "CdcHeartbeat", heartbeatColumns)
        );

    public static IReadOnlyList<CdcExpectedMessageKeyColumns> BuildExpectedMessageKeyColumns() =>
        [
            new(CdcSourceTableKind.DocumentCache, [new DbColumnName("DocumentUuid")]),
            new(CdcSourceTableKind.Document, [new DbColumnName("DocumentUuid")]),
        ];

    public static CoreCdc.CdcProvider ToCoreProvider(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql => CoreCdc.CdcProvider.Postgresql,
            CdcProvider.SqlServer => CoreCdc.CdcProvider.SqlServer,
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    public static CdcProvider ToDdlProvider(CoreCdc.CdcProvider provider) =>
        provider switch
        {
            CoreCdc.CdcProvider.Postgresql => CdcProvider.Postgresql,
            CoreCdc.CdcProvider.SqlServer => CdcProvider.SqlServer,
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };
}
