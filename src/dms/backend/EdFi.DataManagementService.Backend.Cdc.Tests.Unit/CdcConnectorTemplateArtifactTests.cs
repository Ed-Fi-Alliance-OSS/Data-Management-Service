// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateArtifacts")]
public class Given_CdcConnectorTemplateArtifacts
{
    [Test]
    public void It_returns_a_deterministic_postgresql_redacted_manifest_payload_when_requested()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                    includeRedactedArtifactPayload: true
                ),
                providerConnectionProperties: new Dictionary<string, string>
                {
                    ["database.hostname"] = "postgresql-prod.internal",
                    ["database.port"] = "5432",
                    ["database.user"] = "connector_principal",
                    ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                    ["database.dbname"] = "edfi_sensitive_store",
                },
                kafkaSecurityProperties: new Dictionary<string, string>
                {
                    ["security.protocol"] = "SASL_SSL",
                    ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS_CONFIG}",
                    ["ssl.truststore.location"] = "/run/secrets/kafka.truststore.p12",
                },
                heartbeatSql: "update dms.CdcHeartbeat set HeartbeatSequence = HeartbeatSequence + 1"
            )
        );

        result.RedactedArtifactPayload.Should().NotBeNull();
        CdcConnectorTemplateArtifactPayload payload = result.RedactedArtifactPayload!;
        using JsonDocument document = JsonDocument.Parse(payload.Json);
        JsonElement root = document.RootElement;
        JsonElement redactedConfig = root.GetProperty("redactedConfig");

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        payload.FileName.Should().Be(new CdcSafeName("cdc-connector-template.postgresql.manifest.json"));
        root.EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .Equal(
                "version",
                "provider",
                "connectorName",
                "publicTopicName",
                "progressTopicName",
                "schemaHistoryTopicName",
                "configSha256",
                "redactedConfig",
                "reservedKeys"
            );
        root.GetProperty("version").GetInt32().Should().Be(1);
        root.GetProperty("provider").GetString().Should().Be("postgresql");
        root.GetProperty("connectorName").GetString().Should().Be("dms_binding_connector");
        root.GetProperty("publicTopicName").GetString().Should().Be("edfi.documents");
        root.GetProperty("progressTopicName").GetString().Should().Be("edfi.documents.cdc-progress");
        root.GetProperty("schemaHistoryTopicName").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("configSha256").GetString().Should().Be(result.ConfigSha256);
        root.TryGetProperty("generatedAt", out JsonElement generatedAt).Should().BeFalse();
        generatedAt.ValueKind.Should().Be(JsonValueKind.Undefined);
        redactedConfig
            .EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .BeInAscendingOrder(StringComparer.Ordinal);
        redactedConfig
            .GetProperty("connector.class")
            .GetString()
            .Should()
            .Be(result.Config["connector.class"]);
        redactedConfig.GetProperty("publication.name").GetString().Should().Be("dms_binding_publication");
        redactedConfig.GetProperty("database.hostname").GetString().Should().Be("[redacted]");
        redactedConfig.GetProperty("database.dbname").GetString().Should().Be("[redacted]");
        redactedConfig.GetProperty("database.user").GetString().Should().Be("[redacted]");
        redactedConfig.GetProperty("database.password").GetString().Should().Be("[redacted]");
        redactedConfig.GetProperty("heartbeat.action.query").GetString().Should().Be("[redacted]");
        redactedConfig.GetProperty("producer.override.security.protocol").GetString().Should().Be("SASL_SSL");
        redactedConfig
            .GetProperty("producer.override.sasl.jaas.config")
            .GetString()
            .Should()
            .Be("[redacted]");
        redactedConfig
            .GetProperty("producer.override.ssl.truststore.location")
            .GetString()
            .Should()
            .Be("[redacted]");
        root.GetProperty("reservedKeys")
            .EnumerateArray()
            .Select(element => element.GetString())
            .Should()
            .Contain(["connector.class", "producer.override.*", "schema.history.*"]);
        payload.Json.Should().NotContain("${env:CDC_DATABASE_PASSWORD}");
        payload.Json.Should().NotContain("${env:CDC_KAFKA_JAAS_CONFIG}");
        payload.Json.Should().NotContain("postgresql-prod.internal");
        payload.Json.Should().NotContain("edfi_sensitive_store");
        payload.Json.Should().NotContain("connector_principal");
        payload.Json.Should().NotContain("HeartbeatSequence");
    }

    [Test]
    public void It_writes_the_sqlserver_manifest_file_when_an_output_directory_is_requested()
    {
        string artifactDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"cdc-connector-template-artifacts-{Guid.NewGuid():N}"
        );

        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.SqlServer,
                artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                    includeRedactedArtifactPayload: false,
                    manifestOutputDirectoryPath: artifactDirectory
                ),
                kafkaSecurityProperties: new Dictionary<string, string>
                {
                    ["security.protocol"] = "SASL_SSL",
                    ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS_CONFIG}",
                }
            )
        );

        result.RedactedArtifactPayload.Should().NotBeNull();
        CdcConnectorTemplateArtifactPayload payload = result.RedactedArtifactPayload!;
        string manifestPath = Path.Combine(artifactDirectory, payload.FileName.Value);
        using JsonDocument document = JsonDocument.Parse(payload.Json);
        JsonElement root = document.RootElement;
        JsonElement redactedConfig = root.GetProperty("redactedConfig");

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        payload.FileName.Should().Be(new CdcSafeName("cdc-connector-template.sqlserver.manifest.json"));
        File.Exists(manifestPath).Should().BeTrue();
        File.ReadAllText(manifestPath).Should().Be(payload.Json);
        root.GetProperty("provider").GetString().Should().Be("sqlserver");
        root.GetProperty("schemaHistoryTopicName").GetString().Should().Be("edfi.documents.schema-history");
        redactedConfig
            .GetProperty("schema.history.internal.kafka.bootstrap.servers")
            .GetString()
            .Should()
            .Be("[redacted]");
        redactedConfig
            .GetProperty("schema.history.internal.producer.security.protocol")
            .GetString()
            .Should()
            .Be("SASL_SSL");
        redactedConfig
            .GetProperty("schema.history.internal.producer.sasl.jaas.config")
            .GetString()
            .Should()
            .Be("[redacted]");
        redactedConfig
            .GetProperty("schema.history.internal.consumer.sasl.jaas.config")
            .GetString()
            .Should()
            .Be("[redacted]");
    }

    [Test]
    public void It_keeps_artifact_output_optional()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(CdcProvider.Postgresql, artifactOutput: null)
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.ConfigSha256.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        result.RedactedArtifactPayload.Should().BeNull();
    }

    [Test]
    public void It_hashes_unredacted_config_before_redacting_artifact_secrets()
    {
        CdcConnectorTemplateResult first = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                    includeRedactedArtifactPayload: true
                ),
                providerConnectionProperties: BuildPostgresqlConnectionProperties(
                    "${env:CDC_DATABASE_PASSWORD_A}"
                )
            )
        );
        CdcConnectorTemplateResult second = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                    includeRedactedArtifactPayload: true
                ),
                providerConnectionProperties: BuildPostgresqlConnectionProperties(
                    "${env:CDC_DATABASE_PASSWORD_B}"
                )
            )
        );

        using var firstDocument = JsonDocument.Parse(first.RedactedArtifactPayload!.Json);
        using var secondDocument = JsonDocument.Parse(second.RedactedArtifactPayload!.Json);

        using var _ = new AssertionScope();
        first.ConfigSha256.Should().NotBe(second.ConfigSha256);
        first.RedactedArtifactPayload.Json.Should().NotContain("${env:CDC_DATABASE_PASSWORD_A}");
        second.RedactedArtifactPayload.Json.Should().NotContain("${env:CDC_DATABASE_PASSWORD_B}");
        firstDocument
            .RootElement.GetProperty("redactedConfig")
            .GetProperty("database.password")
            .GetString()
            .Should()
            .Be("[redacted]");
        secondDocument
            .RootElement.GetProperty("redactedConfig")
            .GetProperty("database.password")
            .GetString()
            .Should()
            .Be("[redacted]");
    }

    private static CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request)
    {
        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddCdcConnectorTemplates()
            .BuildServiceProvider();

        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        return service.Render(request);
    }
}
