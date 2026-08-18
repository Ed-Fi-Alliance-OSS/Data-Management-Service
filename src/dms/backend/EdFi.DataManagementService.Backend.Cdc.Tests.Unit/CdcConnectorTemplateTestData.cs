// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

internal static class CdcConnectorTemplateTestData
{
    public const long BindingGeneration = 7;
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
        string connectorName = "dms_binding_connector"
    )
    {
        CdcSourceFingerprint physicalSourceFingerprint = fingerprint ?? SourceFingerprintFor(provider);

        return new CdcConnectorTemplateRequest(
            BuildBinding(provider, fingerprint: physicalSourceFingerprint, connectorName: connectorName),
            new CdcConnectorProviderSetupEvidence(
                BindingGeneration,
                BuildProviderSetupResult(
                    provider,
                    outcome: outcome,
                    boundPhysicalSourceFingerprint: physicalSourceFingerprint,
                    artifactInventory: artifactInventory,
                    sourceTableInventory: sourceTableInventory,
                    expectedMessageKeyColumns: expectedMessageKeyColumns,
                    heartbeatActionQuery: new CdcHeartbeatActionQuery(heartbeatSql, "sha256-safe")
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
        CdcConnectorTemplateBindingIdentity? binding = null,
        long providerSetupBindingGeneration = BindingGeneration,
        CdcProviderConnectionProperties? providerConnectionProperties = null,
        CdcConnectorTemplateDeploymentPolicy? deploymentPolicy = null,
        CdcKafkaClientSecurityProperties? kafkaClientSecurityProperties = null,
        CdcConnectorTemplateArtifactOutputRequest? artifactOutput = null
    )
    {
        CdcConnectorTemplateBindingIdentity bindingIdentity =
            binding ?? BuildBinding(providerSetupResult.Provider);

        return new CdcConnectorTemplateRequest(
            bindingIdentity,
            new CdcConnectorProviderSetupEvidence(providerSetupBindingGeneration, providerSetupResult),
            deploymentPolicy
                ?? new CdcConnectorTemplateDeploymentPolicy("broker:9092", maxRecordBytes: 1_048_576),
            providerConnectionProperties ?? CdcProviderConnectionProperties.Empty(bindingIdentity.Provider),
            kafkaClientSecurityProperties ?? CdcKafkaClientSecurityProperties.Empty,
            artifactOutput
        );
    }

    public static CdcConnectorTemplateBindingIdentity BuildBinding(
        CdcProvider provider,
        long bindingGeneration = BindingGeneration,
        string partitionerAlgorithm = CdcConnectorTemplateBindingIdentity.KafkaMurmur2V1PartitionerAlgorithm,
        CdcSourceFingerprint? fingerprint = null,
        string connectorName = "dms_binding_connector"
    ) =>
        new(
            provider,
            new CdcSafeName(connectorName),
            "edfi.documents",
            bindingGeneration,
            partitionerAlgorithm,
            fingerprint ?? SourceFingerprintFor(provider)
        );

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
        CdcProviderSetupOutcome outcome = CdcProviderSetupOutcome.CreatedOrMatched,
        CdcSourceFingerprint? boundPhysicalSourceFingerprint = null,
        CdcSourceFingerprint? observedSourceFingerprint = null,
        IReadOnlyList<CdcProviderArtifactObservation>? artifactInventory = null,
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory = null,
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedMessageKeyColumns = null,
        CdcHeartbeatActionQuery? heartbeatActionQuery = null,
        bool omitHeartbeatActionQuery = false,
        IReadOnlyList<CdcProviderHistoryObservation>? providerHistoryObservations = null
    )
    {
        CdcSourceFingerprint boundFingerprint =
            boundPhysicalSourceFingerprint ?? SourceFingerprintFor(provider);

        return new CdcProviderSetupResult(
            Provider: provider,
            Mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
            Outcome: outcome,
            BoundPhysicalSourceFingerprint: boundFingerprint,
            ObservedSourceFingerprint: observedSourceFingerprint ?? boundFingerprint,
            ArtifactInventory: artifactInventory ?? BuildArtifactInventory(provider),
            GrantInventory: [],
            SourceTableInventory: sourceTableInventory ?? BuildRequiredSourceTableInventory(provider),
            ExpectedMessageKeyColumns: expectedMessageKeyColumns ?? BuildExpectedMessageKeyColumns(),
            HeartbeatActionQuery: omitHeartbeatActionQuery
                ? null
                : heartbeatActionQuery ?? new CdcHeartbeatActionQuery("select 1", "sha256-safe"),
            ProviderHistoryObservations: providerHistoryObservations
                ?? BuildProviderHistoryObservations(provider),
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
        CdcProvider provider
    ) =>
        provider switch
        {
            CdcProvider.Postgresql => BuildPostgresqlArtifactInventory(),
            CdcProvider.SqlServer => BuildSqlServerArtifactInventory(),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    public static IReadOnlyList<CdcProviderArtifactObservation> BuildPostgresqlArtifactInventory() =>
        [
            new(
                CdcProviderArtifactKind.PostgresqlPublication,
                new CdcSafeName("dms_binding_publication"),
                CdcProviderArtifactState.Matched,
                new Dictionary<string, string>()
            ),
            new(
                CdcProviderArtifactKind.PostgresqlReplicationSlot,
                new CdcSafeName("dms_binding_slot"),
                CdcProviderArtifactState.Matched,
                new Dictionary<string, string>()
            ),
        ];

    public static IReadOnlyList<CdcProviderArtifactObservation> BuildSqlServerArtifactInventory() =>
        [
            BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.DocumentCache),
            BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.Document),
            BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.CdcHeartbeat),
        ];

    public static CdcProviderArtifactObservation BuildSqlServerCaptureInstanceArtifact(
        CdcSourceTableKind tableKind,
        CdcProviderArtifactState state = CdcProviderArtifactState.Matched,
        CdcSafeName? safeArtifactName = null,
        IReadOnlyDictionary<string, string>? safeObservedValues = null
    )
    {
        CdcSafeName artifactName = safeArtifactName ?? DefaultSqlServerCaptureInstanceName(tableKind);
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

    private static CdcSafeName DefaultSqlServerCaptureInstanceName(CdcSourceTableKind tableKind) =>
        tableKind switch
        {
            CdcSourceTableKind.DocumentCache => new CdcSafeName("dms_binding_document_cache_capture"),
            CdcSourceTableKind.Document => new CdcSafeName("dms_binding_document_capture"),
            CdcSourceTableKind.CdcHeartbeat => new CdcSafeName("dms_binding_cdc_heartbeat_capture"),
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
        [
            new(
                CdcProviderArtifactKind.SqlServerCaptureInstance,
                new CdcSafeName("dms_binding_document_cache_capture"),
                new Dictionary<string, string>
                {
                    ["capture_instance"] = "dms_binding_document_cache_capture",
                },
                CdcProviderRetryContinuityClassification.None
            ),
            new(
                CdcProviderArtifactKind.SqlServerCaptureInstance,
                new CdcSafeName("dms_binding_document_capture"),
                new Dictionary<string, string> { ["capture_instance"] = "dms_binding_document_capture" },
                CdcProviderRetryContinuityClassification.None
            ),
        ];

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
        int ordinal = 1
    ) =>
        new(
            new DbColumnName(columnName),
            provider == CdcProvider.Postgresql ? $"\"{columnName}\"" : $"[{columnName}]",
            ordinal,
            provider == CdcProvider.Postgresql ? "text" : "nvarchar(max)",
            IsNullable: false
        );

    public static IReadOnlyList<CdcSourceTableInventory> BuildSourceInventoryReplacing(
        CdcProvider provider,
        CdcSourceTableInventory replacement
    ) =>
        BuildRequiredSourceTableInventory(provider)
            .Select(table => table.TableKind == replacement.TableKind ? replacement : table)
            .ToArray();

    public static IReadOnlyList<CdcExpectedMessageKeyColumns> BuildExpectedMessageKeyColumns() =>
        [
            new(CdcSourceTableKind.DocumentCache, [new DbColumnName("DocumentUuid")]),
            new(CdcSourceTableKind.Document, [new DbColumnName("DocumentUuid")]),
        ];
}
