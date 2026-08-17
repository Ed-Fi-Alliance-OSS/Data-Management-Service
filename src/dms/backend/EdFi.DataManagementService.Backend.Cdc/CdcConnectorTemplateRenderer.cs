// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;

namespace EdFi.DataManagementService.Backend.Cdc;

internal interface ICdcConnectorTemplateRenderer
{
    CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request);
}

internal sealed class CdcConnectorTemplateRenderer(ICdcConnectorTemplateInputValidator inputValidator)
    : ICdcConnectorTemplateRenderer
{
    private const int DefaultHeartbeatIntervalMilliseconds = 5000;
    private const int DefaultProducerBufferBytes = 33_554_432;

    public CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        CdcConnectorTemplateValidationResult requestValidationResult = inputValidator.ValidateRequest(
            request,
            CdcConnectorTemplateSourcePhase.Rendering
        );
        IReadOnlyList<CdcConnectorTemplateDiagnostic> renderingDiagnostics = ValidateProviderSpecificInput(
            request
        );
        CdcConnectorTemplateDiagnostic[] diagnostics =
        [
            .. requestValidationResult.Diagnostics,
            .. renderingDiagnostics,
        ];

        if (
            Array.Exists(
                diagnostics,
                diagnostic => diagnostic.Severity == CdcConnectorTemplateDiagnosticSeverity.Error
            )
        )
        {
            return new CdcConnectorTemplateResult(
                request.BindingIdentity,
                CdcConnectorTemplateOutcome.ValidationFailed,
                new SortedDictionary<string, string>(StringComparer.Ordinal),
                registrationPayload: null,
                redactedArtifactPayload: null,
                configSha256: null,
                diagnostics
            );
        }

        IReadOnlyDictionary<string, string> config = BuildConfig(request);
        var registrationPayload = new CdcKafkaConnectRegistrationPayload(request.ConnectorName, config);

        return new CdcConnectorTemplateResult(
            request.BindingIdentity,
            CdcConnectorTemplateOutcome.Rendered,
            config,
            registrationPayload,
            redactedArtifactPayload: null,
            ComputeCanonicalConfigSha256(config),
            diagnostics
        );
    }

    private static IReadOnlyDictionary<string, string> BuildConfig(CdcConnectorTemplateRequest request)
    {
        var config = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = request.ConnectorName.Value,
            ["connector.class"] = ConnectorClass(request.Provider),
            ["tasks.max"] = "1",
            ["topic.prefix"] = request.ConnectorName.Value,
            ["transforms"] = "documentState",
            ["transforms.documentState.type"] = "org.edfi.kafka.connect.transforms.DocumentState",
            ["transforms.documentState.provider"] = ProviderToken(request.Provider),
            ["transforms.documentState.target.topic"] = request.PublicTopicName,
            ["transforms.documentState.progress.topic"] = request.ProgressTopicName,
            ["key.converter"] = "org.apache.kafka.connect.storage.StringConverter",
            ["value.converter"] = "org.edfi.kafka.connect.converters.DocumentStateJsonConverter",
            ["value.converter.schemas.enable"] = "false",
            ["value.converter.decimal.format"] = "NUMERIC",
            ["tombstones.on.delete"] = "false",
            ["errors.tolerance"] = "none",
            ["statistics.metrics.enabled"] = "true",
            ["snapshot.mode"] = "initial",
            ["producer.override.enable.idempotence"] = "true",
            ["producer.override.acks"] = "all",
            ["producer.override.retries"] = int.MaxValue.ToString(),
            ["producer.override.max.in.flight.requests.per.connection"] = "5",
            ["producer.override.max.request.size"] = request.DeploymentPolicy.MaxRecordBytes.ToString(),
            ["producer.override.buffer.memory"] = ProducerBufferBytes(request).ToString(),
            ["producer.override.compression.type"] = "none",
            ["producer.override.partitioner.class"] =
                "org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner",
            ["heartbeat.interval.ms"] = HeartbeatIntervalMilliseconds(request).ToString(),
            ["heartbeat.action.query"] = request.ProviderSetupEvidence.Result.HeartbeatActionQuery!.Sql,
            ["topic.delimiter"] = ".",
            ["topic.naming.strategy"] = "io.debezium.schema.SchemaTopicNamingStrategy",
            ["topic.heartbeat.prefix"] = "__debezium-heartbeat",
        };

        foreach (var property in request.ProviderConnectionProperties.Properties)
        {
            config[property.Key] = property.Value;
        }

        foreach (var property in request.KafkaClientSecurityProperties.Properties)
        {
            config[$"producer.override.{property.Key}"] = property.Value;
        }

        AddProviderSpecificConfig(request, config);

        return config;
    }

    private static void AddProviderSpecificConfig(
        CdcConnectorTemplateRequest request,
        SortedDictionary<string, string> config
    )
    {
        if (request.Provider == CdcProvider.Postgresql)
        {
            AddPostgresqlConfig(request, config);
        }
    }

    private static void AddPostgresqlConfig(
        CdcConnectorTemplateRequest request,
        SortedDictionary<string, string> config
    )
    {
        CdcProviderArtifactObservation publication = RequiredArtifact(
            request,
            CdcProviderArtifactKind.PostgresqlPublication
        );
        CdcProviderArtifactObservation replicationSlot = RequiredArtifact(
            request,
            CdcProviderArtifactKind.PostgresqlReplicationSlot
        );

        config["plugin.name"] = "pgoutput";
        config["publication.autocreate.mode"] = "disabled";
        config["publication.name"] = publication.SafeArtifactName.Value;
        config["slot.name"] = replicationSlot.SafeArtifactName.Value;
        config["table.include.list"] = string.Join(
            ",",
            OrderedSourceTables(request).Select(table => table.EmittedQuotedTableName)
        );
        config["message.key.columns"] = string.Join(
            ";",
            OrderedMessageKeyColumns(request)
                .Select(messageKeyColumns =>
                {
                    CdcSourceTableInventory table = SourceTable(request, messageKeyColumns.TableKind);
                    return $"{table.EmittedQuotedTableName}:{EmittedKeyColumnList(table, messageKeyColumns)}";
                })
        );
        config["unavailable.value.placeholder"] = "__debezium_unavailable_value";
    }

    private static IReadOnlyList<CdcConnectorTemplateDiagnostic> ValidateProviderSpecificInput(
        CdcConnectorTemplateRequest request
    )
    {
        if (request.Provider != CdcProvider.Postgresql)
        {
            return [];
        }

        List<CdcConnectorTemplateDiagnostic> diagnostics = [];

        AddMissingArtifactDiagnosticIfNeeded(
            request,
            diagnostics,
            CdcProviderArtifactKind.PostgresqlPublication,
            CdcConnectorTemplateDiagnosticCodes.PostgresqlPublicationMetadataRequired,
            "publication.name"
        );
        AddMissingArtifactDiagnosticIfNeeded(
            request,
            diagnostics,
            CdcProviderArtifactKind.PostgresqlReplicationSlot,
            CdcConnectorTemplateDiagnosticCodes.PostgresqlReplicationSlotMetadataRequired,
            "slot.name"
        );

        foreach (CdcSourceTableInventory sourceTable in OrderedSourceTables(request))
        {
            DbTableName expectedTableName = ExpectedSourceTableName(sourceTable.TableKind);
            if (sourceTable.TableName.Equals(expectedTableName))
            {
                continue;
            }

            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch,
                    CdcConnectorTemplateDiagnosticCategory.IncludeList,
                    "table.include.list",
                    $"{expectedTableName.Schema.Value}.{expectedTableName.Name}",
                    SanitizePhysicalIdentifier(
                        $"{sourceTable.TableName.Schema.Value}.{sourceTable.TableName.Name}"
                    ),
                    request,
                    CdcConnectorTemplateSourcePhase.Rendering,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        return diagnostics;
    }

    private static void AddMissingArtifactDiagnosticIfNeeded(
        CdcConnectorTemplateRequest request,
        List<CdcConnectorTemplateDiagnostic> diagnostics,
        CdcProviderArtifactKind artifactKind,
        string code,
        string propertyName
    )
    {
        CdcProviderArtifactObservation[] artifacts = MatchingUsableArtifacts(request, artifactKind);
        if (artifacts.Length == 1)
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                code,
                CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult,
                propertyName,
                "one matched provider setup artifact",
                artifacts.Length == 0 ? "missing" : artifacts.Length.ToString(),
                request,
                CdcConnectorTemplateSourcePhase.Rendering,
                CdcConnectorTemplateRedactionClassification.Safe
            )
        );
    }

    private static CdcProviderArtifactObservation RequiredArtifact(
        CdcConnectorTemplateRequest request,
        CdcProviderArtifactKind artifactKind
    )
    {
        CdcProviderArtifactObservation[] artifacts = MatchingUsableArtifacts(request, artifactKind);
        if (artifacts.Length != 1)
        {
            throw new InvalidOperationException(
                "CDC connector template PostgreSQL provider artifact metadata was not validated before rendering."
            );
        }

        return artifacts[0];
    }

    private static CdcProviderArtifactObservation[] MatchingUsableArtifacts(
        CdcConnectorTemplateRequest request,
        CdcProviderArtifactKind artifactKind
    ) =>
        request
            .ProviderSetupEvidence.Result.ArtifactInventory.Where(artifact =>
                artifact.ArtifactKind == artifactKind
                && artifact.State is CdcProviderArtifactState.Created or CdcProviderArtifactState.Matched
            )
            .ToArray();

    private static IReadOnlyList<CdcSourceTableInventory> OrderedSourceTables(
        CdcConnectorTemplateRequest request
    ) =>
        [
            SourceTable(request, CdcSourceTableKind.DocumentCache),
            SourceTable(request, CdcSourceTableKind.Document),
            SourceTable(request, CdcSourceTableKind.CdcHeartbeat),
        ];

    private static CdcSourceTableInventory SourceTable(
        CdcConnectorTemplateRequest request,
        CdcSourceTableKind tableKind
    ) =>
        request.ProviderSetupEvidence.Result.SourceTableInventory.Single(table =>
            table.TableKind == tableKind
        );

    private static IReadOnlyList<CdcExpectedMessageKeyColumns> OrderedMessageKeyColumns(
        CdcConnectorTemplateRequest request
    ) =>
        [
            MessageKeyColumns(request, CdcSourceTableKind.DocumentCache),
            MessageKeyColumns(request, CdcSourceTableKind.Document),
        ];

    private static CdcExpectedMessageKeyColumns MessageKeyColumns(
        CdcConnectorTemplateRequest request,
        CdcSourceTableKind tableKind
    ) =>
        request.ProviderSetupEvidence.Result.ExpectedMessageKeyColumns.Single(columns =>
            columns.TableKind == tableKind
        );

    private static string EmittedKeyColumnList(
        CdcSourceTableInventory table,
        CdcExpectedMessageKeyColumns messageKeyColumns
    )
    {
        var emittedColumnsByName = table.Columns.ToDictionary(
            column => column.ColumnName.Value,
            StringComparer.Ordinal
        );

        return string.Join(
            ",",
            messageKeyColumns.KeyColumns.Select(column =>
                emittedColumnsByName[column.Value].EmittedQuotedColumnName
            )
        );
    }

    private static DbTableName ExpectedSourceTableName(CdcSourceTableKind tableKind) =>
        tableKind switch
        {
            CdcSourceTableKind.DocumentCache => new(new DbSchemaName("dms"), "DocumentCache"),
            CdcSourceTableKind.Document => new(new DbSchemaName("dms"), "Document"),
            CdcSourceTableKind.CdcHeartbeat => new(new DbSchemaName("dms"), "CdcHeartbeat"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(tableKind),
                tableKind,
                "Unsupported CDC source table kind."
            ),
        };

    private static CdcConnectorTemplateDiagnostic BuildDiagnostic(
        string code,
        CdcConnectorTemplateDiagnosticCategory category,
        string propertyName,
        string? expectedValue,
        string? observedValue,
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        CdcConnectorTemplateRedactionClassification redactionClassification
    ) =>
        new(
            code,
            category,
            CdcConnectorTemplateDiagnosticSeverity.Error,
            propertyName,
            request.ConnectorName,
            expectedValue,
            observedValue,
            request.Provider,
            sourcePhase,
            redactionClassification
        );

    private static string SanitizePhysicalIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "[redacted]";
        }

        return new string(
            value
                .Select(character =>
                    char.IsLetterOrDigit(character) || character is '_' or '.' ? character : '_'
                )
                .ToArray()
        );
    }

    private static int ProducerBufferBytes(CdcConnectorTemplateRequest request) =>
        request.DeploymentPolicy.ProducerBufferBytes
        ?? Math.Max(DefaultProducerBufferBytes, request.DeploymentPolicy.MaxRecordBytes);

    private static long HeartbeatIntervalMilliseconds(CdcConnectorTemplateRequest request)
    {
        if (request.DeploymentPolicy.HeartbeatInterval is null)
        {
            return DefaultHeartbeatIntervalMilliseconds;
        }

        double milliseconds = Math.Ceiling(
            request.DeploymentPolicy.HeartbeatInterval.Value.TotalMilliseconds
        );
        if (milliseconds is < 1 or > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "CDC connector template heartbeat interval must render to a positive millisecond value."
            );
        }

        return Convert.ToInt64(milliseconds);
    }

    private static string ConnectorClass(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql => "io.debezium.connector.postgresql.PostgresConnector",
            CdcProvider.SqlServer => "io.debezium.connector.sqlserver.SqlServerConnector",
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static string ProviderToken(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql => "postgresql",
            CdcProvider.SqlServer => "sqlserver",
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static string ComputeCanonicalConfigSha256(IReadOnlyDictionary<string, string> config)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in config.OrderBy(property => property.Key, StringComparer.Ordinal))
            {
                writer.WriteString(property.Key, property.Value);
            }
            writer.WriteEndObject();
        }

        byte[] hash = SHA256.HashData(stream.ToArray());
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
