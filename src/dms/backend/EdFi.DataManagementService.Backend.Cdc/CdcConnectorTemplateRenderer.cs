// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc;

internal interface ICdcConnectorTemplateRenderer
{
    CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request);
}

internal sealed class CdcConnectorTemplateRenderer(ICdcConnectorTemplateInputValidator inputValidator)
    : ICdcConnectorTemplateRenderer
{
    private const int ManifestFileNameMaxLength = 255;
    private const int ManifestFileNameConnectorHashLength = 32;
    private const string ManifestFileNameConnectorHashPrefix = ".sha256-";
    private const string RedactedArtifactValue = "[redacted]";

    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false
    );

    public CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        CdcConnectorTemplateValidationResult requestValidationResult = inputValidator.ValidateRequest(
            request,
            CdcConnectorTemplateSourcePhase.Render
        );
        IReadOnlyList<CdcConnectorTemplateDiagnostic> diagnostics = requestValidationResult.Diagnostics;

        if (
            diagnostics.Any(diagnostic => diagnostic.Severity == CdcConnectorTemplateDiagnosticSeverity.Error)
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
        string configSha256 = ComputeCanonicalConfigSha256(config);
        CdcConnectorTemplateArtifactPayload? artifactPayload = BuildArtifactPayloadIfRequested(
            request,
            config,
            configSha256
        );

        return new CdcConnectorTemplateResult(
            request.BindingIdentity,
            CdcConnectorTemplateOutcome.Rendered,
            config,
            registrationPayload,
            artifactPayload,
            configSha256,
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
            ["producer.override.partitioner.class"] = PartitionerClass(request.PartitionerAlgorithm),
            ["heartbeat.interval.ms"] = CdcConnectorTemplateSharedRules
                .HeartbeatIntervalMilliseconds(request)
                .ToString(),
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
            return;
        }

        if (request.Provider == CdcProvider.SqlServer)
        {
            AddSqlServerConfig(request, config);
        }
    }

    private static void AddPostgresqlConfig(
        CdcConnectorTemplateRequest request,
        SortedDictionary<string, string> config
    )
    {
        CdcProviderArtifactObservation publication = RequiredArtifact(
            request,
            CdcProviderArtifactKind.PostgresqlPublication,
            request.ProviderArtifactNames.Postgresql!.PublicationName
        );
        CdcProviderArtifactObservation replicationSlot = RequiredArtifact(
            request,
            CdcProviderArtifactKind.PostgresqlReplicationSlot,
            request.ProviderArtifactNames.Postgresql.ReplicationSlotName
        );

        config["plugin.name"] = "pgoutput";
        config["publication.autocreate.mode"] = "disabled";
        config["publication.name"] = publication.SafeArtifactName.Value;
        config["slot.name"] = replicationSlot.SafeArtifactName.Value;
        config["table.include.list"] = string.Join(
            ",",
            CdcConnectorTemplateSharedRules
                .OrderedSourceTables(request)
                .Select(CdcConnectorTemplateDebeziumSelectorFormatter.TableSelector)
        );
        config["message.key.columns"] = string.Join(
            ";",
            CdcConnectorTemplateSharedRules
                .OrderedMessageKeyColumns(request)
                .Select(messageKeyColumns =>
                {
                    CdcSourceTableInventory table = CdcConnectorTemplateSharedRules.SourceTable(
                        request,
                        messageKeyColumns.TableKind
                    );
                    return $"{CdcConnectorTemplateDebeziumSelectorFormatter.TableSelector(table)}:{CdcConnectorTemplateDebeziumSelectorFormatter.KeyColumnList(table, messageKeyColumns)}";
                })
        );
        config["unavailable.value.placeholder"] = "__debezium_unavailable_value";
    }

    private static void AddSqlServerConfig(
        CdcConnectorTemplateRequest request,
        SortedDictionary<string, string> config
    )
    {
        config["table.include.list"] = string.Join(
            ",",
            CdcConnectorTemplateSharedRules
                .OrderedSourceTables(request)
                .Select(CdcConnectorTemplateDebeziumSelectorFormatter.TableSelector)
        );
        config["message.key.columns"] = string.Join(
            ";",
            CdcConnectorTemplateSharedRules
                .OrderedMessageKeyColumns(request)
                .Select(messageKeyColumns =>
                {
                    CdcSourceTableInventory table = CdcConnectorTemplateSharedRules.SourceTable(
                        request,
                        messageKeyColumns.TableKind
                    );
                    return $"{CdcConnectorTemplateDebeziumSelectorFormatter.TableSelector(table)}:{CdcConnectorTemplateDebeziumSelectorFormatter.KeyColumnList(table, messageKeyColumns)}";
                })
        );
        config["time.precision.mode"] = "isostring";
        config["unavailable.value.placeholder"] = "__debezium_unavailable_value";
        config["poll.interval.ms"] = CdcConnectorTemplateSharedRules
            .PollIntervalMilliseconds(request)
            .ToString();
        config["snapshot.isolation.mode"] = "snapshot";

        config["schema.history.internal.kafka.bootstrap.servers"] = request
            .DeploymentPolicy
            .KafkaBootstrapServers;
        config["schema.history.internal.kafka.topic"] =
            request.SchemaHistoryTopicName
            ?? throw new InvalidOperationException(
                "CDC connector template SQL Server schema-history topic was not derived."
            );
        config["schema.history.internal.producer.enable.idempotence"] = "true";
        config["schema.history.internal.producer.acks"] = "all";
        config["schema.history.internal.producer.retries"] = int.MaxValue.ToString();
        config["schema.history.internal.producer.max.in.flight.requests.per.connection"] = "1";
        config["include.schema.changes"] = "false";

        foreach (var property in request.KafkaClientSecurityProperties.Properties)
        {
            config[$"schema.history.internal.producer.{property.Key}"] = property.Value;
            config[$"schema.history.internal.consumer.{property.Key}"] = property.Value;
        }
    }

    private static CdcProviderArtifactObservation RequiredArtifact(
        CdcConnectorTemplateRequest request,
        CdcProviderArtifactKind artifactKind,
        CdcSafeName expectedArtifactName
    )
    {
        CdcProviderArtifactObservation[] artifacts = MatchingUsableArtifacts(
            request,
            artifactKind,
            expectedArtifactName
        );
        if (artifacts.Length != 1)
        {
            throw new InvalidOperationException(
                "CDC connector template PostgreSQL provider artifact metadata was not validated against binding artifact names before rendering."
            );
        }

        return artifacts[0];
    }

    private static CdcProviderArtifactObservation[] MatchingUsableArtifacts(
        CdcConnectorTemplateRequest request,
        CdcProviderArtifactKind artifactKind,
        CdcSafeName expectedArtifactName
    ) =>
        CdcConnectorTemplateSharedRules
            .ArtifactInventory(request.ProviderSetupEvidence.Result)
            .Where(artifact =>
                artifact.ArtifactKind == artifactKind
                && artifact.SafeArtifactName.Equals(expectedArtifactName)
                && artifact.State is CdcProviderArtifactState.Created or CdcProviderArtifactState.Matched
            )
            .ToArray();

    private static int ProducerBufferBytes(CdcConnectorTemplateRequest request) =>
        request.DeploymentPolicy.ProducerBufferBytes
        ?? Math.Max(
            CdcConnectorTemplateDeploymentPolicy.MinimumProducerBufferBytes,
            request.DeploymentPolicy.MaxRecordBytes
        );

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

    private static string PartitionerClass(string partitionerAlgorithm) =>
        partitionerAlgorithm switch
        {
            CdcBindingIdentity.KafkaMurmur2V1PartitionerAlgorithm =>
                "org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner",
            _ => throw new ArgumentOutOfRangeException(
                nameof(partitionerAlgorithm),
                partitionerAlgorithm,
                "Unsupported CDC binding partitioner algorithm."
            ),
        };

    private static CdcConnectorTemplateArtifactPayload? BuildArtifactPayloadIfRequested(
        CdcConnectorTemplateRequest request,
        IReadOnlyDictionary<string, string> config,
        string configSha256
    )
    {
        if (request.ArtifactOutput is not { IncludeRedactedArtifactPayload: true } artifactOutput)
        {
            return null;
        }

        CdcSafeName fileName = ManifestFileName(request.Provider, request.ConnectorName);
        string json = SerializeManifest(request, config, configSha256);
        var payload = new CdcConnectorTemplateArtifactPayload(fileName, json);

        if (artifactOutput.ManifestOutputDirectoryPath is not null)
        {
            Directory.CreateDirectory(artifactOutput.ManifestOutputDirectoryPath);
            File.WriteAllText(
                Path.Combine(artifactOutput.ManifestOutputDirectoryPath, fileName.Value),
                json,
                Utf8NoBom
            );
        }

        return payload;
    }

    private static CdcSafeName ManifestFileName(CdcProvider provider, CdcSafeName connectorName)
    {
        string prefix = $"cdc-connector-template.{ProviderToken(provider)}.";
        const string suffix = ".manifest.json";
        string fullFileName = $"{prefix}{connectorName.Value}{suffix}";

        if (fullFileName.Length <= ManifestFileNameMaxLength)
        {
            return new CdcSafeName(fullFileName);
        }

        string connectorHashToken =
            $"{ManifestFileNameConnectorHashPrefix}{ComputeSha256Prefix(connectorName.Value)}";
        int maxConnectorPrefixLength =
            ManifestFileNameMaxLength - prefix.Length - connectorHashToken.Length - suffix.Length;

        if (maxConnectorPrefixLength <= 0)
        {
            throw new InvalidOperationException(
                "CDC connector template manifest filename budget cannot fit provider token and connector hash."
            );
        }

        return new CdcSafeName(
            $"{prefix}{connectorName.Value[..maxConnectorPrefixLength]}{connectorHashToken}{suffix}"
        );
    }

    private static string ComputeSha256Prefix(string value)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..ManifestFileNameConnectorHashLength].ToLowerInvariant();
    }

    private static string SerializeManifest(
        CdcConnectorTemplateRequest request,
        IReadOnlyDictionary<string, string> config,
        string configSha256
    )
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("provider", ProviderToken(request.Provider));
            writer.WriteString("connectorName", request.ConnectorName.Value);
            writer.WriteString("publicTopicName", request.PublicTopicName);
            writer.WriteString("progressTopicName", request.ProgressTopicName);

            if (request.SchemaHistoryTopicName is null)
            {
                writer.WriteNull("schemaHistoryTopicName");
            }
            else
            {
                writer.WriteString("schemaHistoryTopicName", request.SchemaHistoryTopicName);
            }

            writer.WriteString("configSha256", configSha256);
            writer.WritePropertyName("redactedConfig");
            WriteStringMap(writer, BuildRedactedConfig(config));
            writer.WritePropertyName("reservedKeys");
            WriteStringArray(writer, CdcConnectorTemplateInputValidator.ReservedManifestKeys);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IReadOnlyDictionary<string, string> BuildRedactedConfig(
        IReadOnlyDictionary<string, string> config
    )
    {
        var redactedConfig = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in config)
        {
            redactedConfig[property.Key] = RedactArtifactValue(property.Key, property.Value);
        }

        return redactedConfig;
    }

    private static string RedactArtifactValue(string propertyName, string value) =>
        IsArtifactRedactedProperty(propertyName) ? RedactedArtifactValue : value;

    private static bool IsArtifactRedactedProperty(string propertyName) =>
        propertyName.StartsWith("database.", StringComparison.Ordinal)
        || propertyName.StartsWith("driver.", StringComparison.Ordinal)
        || propertyName == "heartbeat.action.query"
        || propertyName == "schema.history.internal.kafka.bootstrap.servers"
        || CdcConnectorTemplateInputValidator.IsSecretBearingRenderedProperty(propertyName)
        || CdcConnectorTemplateInputValidator.IsKafkaSecurityMaterialRenderedProperty(propertyName);

    private static void WriteStringMap(Utf8JsonWriter writer, IReadOnlyDictionary<string, string> properties)
    {
        writer.WriteStartObject();
        foreach (var property in properties.OrderBy(property => property.Key, StringComparer.Ordinal))
        {
            writer.WriteString(property.Key, property.Value);
        }
        writer.WriteEndObject();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, IReadOnlyList<string> values)
    {
        writer.WriteStartArray();
        foreach (string value in values.OrderBy(value => value, StringComparer.Ordinal))
        {
            writer.WriteStringValue(value);
        }
        writer.WriteEndArray();
    }

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
