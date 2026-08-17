// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateRenderingPostgresql")]
public class Given_CdcConnectorTemplatePostgresqlRendering
{
    private static readonly CdcSourceFingerprint SourceFingerprint = new(
        "cdc-source-fingerprint-v1",
        "physical-source-fingerprint"
    );

    [Test]
    public void It_renders_the_postgresql_connector_contract_from_provider_setup_metadata()
    {
        CdcConnectorTemplateResult result = Render(BuildRequest());

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.SchemaHistoryTopicName.Should().BeNull();
        result
            .Config.Should()
            .Contain("connector.class", "io.debezium.connector.postgresql.PostgresConnector");
        result.Config.Should().Contain("plugin.name", "pgoutput");
        result.Config.Should().Contain("publication.autocreate.mode", "disabled");
        result.Config.Should().Contain("publication.name", "dms_binding_publication");
        result.Config.Should().Contain("slot.name", "dms_binding_slot");
        result
            .Config.Should()
            .Contain("table.include.list", "dms.DocumentCache,dms.Document,dms.CdcHeartbeat");
        result
            .Config.Should()
            .Contain("message.key.columns", "dms.DocumentCache:DocumentUuid;dms.Document:DocumentUuid");
        result.Config.Should().Contain("unavailable.value.placeholder", "__debezium_unavailable_value");
        result.Config.Should().NotContainKey("slot.drop.on.stop");
        result.Config.Should().NotContainKey("topic.creation.default.replication.factor");
        result.Config.Should().NotContainKey("schema.history.internal.kafka.topic");
        result
            .Config["table.include.list"]
            .Should()
            .NotContain("DocumentProjectionWork", because: "work-table capture is outside the contract");
        result
            .Config["table.include.list"]
            .Should()
            .NotContain("\"", because: "Debezium selectors are not SQL quoted identifiers");
        result
            .Config["message.key.columns"]
            .Should()
            .NotContain("CdcHeartbeat", because: "heartbeat rows use the transform progress key");
        result
            .Config["message.key.columns"]
            .Should()
            .NotContain("\"", because: "Debezium selectors are not SQL quoted identifiers");
    }

