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
[Category("CdcConnectorTemplateRenderingCommon")]
public class Given_CdcConnectorTemplateCommonRendering
{
    private static readonly CdcSourceFingerprint SourceFingerprint = new(
        "cdc-source-fingerprint-v1",
        "physical-source-fingerprint"
    );

    [Test]
    public void It_renders_the_common_postgresql_connector_contract()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                new CdcConnectorTemplateDeploymentPolicy(
                    "broker-1:9092,broker-2:9092",
                    maxRecordBytes: 67_108_864
                ),
                new Dictionary<string, string>
                {
                    ["database.hostname"] = "postgresql.internal",
                    ["database.user"] = "connector_user",
                    ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                    ["database.dbname"] = "edfi_datastore",
                },
                new Dictionary<string, string>
                {
                    ["security.protocol"] = "SASL_SSL",
                    ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS_CONFIG}",
                }
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.RegistrationPayload.Should().NotBeNull();
        result.RegistrationPayload!.Name.Should().Be("dms_binding_connector");
        result.RegistrationPayload.Config.Should().Equal(result.Config);
        result.ConfigSha256.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        result.Diagnostics.Should().BeEmpty();
        result.Config.Keys.Should().BeInAscendingOrder(StringComparer.Ordinal);
        result
            .Config.Should()
            .Contain(
                new KeyValuePair<string, string>(
                    "connector.class",
                    "io.debezium.connector.postgresql.PostgresConnector"
                )
            )
            .And.Contain("name", "dms_binding_connector")
            .And.Contain("tasks.max", "1")
            .And.Contain("topic.prefix", "dms_binding_connector")
            .And.Contain("transforms", "documentState")
            .And.Contain("transforms.documentState.type", "org.edfi.kafka.connect.transforms.DocumentState")
            .And.Contain("transforms.documentState.provider", "postgresql")
            .And.Contain("transforms.documentState.target.topic", "edfi.documents")
            .And.Contain("transforms.documentState.progress.topic", "edfi.documents.cdc-progress")
            .And.Contain("key.converter", "org.apache.kafka.connect.storage.StringConverter")
            .And.Contain("value.converter", "org.edfi.kafka.connect.converters.DocumentStateJsonConverter")
            .And.Contain("value.converter.schemas.enable", "false")
            .And.Contain("value.converter.decimal.format", "NUMERIC")
            .And.Contain("tombstones.on.delete", "false")
            .And.Contain("errors.tolerance", "none")
            .And.Contain("statistics.metrics.enabled", "true")
            .And.Contain("snapshot.mode", "initial")
            .And.Contain("producer.override.enable.idempotence", "true")
            .And.Contain("producer.override.acks", "all")
            .And.Contain("producer.override.retries", "2147483647")
            .And.Contain("producer.override.max.in.flight.requests.per.connection", "5")
            .And.Contain("producer.override.max.request.size", "67108864")
            .And.Contain("producer.override.buffer.memory", "67108864")
            .And.Contain("producer.override.compression.type", "none")
            .And.Contain(
                "producer.override.partitioner.class",
                "org.edfi.kafka.connect.partitioner.KafkaMurmur2V1Partitioner"
            )
            .And.Contain("heartbeat.interval.ms", "5000")
            .And.Contain("heartbeat.action.query", "select 1")
            .And.Contain("topic.delimiter", ".")
            .And.Contain("topic.naming.strategy", "io.debezium.schema.SchemaTopicNamingStrategy")
            .And.Contain("topic.heartbeat.prefix", "__debezium-heartbeat")
            .And.Contain("database.hostname", "postgresql.internal")
            .And.Contain("database.password", "${env:CDC_DATABASE_PASSWORD}")
            .And.Contain("producer.override.security.protocol", "SASL_SSL")
            .And.Contain("producer.override.sasl.jaas.config", "${env:CDC_KAFKA_JAAS_CONFIG}");
        result.Config.Should().NotContainKey("security.protocol");
        result.Config.Should().NotContainKey("sasl.jaas.config");
        result.Config.Should().NotContainKey("topic.heartbeat.name");
        result
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("topic.creation.", StringComparison.Ordinal));
        result
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("errors.deadletterqueue.", StringComparison.Ordinal));
        result
            .Config.Keys.Should()
            .NotContain(key => key.Contains("offset.storage", StringComparison.Ordinal));
        result.Config.Keys.Should().NotContain(key => key.Contains("ACL", StringComparison.Ordinal));
    }

    [Test]
    public void It_serializes_the_registration_payload_to_the_kafka_connect_rest_shape()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                new CdcConnectorTemplateDeploymentPolicy(
                    "broker-1:9092,broker-2:9092",
                    maxRecordBytes: 1_048_576
                ),
                new Dictionary<string, string>
                {
                    ["database.hostname"] = "postgresql.internal",
                    ["database.user"] = "connector_user",
                    ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                    ["database.dbname"] = "edfi_datastore",
                }
            )
        );

        string json = JsonSerializer.Serialize(result.RegistrationPayload);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Dictionary<string, string> serializedConfig = root.GetProperty("config")
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!);

        using var _ = new AssertionScope();
        root.EnumerateObject().Select(property => property.Name).Should().Equal("name", "config");
        root.GetProperty("name").ValueKind.Should().Be(JsonValueKind.String);
        root.GetProperty("name").GetString().Should().Be("dms_binding_connector");
        serializedConfig.Should().Equal(result.Config);
    }

    [Test]
    public void It_renders_the_common_sqlserver_connector_contract()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.SqlServer,
                new CdcConnectorTemplateDeploymentPolicy(
                    "broker:9092",
                    maxRecordBytes: 1_048_576,
                    producerBufferBytes: 67_108_864,
                    heartbeatInterval: TimeSpan.FromSeconds(12)
                ),
                new Dictionary<string, string>
                {
                    ["database.hostname"] = "sqlserver.internal",
                    ["database.user"] = "connector_user",
                    ["database.names"] = "edfi_datastore",
                    ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                },
                new Dictionary<string, string> { ["security.protocol"] = "SSL" }
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.SchemaHistoryTopicName.Should().Be("edfi.documents.schema-history");
        result
            .Config.Should()
            .Contain("connector.class", "io.debezium.connector.sqlserver.SqlServerConnector");
        result.Config.Should().Contain("transforms.documentState.provider", "sqlserver");
        result.Config.Should().Contain("producer.override.buffer.memory", "67108864");
        result.Config.Should().Contain("heartbeat.interval.ms", "12000");
        result.Config.Should().Contain("database.names", "edfi_datastore");
        result.Config.Should().Contain("producer.override.security.protocol", "SSL");
    }

    [Test]
    public void It_returns_validation_diagnostics_without_rendering_when_reserved_inputs_are_supplied()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                new CdcConnectorTemplateDeploymentPolicy("broker:9092", maxRecordBytes: 1_048_576),
                new Dictionary<string, string>
                {
                    ["database.hostname"] = "postgresql.internal",
                    ["database.user"] = "connector_user",
                    ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                    ["database.dbname"] = "edfi_datastore",
                    ["producer.override.acks"] = "all",
                }
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        result.RegistrationPayload.Should().BeNull();
        result.ConfigSha256.Should().BeNull();
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.ReservedKey
                && diagnostic.PropertyName == "producer.override.acks"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Rendering
            );
    }

    [Test]
    public void It_computes_a_stable_canonical_hash_from_the_rendered_config()
    {
        CdcConnectorTemplateResult first = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                new CdcConnectorTemplateDeploymentPolicy("broker:9092", maxRecordBytes: 1_048_576),
                kafkaSecurityProperties: new Dictionary<string, string>
                {
                    ["security.protocol"] = "SASL_SSL",
                    ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS_CONFIG}",
                }
            )
        );
        CdcConnectorTemplateResult sameWithDifferentInputOrder = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                new CdcConnectorTemplateDeploymentPolicy("broker:9092", maxRecordBytes: 1_048_576),
                kafkaSecurityProperties: new Dictionary<string, string>
                {
                    ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS_CONFIG}",
                    ["security.protocol"] = "SASL_SSL",
                }
            )
        );
        CdcConnectorTemplateResult changedSecretReference = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                new CdcConnectorTemplateDeploymentPolicy("broker:9092", maxRecordBytes: 1_048_576),
                kafkaSecurityProperties: new Dictionary<string, string>
                {
                    ["security.protocol"] = "SASL_SSL",
                    ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS_CONFIG_ROTATED}",
                }
            )
        );

        using var _ = new AssertionScope();
        first.Config.Should().Equal(sameWithDifferentInputOrder.Config);
        first.ConfigSha256.Should().Be(sameWithDifferentInputOrder.ConfigSha256);
        first.ConfigSha256.Should().NotBe(changedSecretReference.ConfigSha256);
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
        CdcConnectorTemplateDeploymentPolicy deploymentPolicy,
        IReadOnlyDictionary<string, string>? providerConnectionProperties = null,
        IReadOnlyDictionary<string, string>? kafkaSecurityProperties = null
    ) =>
        new(
            BuildBinding(provider),
            new CdcConnectorProviderSetupEvidence(bindingGeneration: 7, BuildProviderSetupResult(provider)),
            deploymentPolicy,
            new CdcProviderConnectionProperties(
                provider,
                providerConnectionProperties ?? BuildProviderConnectionProperties(provider)
            ),
            new CdcKafkaClientSecurityProperties(kafkaSecurityProperties ?? new Dictionary<string, string>())
        );

    private static IReadOnlyDictionary<string, string> BuildProviderConnectionProperties(
        CdcProvider provider
    ) =>
        provider == CdcProvider.Postgresql
            ? new Dictionary<string, string>
            {
                ["database.hostname"] = "postgresql.internal",
                ["database.port"] = "5432",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.dbname"] = "edfi_datastore",
            }
            : new Dictionary<string, string>
            {
                ["database.hostname"] = "sqlserver.internal",
                ["database.port"] = "1433",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.names"] = "edfi_datastore",
            };

    private static CdcConnectorTemplateBindingIdentity BuildBinding(CdcProvider provider) =>
        new(
            provider,
            new CdcSafeName("dms_binding_connector"),
            "edfi.documents",
            bindingGeneration: 7,
            partitionerAlgorithm: "kafka-murmur2-v1",
            SourceFingerprint
        );

    private static CdcProviderSetupResult BuildProviderSetupResult(CdcProvider provider) =>
        new(
            Provider: provider,
            Mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
            Outcome: CdcProviderSetupOutcome.CreatedOrMatched,
            BoundPhysicalSourceFingerprint: SourceFingerprint,
            ObservedSourceFingerprint: SourceFingerprint,
            ArtifactInventory: BuildArtifactInventory(provider),
            GrantInventory: [],
            SourceTableInventory: BuildRequiredSourceTableInventory(),
            ExpectedMessageKeyColumns: BuildExpectedMessageKeyColumns(),
            HeartbeatActionQuery: new CdcHeartbeatActionQuery("select 1", "sha256-safe"),
            ProviderHistoryObservations: [],
            ManifestPayload: null,
            Diagnostics: []
        );

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
            CdcProvider.SqlServer => [],
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static IReadOnlyList<CdcSourceTableInventory> BuildRequiredSourceTableInventory() =>
        [
            BuildSourceTable(
                CdcSourceTableKind.DocumentCache,
                "DocumentCache",
                [BuildColumn("DocumentUuid")]
            ),
            BuildSourceTable(CdcSourceTableKind.Document, "Document", [BuildColumn("DocumentUuid")]),
            BuildSourceTable(
                CdcSourceTableKind.CdcHeartbeat,
                "CdcHeartbeat",
                [
                    BuildColumn("HeartbeatId"),
                    BuildColumn("HeartbeatSequence", 2),
                    BuildColumn("HeartbeatAt", 3),
                ]
            ),
        ];

    private static CdcSourceTableInventory BuildSourceTable(
        CdcSourceTableKind tableKind,
        string tableName,
        IReadOnlyList<CdcSourceColumnInventory> columns
    ) =>
        new(
            tableKind,
            new DbTableName(new DbSchemaName("dms"), tableName),
            $"\"dms\".\"{tableName}\"",
            columns
        );

    private static CdcSourceColumnInventory BuildColumn(string columnName, int ordinal = 1) =>
        new(new DbColumnName(columnName), $"\"{columnName}\"", ordinal, "text", IsNullable: false);

    private static IReadOnlyList<CdcExpectedMessageKeyColumns> BuildExpectedMessageKeyColumns() =>
        [
            new(CdcSourceTableKind.DocumentCache, [new DbColumnName("DocumentUuid")]),
            new(CdcSourceTableKind.Document, [new DbColumnName("DocumentUuid")]),
        ];
}
