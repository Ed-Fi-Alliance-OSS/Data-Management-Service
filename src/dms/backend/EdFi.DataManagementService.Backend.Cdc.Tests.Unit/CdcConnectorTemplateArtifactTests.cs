// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateArtifacts")]
public class Given_CdcConnectorTemplateArtifacts
{
    private static readonly CdcSourceFingerprint SourceFingerprint = new(
        "cdc-source-fingerprint-v1",
        "physical-source-fingerprint"
    );

    [Test]
    public void It_returns_a_deterministic_postgresql_redacted_manifest_payload_when_requested()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                new CdcConnectorTemplateArtifactOutputRequest(includeRedactedArtifactPayload: true),
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
                new CdcConnectorTemplateArtifactOutputRequest(
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
                new CdcConnectorTemplateArtifactOutputRequest(includeRedactedArtifactPayload: true),
                providerConnectionProperties: BuildPostgresqlConnectionProperties(
                    "${env:CDC_DATABASE_PASSWORD_A}"
                )
            )
        );
        CdcConnectorTemplateResult second = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                new CdcConnectorTemplateArtifactOutputRequest(includeRedactedArtifactPayload: true),
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

    private static CdcConnectorTemplateRequest BuildRequest(
        CdcProvider provider,
        CdcConnectorTemplateArtifactOutputRequest? artifactOutput,
        IReadOnlyDictionary<string, string>? providerConnectionProperties = null,
        IReadOnlyDictionary<string, string>? kafkaSecurityProperties = null,
        string heartbeatSql = "select 1"
    ) =>
        new(
            BuildBinding(provider),
            new CdcConnectorProviderSetupEvidence(
                bindingGeneration: 7,
                BuildProviderSetupResult(provider, heartbeatSql)
            ),
            new CdcConnectorTemplateDeploymentPolicy(
                "broker-1:9092,broker-2:9092",
                maxRecordBytes: 67_108_864
            ),
            new CdcProviderConnectionProperties(
                provider,
                providerConnectionProperties ?? BuildProviderConnectionProperties(provider)
            ),
            new CdcKafkaClientSecurityProperties(kafkaSecurityProperties ?? new Dictionary<string, string>()),
            artifactOutput
        );

    private static CdcConnectorTemplateBindingIdentity BuildBinding(CdcProvider provider) =>
        new(
            provider,
            new CdcSafeName("dms_binding_connector"),
            "edfi.documents",
            bindingGeneration: 7,
            SourceFingerprint
        );

    private static CdcProviderSetupResult BuildProviderSetupResult(
        CdcProvider provider,
        string heartbeatSql
    ) =>
        new(
            Provider: provider,
            Mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
            Outcome: CdcProviderSetupOutcome.CreatedOrMatched,
            BoundPhysicalSourceFingerprint: SourceFingerprint,
            ObservedSourceFingerprint: SourceFingerprint,
            ArtifactInventory: BuildArtifactInventory(provider),
            GrantInventory: [],
            SourceTableInventory: BuildRequiredSourceTableInventory(provider),
            ExpectedMessageKeyColumns: BuildExpectedMessageKeyColumns(),
            HeartbeatActionQuery: new CdcHeartbeatActionQuery(heartbeatSql, "sha256-safe"),
            ProviderHistoryObservations: [],
            ManifestPayload: null,
            Diagnostics: []
        );

    private static IReadOnlyDictionary<string, string> BuildProviderConnectionProperties(
        CdcProvider provider
    ) =>
        provider == CdcProvider.Postgresql
            ? BuildPostgresqlConnectionProperties("${env:CDC_DATABASE_PASSWORD}")
            : new Dictionary<string, string>
            {
                ["database.hostname"] = "sqlserver.internal",
                ["database.port"] = "1433",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.names"] = "edfi_datastore",
            };

    private static IReadOnlyDictionary<string, string> BuildPostgresqlConnectionProperties(
        string passwordReference
    ) =>
        new Dictionary<string, string>
        {
            ["database.hostname"] = "postgresql.internal",
            ["database.port"] = "5432",
            ["database.user"] = "connector_user",
            ["database.password"] = passwordReference,
            ["database.dbname"] = "edfi_datastore",
        };

    private static IReadOnlyList<CdcProviderArtifactObservation> BuildArtifactInventory(
        CdcProvider provider
    ) =>
        provider switch
        {
            CdcProvider.Postgresql =>
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
            ],
            CdcProvider.SqlServer =>
            [
                new(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    new CdcSafeName("dms_binding_document_cache_capture"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
                new(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    new CdcSafeName("dms_binding_document_capture"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
                new(
                    CdcProviderArtifactKind.SqlServerCaptureInstance,
                    new CdcSafeName("dms_binding_cdc_heartbeat_capture"),
                    CdcProviderArtifactState.Matched,
                    new Dictionary<string, string>()
                ),
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static IReadOnlyList<CdcSourceTableInventory> BuildRequiredSourceTableInventory(
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

    private static CdcSourceTableInventory BuildSourceTable(
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

    private static CdcSourceColumnInventory BuildColumn(
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

    private static IReadOnlyList<CdcExpectedMessageKeyColumns> BuildExpectedMessageKeyColumns() =>
        [
            new(CdcSourceTableKind.DocumentCache, [new DbColumnName("DocumentUuid")]),
            new(CdcSourceTableKind.Document, [new DbColumnName("DocumentUuid")]),
        ];
}
