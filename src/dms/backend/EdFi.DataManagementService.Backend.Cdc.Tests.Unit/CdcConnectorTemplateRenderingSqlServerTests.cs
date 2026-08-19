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
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateRenderingSqlServer")]
public class Given_CdcConnectorTemplateSqlServerRendering
{
    [Test]
    public void It_renders_the_sqlserver_connector_contract_from_provider_setup_metadata()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.SqlServer,
                deploymentPolicy: new CdcConnectorTemplateDeploymentPolicy(
                    "broker-1:9092,broker-2:9092",
                    maxRecordBytes: 67_108_864,
                    heartbeatInterval: TimeSpan.FromSeconds(5),
                    sqlServerPollInterval: TimeSpan.FromSeconds(2)
                ),
                providerConnectionProperties: new Dictionary<string, string>(
                    BuildSqlServerConnectionProperties()
                )
                {
                    ["driver.encrypt"] = "true",
                    ["driver.trustServerCertificate"] = "true",
                    ["driver.trustStorePassword"] = "${env:CDC_SQLSERVER_TRUSTSTORE_PASSWORD}",
                },
                kafkaSecurityProperties: new Dictionary<string, string>
                {
                    ["security.protocol"] = "SASL_SSL",
                    ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS_CONFIG}",
                }
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.SchemaHistoryTopicName.Should().Be("edfi.documents.schema-history");
        result
            .Config.Should()
            .Contain("connector.class", "io.debezium.connector.sqlserver.SqlServerConnector");
        result
            .Config.Should()
            .Contain("table.include.list", @"dms\.DocumentCache,dms\.Document,dms\.CdcHeartbeat");
        result
            .Config.Should()
            .Contain("message.key.columns", @"dms\.DocumentCache:DocumentUuid;dms\.Document:DocumentUuid");
        result.Config.Should().Contain("time.precision.mode", "isostring");
        result.Config.Should().Contain("unavailable.value.placeholder", "__debezium_unavailable_value");
        result.Config.Should().Contain("poll.interval.ms", "2000");
        result.Config.Should().Contain("snapshot.isolation.mode", "snapshot");
        result.Config.Should().Contain("database.names", "edfi_datastore");
        result.Config.Should().Contain("driver.encrypt", "true");
        result.Config.Should().Contain("driver.trustServerCertificate", "true");
        result
            .Config.Should()
            .Contain("driver.trustStorePassword", "${env:CDC_SQLSERVER_TRUSTSTORE_PASSWORD}");
        result
            .Config.Should()
            .Contain("schema.history.internal.kafka.bootstrap.servers", "broker-1:9092,broker-2:9092");
        result
            .Config.Should()
            .Contain("schema.history.internal.kafka.topic", "edfi.documents.schema-history");
        result.Config.Should().Contain("schema.history.internal.producer.enable.idempotence", "true");
        result.Config.Should().Contain("schema.history.internal.producer.acks", "all");
        result.Config.Should().Contain("schema.history.internal.producer.retries", "2147483647");
        result
            .Config.Should()
            .Contain("schema.history.internal.producer.max.in.flight.requests.per.connection", "1");
        result.Config.Should().Contain("include.schema.changes", "false");
        result.Config.Should().Contain("producer.override.security.protocol", "SASL_SSL");
        result.Config.Should().Contain("producer.override.sasl.jaas.config", "${env:CDC_KAFKA_JAAS_CONFIG}");
        result.Config.Should().Contain("schema.history.internal.producer.security.protocol", "SASL_SSL");
        result
            .Config.Should()
            .Contain("schema.history.internal.producer.sasl.jaas.config", "${env:CDC_KAFKA_JAAS_CONFIG}");
        result.Config.Should().Contain("schema.history.internal.consumer.security.protocol", "SASL_SSL");
        result
            .Config.Should()
            .Contain("schema.history.internal.consumer.sasl.jaas.config", "${env:CDC_KAFKA_JAAS_CONFIG}");
        result
            .Config["table.include.list"]
            .Should()
            .NotContain("DocumentProjectionWork", because: "work-table capture is outside the contract");
        result
            .Config["table.include.list"]
            .Should()
            .NotContain("[", because: "Debezium selectors are not SQL quoted identifiers");
        result
            .Config["message.key.columns"]
            .Should()
            .NotContain("CdcHeartbeat", because: "heartbeat rows use the transform progress key");
        result
            .Config["message.key.columns"]
            .Should()
            .NotContain("[", because: "Debezium selectors are not SQL quoted identifiers");
        result
            .Config.Values.Should()
            .NotContain(
                value => value.Contains("_capture", StringComparison.Ordinal),
                because: "SQL Server capture-instance names remain provider validation metadata"
            );
        result
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("database.history.", StringComparison.Ordinal));
        result
            .Config.Keys.Should()
            .NotContain(key => key.StartsWith("topic.creation.", StringComparison.Ordinal));
    }

    [Test]
    public void It_orders_the_include_list_and_message_keys_by_the_contract_not_input_order()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.SqlServer,
                sourceTableInventory:
                [
                    BuildSourceTable(
                        CdcProvider.SqlServer,
                        CdcSourceTableKind.CdcHeartbeat,
                        "CdcHeartbeat",
                        [
                            BuildColumn(CdcProvider.SqlServer, "HeartbeatId"),
                            BuildColumn(CdcProvider.SqlServer, "HeartbeatSequence", 2),
                            BuildColumn(CdcProvider.SqlServer, "HeartbeatAt", 3),
                        ]
                    ),
                    BuildSourceTable(
                        CdcProvider.SqlServer,
                        CdcSourceTableKind.Document,
                        "Document",
                        [BuildColumn(CdcProvider.SqlServer, "DocumentUuid")]
                    ),
                    BuildSourceTable(
                        CdcProvider.SqlServer,
                        CdcSourceTableKind.DocumentCache,
                        "DocumentCache",
                        [BuildColumn(CdcProvider.SqlServer, "DocumentUuid")]
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
        result
            .Config["table.include.list"]
            .Should()
            .Be(@"dms\.DocumentCache,dms\.Document,dms\.CdcHeartbeat");
        result
            .Config["message.key.columns"]
            .Should()
            .Be(@"dms\.DocumentCache:DocumentUuid;dms\.Document:DocumentUuid");
    }

    [Test]
    public void It_rejects_work_table_capture_from_the_provider_setup_source_inventory()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.SqlServer,
                sourceTableInventory:
                [
                    BuildSourceTable(
                        CdcProvider.SqlServer,
                        CdcSourceTableKind.DocumentCache,
                        "DocumentCache",
                        [BuildColumn(CdcProvider.SqlServer, "DocumentUuid")]
                    ),
                    BuildSourceTable(
                        CdcProvider.SqlServer,
                        CdcSourceTableKind.Document,
                        "DocumentProjectionWork;DROP TABLE",
                        [BuildColumn(CdcProvider.SqlServer, "DocumentUuid")]
                    ),
                    BuildSourceTable(
                        CdcProvider.SqlServer,
                        CdcSourceTableKind.CdcHeartbeat,
                        "CdcHeartbeat",
                        [
                            BuildColumn(CdcProvider.SqlServer, "HeartbeatId"),
                            BuildColumn(CdcProvider.SqlServer, "HeartbeatSequence", 2),
                            BuildColumn(CdcProvider.SqlServer, "HeartbeatAt", 3),
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
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.IncludeListViolation);
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

    [TestCase(CdcSourceTableKind.DocumentCache, "DocumentCache")]
    [TestCase(CdcSourceTableKind.Document, "Document")]
    public void It_rejects_missing_document_uuid_source_columns_before_rendering(
        CdcSourceTableKind tableKind,
        string tableName
    )
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.SqlServer,
                sourceTableInventory: BuildSourceInventoryReplacing(
                    CdcProvider.SqlServer,
                    BuildSourceTable(
                        CdcProvider.SqlServer,
                        tableKind,
                        tableName,
                        [BuildColumn(CdcProvider.SqlServer, "DocumentUuid;DROP_TABLE")]
                    )
                )
            )
        );

        CdcConnectorTemplateDiagnostic diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
            )
            .Subject;

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation);
        diagnostic.PropertyName.Should().Be("message.key.columns");
        diagnostic.ExpectedValue.Should().Be($"source column DocumentUuid for dms.{tableName}");
        diagnostic.ObservedValue.Should().Be("missing");
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
        string.Join("|", result.Diagnostics.SelectMany(DiagnosticText))
            .Should()
            .NotContain("DROP_TABLE", because: "raw physical source column names are redacted");
    }

    [Test]
    public void It_rejects_duplicate_source_column_names_before_rendering()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.SqlServer,
                sourceTableInventory: BuildSourceInventoryReplacing(
                    CdcProvider.SqlServer,
                    BuildSourceTable(
                        CdcProvider.SqlServer,
                        CdcSourceTableKind.Document,
                        "Document",
                        [
                            BuildColumn(CdcProvider.SqlServer, "DocumentUuid"),
                            BuildColumn(CdcProvider.SqlServer, "DocumentUuid", 2),
                        ]
                    )
                )
            )
        );

        CdcConnectorTemplateDiagnostic diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
            )
            .Subject;

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation);
        diagnostic.PropertyName.Should().Be("message.key.columns");
        diagnostic.ExpectedValue.Should().Be("unique source column names for dms.Document");
        diagnostic.ObservedValue.Should().Be("duplicate");
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
    }

    [Test]
    public void It_rejects_multi_database_input_without_rendering()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.SqlServer,
                providerConnectionProperties: new Dictionary<string, string>
                {
                    ["database.hostname"] = "sqlserver.internal",
                    ["database.user"] = "connector_user",
                    ["database.names"] = "edfi_datastore, other_datastore",
                    ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                }
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SqlServerSingleDatabaseRequired
                && diagnostic.PropertyName == "database.names"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
    }

    [Test]
    public void It_rejects_whitespace_padded_database_name_input_without_rendering()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.SqlServer,
                providerConnectionProperties: new Dictionary<string, string>
                {
                    ["database.hostname"] = "sqlserver.internal",
                    ["database.user"] = "connector_user",
                    ["database.names"] = " edfi_datastore ",
                    ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                }
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        result.RegistrationPayload.Should().BeNull();
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SqlServerSingleDatabaseRequired
                && diagnostic.PropertyName == "database.names"
                && diagnostic.ObservedValue == "[redacted]"
            );
        result
            .Diagnostics.SelectMany(DiagnosticText)
            .Should()
            .NotContain(value => value.Contains("edfi_datastore", StringComparison.Ordinal));
    }

    [Test]
    public void It_rejects_missing_capture_instance_artifacts_before_rendering()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(CdcProvider.SqlServer, artifactInventory: [])
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        result.RegistrationPayload.Should().BeNull();
        result
            .Diagnostics.Where(diagnostic =>
                diagnostic.Code
                == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
            )
            .Should()
            .HaveCount(3)
            .And.OnlyContain(diagnostic =>
                diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.PropertyName == "providerSetup.artifactInventory.sqlServerCaptureInstance"
                && diagnostic.ObservedValue == "missing"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Render
            );
    }

    [Test]
    public void It_rejects_missing_poll_interval_before_rendering()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.SqlServer,
                deploymentPolicy: new CdcConnectorTemplateDeploymentPolicy(
                    "broker:9092",
                    maxRecordBytes: 1_048_576,
                    heartbeatInterval: TimeSpan.FromSeconds(5)
                )
            )
        );

        CdcConnectorTemplateDiagnostic diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SqlServerPollIntervalRequired
            )
            .Subject;

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        result.RegistrationPayload.Should().BeNull();
        diagnostic
            .Category.Should()
            .Be(CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation);
        diagnostic.PropertyName.Should().Be("poll.interval.ms");
        diagnostic.ExpectedValue.Should().Be("positive SQL Server poll interval");
        diagnostic.ObservedValue.Should().BeNull();
        diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.Render);
    }

    [Test]
    public void It_rejects_poll_intervals_that_exceed_the_heartbeat_interval()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.SqlServer,
                deploymentPolicy: new CdcConnectorTemplateDeploymentPolicy(
                    "broker:9092",
                    maxRecordBytes: 1_048_576,
                    heartbeatInterval: TimeSpan.FromSeconds(5),
                    sqlServerPollInterval: TimeSpan.FromSeconds(6)
                )
            )
        );

        CdcConnectorTemplateDiagnostic diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code
                == CdcConnectorTemplateDiagnosticCodes.SqlServerPollIntervalExceedsHeartbeatInterval
            )
            .Subject;

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        diagnostic
            .Category.Should()
            .Be(CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation);
        diagnostic.PropertyName.Should().Be("poll.interval.ms");
        diagnostic.ExpectedValue.Should().Be("<= heartbeat.interval.ms (5000)");
        diagnostic.ObservedValue.Should().Be("6000");
    }

    [Test]
    public void It_rejects_non_positive_sqlserver_poll_intervals()
    {
        Action act = () =>
            _ = new CdcConnectorTemplateDeploymentPolicy(
                "broker:9092",
                maxRecordBytes: 1_048_576,
                sqlServerPollInterval: TimeSpan.Zero
            );

        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*positive*");
    }

    [Test]
    public void It_rejects_caller_supplied_sqlserver_connector_and_schema_history_properties()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.SqlServer,
                providerConnectionProperties: new Dictionary<string, string>
                {
                    ["database.hostname"] = "sqlserver.internal",
                    ["database.user"] = "connector_user",
                    ["database.names"] = "edfi_datastore",
                    ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                    ["time.precision.mode"] = "isostring",
                    ["schema.history.internal.kafka.topic"] = "edfi.documents.schema-history",
                }
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        result
            .Diagnostics.Select(diagnostic => diagnostic.PropertyName)
            .Should()
            .BeEquivalentTo("time.precision.mode", "schema.history.internal.kafka.topic");
        result
            .Diagnostics.Should()
            .OnlyContain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.ReservedKey
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ReservedKeyViolation
            );
    }

    [Test]
    public void It_rejects_missing_message_key_inventory_before_rendering()
    {
        Action act = () => BuildRequest(CdcProvider.SqlServer, expectedMessageKeyColumns: []);

        act.Should().Throw<ArgumentException>().WithMessage("*message-key inventory*");
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

    private static IEnumerable<string> DiagnosticText(CdcConnectorTemplateDiagnostic diagnostic) =>
        [diagnostic.ExpectedValue ?? string.Empty, diagnostic.ObservedValue ?? string.Empty];
}