    [Test]
    public void It_orders_the_include_list_and_message_keys_by_the_contract_not_input_order()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                sourceTableInventory:
                [
                    BuildSourceTable(
                        CdcSourceTableKind.CdcHeartbeat,
                        "CdcHeartbeat",
                        [
                            BuildColumn("HeartbeatId"),
                            BuildColumn("HeartbeatSequence", 2),
                            BuildColumn("HeartbeatAt", 3),
                        ]
                    ),
                    BuildSourceTable(CdcSourceTableKind.Document, "Document", [BuildColumn("DocumentUuid")]),
                    BuildSourceTable(
                        CdcSourceTableKind.DocumentCache,
                        "DocumentCache",
                        [BuildColumn("DocumentUuid")]
                    ),
                ],
                expectedMessageKeyColumns:
                [
                    new(CdcSourceTableKind.Document, [new DbColumnName("DocumentUuid")]),
                    new(CdcSourceTableKind.DocumentCache, [new DbColumnName("DocumentUuid")]),
                ]
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Config["table.include.list"].Should().Be("dms.DocumentCache,dms.Document,dms.CdcHeartbeat");
        result
            .Config["message.key.columns"]
            .Should()
            .Be("dms.DocumentCache:DocumentUuid;dms.Document:DocumentUuid");
    }

    [Test]
    public void It_returns_diagnostics_without_rendering_when_publication_or_slot_metadata_is_missing()
    {
        CdcConnectorTemplateResult result = Render(BuildRequest(artifactInventory: []));

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        result.ConfigSha256.Should().BeNull();
        result
            .Diagnostics.Select(diagnostic => diagnostic.Code)
            .Should()
            .BeEquivalentTo(
                CdcConnectorTemplateDiagnosticCodes.PostgresqlPublicationMetadataRequired,
                CdcConnectorTemplateDiagnosticCodes.PostgresqlReplicationSlotMetadataRequired
            );
        result
            .Diagnostics.Should()
            .OnlyContain(diagnostic =>
                diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Rendering
            );
    }

    [Test]
    public void It_rejects_work_table_capture_from_the_provider_setup_source_inventory()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                sourceTableInventory:
                [
                    BuildSourceTable(
                        CdcSourceTableKind.DocumentCache,
                        "DocumentCache",
                        [BuildColumn("DocumentUuid")]
                    ),
                    BuildSourceTable(
                        CdcSourceTableKind.Document,
                        "DocumentProjectionWork;DROP TABLE",
                        [BuildColumn("DocumentUuid")]
                    ),
                    BuildSourceTable(
                        CdcSourceTableKind.CdcHeartbeat,
                        "CdcHeartbeat",
                        [
                            BuildColumn("HeartbeatId"),
                            BuildColumn("HeartbeatSequence", 2),
                            BuildColumn("HeartbeatAt", 3),
                        ]
                    ),
                ]
            )
        );

        CdcConnectorTemplateDiagnostic diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch
            )
            .Subject;

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.IncludeList);
        diagnostic.PropertyName.Should().Be("table.include.list");
        diagnostic.ExpectedValue.Should().Be("dms.Document");
        diagnostic.ObservedValue.Should().Be("dms.DocumentProjectionWork_DROP_TABLE");
        diagnostic
            .ObservedValue.Should()
            .NotContain(";", because: "physical identifiers are sanitized in diagnostics");
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
    }

    [Test]
    public void It_rejects_caller_supplied_postgresql_connector_properties()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                providerConnectionProperties: new Dictionary<string, string>
                {
                    ["database.hostname"] = "postgresql.internal",
                    ["database.dbname"] = "edfi_datastore",
                    ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                    ["publication.name"] = "dms_binding_publication",
                    ["table.include.list"] = "dms.DocumentCache,dms.Document,dms.CdcHeartbeat",
                }
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        result
            .Diagnostics.Select(diagnostic => diagnostic.PropertyName)
            .Should()
            .BeEquivalentTo("publication.name", "table.include.list");
        result
            .Diagnostics.Should()
            .OnlyContain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.ReservedKey
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ReservedKey
            );
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
        IReadOnlyList<CdcProviderArtifactObservation>? artifactInventory = null,
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory = null,
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedMessageKeyColumns = null,
        IReadOnlyDictionary<string, string>? providerConnectionProperties = null
    ) =>
        new(
            BuildBinding(),
            new CdcConnectorProviderSetupEvidence(
                bindingGeneration: 7,
                BuildProviderSetupResult(artifactInventory, sourceTableInventory, expectedMessageKeyColumns)
            ),
            new CdcConnectorTemplateDeploymentPolicy(
                "broker-1:9092,broker-2:9092",
                maxRecordBytes: 67_108_864
            ),
            new CdcProviderConnectionProperties(
                CdcProvider.Postgresql,
                providerConnectionProperties
                    ?? new Dictionary<string, string>
                    {
                        ["database.hostname"] = "postgresql.internal",
                        ["database.port"] = "5432",
                        ["database.user"] = "connector_user",
                        ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                        ["database.dbname"] = "edfi_datastore",
                    }
            ),
            CdcKafkaClientSecurityProperties.Empty
        );

    private static CdcConnectorTemplateBindingIdentity BuildBinding() =>
        new(
            CdcProvider.Postgresql,
            new CdcSafeName("dms_binding_connector"),
            "edfi.documents",
            bindingGeneration: 7,
            partitionerAlgorithm: "kafka-murmur2-v1",
            SourceFingerprint
        );

    private static CdcProviderSetupResult BuildProviderSetupResult(
        IReadOnlyList<CdcProviderArtifactObservation>? artifactInventory,
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory,
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedMessageKeyColumns
    ) =>
        new(
            Provider: CdcProvider.Postgresql,
            Mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
            Outcome: CdcProviderSetupOutcome.CreatedOrMatched,
            BoundPhysicalSourceFingerprint: SourceFingerprint,
            ObservedSourceFingerprint: SourceFingerprint,
            ArtifactInventory: artifactInventory ?? BuildPostgresqlArtifactInventory(),
            GrantInventory: [],
            SourceTableInventory: sourceTableInventory ?? BuildRequiredSourceTableInventory(),
            ExpectedMessageKeyColumns: expectedMessageKeyColumns ?? BuildExpectedMessageKeyColumns(),
            HeartbeatActionQuery: new CdcHeartbeatActionQuery("select 1", "sha256-safe"),
            ProviderHistoryObservations: [],
            ManifestPayload: null,
            Diagnostics: []
        );

    private static IReadOnlyList<CdcProviderArtifactObservation> BuildPostgresqlArtifactInventory() =>
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
