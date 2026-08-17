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
    private const int DefaultHeartbeatIntervalMilliseconds = 5000;
    private const int DefaultProducerBufferBytes = 33_554_432;

    public CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        CdcConnectorTemplateValidationResult validationResult = inputValidator.ValidateRequest(
            request,
            CdcConnectorTemplateSourcePhase.Rendering
        );
        if (!validationResult.IsValid)
        {
            return new CdcConnectorTemplateResult(
                request.BindingIdentity,
                CdcConnectorTemplateOutcome.ValidationFailed,
                new SortedDictionary<string, string>(StringComparer.Ordinal),
                registrationPayload: null,
                redactedArtifactPayload: null,
                configSha256: null,
                validationResult.Diagnostics
            );
        }

        IReadOnlyDictionary<string, string> config = BuildCommonConfig(request);
        var registrationPayload = new CdcKafkaConnectRegistrationPayload(request.ConnectorName, config);

        return new CdcConnectorTemplateResult(
            request.BindingIdentity,
            CdcConnectorTemplateOutcome.Rendered,
            config,
            registrationPayload,
            redactedArtifactPayload: null,
            ComputeCanonicalConfigSha256(config),
            validationResult.Diagnostics
        );
    }

    private static IReadOnlyDictionary<string, string> BuildCommonConfig(CdcConnectorTemplateRequest request)
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

        return config;
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
