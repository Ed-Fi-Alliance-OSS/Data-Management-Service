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
[Category("CdcConnectorTemplateLiveValidation")]
public class Given_CdcConnectorTemplateLiveValidation
{
    private static readonly CdcSourceFingerprint SourceFingerprint = new(
        "cdc-source-fingerprint-v1",
        "physical-source-fingerprint"
    );

    [Test]
    public void It_accepts_exact_registration_preflight_config_and_empty_heartbeat_name()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        effectiveConfig["topic.heartbeat.name"] = "";

        CdcConnectorTemplateResult result = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                )
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        rendered.Config["table.include.list"].Should().Be("dms.DocumentCache,dms.Document,dms.CdcHeartbeat");
        rendered
            .Config["message.key.columns"]
            .Should()
            .Be("dms.DocumentCache:DocumentUuid;dms.Document:DocumentUuid");
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Config.Should().Equal(rendered.Config);
        result.RegistrationPayload!.Config.Should().Equal(rendered.RegistrationPayload!.Config);
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_accepts_masked_secret_read_back_values_for_sqlserver_generated_clients()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(
            CdcProvider.SqlServer,
            kafkaSecurityProperties: new Dictionary<string, string>
            {
                ["security.protocol"] = "SASL_SSL",
                ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS_CONFIG}",
            }
        );
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        effectiveConfig["database.password"] = "[hidden]";
        effectiveConfig["producer.override.sasl.jaas.config"] = "********";
        effectiveConfig["schema.history.internal.producer.sasl.jaas.config"] = "[hidden]";
        effectiveConfig["schema.history.internal.consumer.sasl.jaas.config"] = "***";

        CdcConnectorTemplateResult result = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.SqlServer)
                ),
                new CdcConnectorTemplateSourcePartitionEvidence(
                    new Dictionary<string, string>
                    {
                        ["server"] = "dms_binding_connector",
                        ["database"] = "edfi_datastore",
                    }
                )
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_unmasked_or_missing_secret_read_back_values_without_leaking_them()
    {
        const string rawSecret =
            "Server=unsafe-prod;Password=should-not-leak;Tenant=GrandBend;{\"documentUuid\":\"abc\"}";
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(
            CdcProvider.Postgresql,
            kafkaSecurityProperties: new Dictionary<string, string>
            {
                ["security.protocol"] = "SASL_SSL",
                ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS_CONFIG}",
            }
        );
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        effectiveConfig["database.password"] = rawSecret;
        effectiveConfig.Remove("producer.override.sasl.jaas.config");

        CdcConnectorTemplateResult result = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                )
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackSecretMismatch
                && diagnostic.PropertyName == "database.password"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.SecretRedactionFailure
            );
        CdcConnectorTemplateDiagnostic missingSecretDiagnostic = result.Diagnostics.Single(diagnostic =>
            diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackPropertyMissing
            && diagnostic.PropertyName == "producer.override.sasl.jaas.config"
        );
        missingSecretDiagnostic.ObservedValue.Should().BeNull();
        result
            .Diagnostics.SelectMany(diagnostic =>
                new[] { diagnostic.ExpectedValue, diagnostic.ObservedValue }
            )
            .Where(value => value is not null)
            .Should()
            .NotContain(value => value!.Contains(rawSecret, StringComparison.Ordinal));
        result.ToString().Contains(rawSecret, StringComparison.Ordinal).Should().BeFalse();
    }

    [Test]
    public void It_rejects_generated_config_drift_and_unexpected_reserved_read_back_keys()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.SqlServer);
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        effectiveConfig["value.converter"] = "org.example.WrongConverter";
        effectiveConfig["tombstones.on.delete"] = "true";
        effectiveConfig["transforms.documentState.progress.topic"] = "edfi.documents.wrong-progress";
        effectiveConfig["schema.history.internal.kafka.topic"] = "edfi.documents.wrong-history";
        effectiveConfig["topic.heartbeat.name"] = "unexpected-heartbeat-name";
        effectiveConfig["table.include.list"] =
            $"{rendered.Config["table.include.list"]},dms.DocumentProjectionWork";
        effectiveConfig["message.key.columns"] =
            $"{rendered.Config["message.key.columns"]};dms.CdcHeartbeat:HeartbeatId";
        effectiveConfig["topic.creation.default.replication.factor"] = "1";

        CdcConnectorTemplateResult result = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.SqlServer)
                )
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().Equal(rendered.Config);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.PropertyName == "value.converter"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.Converter
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "tombstones.on.delete"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.Converter
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "transforms.documentState.progress.topic"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.Transform
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "schema.history.internal.kafka.topic"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.SchemaHistory
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "topic.heartbeat.name"
                && diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "table.include.list"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.IncludeList
                && diagnostic.ObservedValue == "[redacted]"
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "message.key.columns"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKey
                && diagnostic.ObservedValue == "[redacted]"
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "topic.creation.default.replication.factor"
                && diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
            );
    }

    [Test]
    public void It_rejects_provider_setup_evidence_that_no_longer_matches_the_binding()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);
        var differentFingerprint = new CdcSourceFingerprint(
            "cdc-source-fingerprint-v1",
            "different-physical-source"
        );

        CdcConnectorTemplateResult result = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 8,
                    BuildProviderSetupResult(
                        CdcProvider.Postgresql,
                        outcome: CdcProviderSetupOutcome.Failed,
                        fingerprint: differentFingerprint
                    )
                )
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        result
            .Diagnostics.Should()
            .OnlyContain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
            );
        result
            .Diagnostics.Select(diagnostic => diagnostic.PropertyName)
            .Should()
            .Contain(["providerSetup.outcome", "providerSetup.bindingGeneration"]);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.PropertyName == "providerSetup.boundPhysicalSourceFingerprint"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
    }

    [Test]
    public void It_rejects_source_partition_shape_drift_without_leaking_sqlserver_database_names()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.SqlServer);
        CdcConnectorTemplateResult rendered = service.Render(request);

        CdcConnectorTemplateResult result = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.SqlServer)
                ),
                new CdcConnectorTemplateSourcePartitionEvidence(
                    new Dictionary<string, string>
                    {
                        ["server"] = "different_connector",
                        ["database"] = "other_datastore",
                        ["extra"] = "unexpected",
                    }
                )
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackSourcePartitionMismatch
                && diagnostic.PropertyName == "source.partition.server"
                && diagnostic.ExpectedValue == "dms_binding_connector"
                && diagnostic.ObservedValue == "different_connector"
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "source.partition.database"
                && diagnostic.ExpectedValue == "[redacted]"
                && diagnostic.ObservedValue == "[redacted]"
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "source.partition.extra"
                && diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "unexpected"
            );
        result
            .Diagnostics.SelectMany(diagnostic =>
                new[] { diagnostic.ExpectedValue, diagnostic.ObservedValue }
            )
            .Where(value => value is not null)
            .Should()
            .NotContain(value => value!.Contains("other_datastore", StringComparison.Ordinal));
    }

    private static ServiceProvider BuildServiceProvider() =>
        new ServiceCollection().AddCdcConnectorTemplates().BuildServiceProvider();

    private static Dictionary<string, string> CopyConfig(IReadOnlyDictionary<string, string> config) =>
        config.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static CdcConnectorTemplateRequest BuildRequest(
        CdcProvider provider,
        IReadOnlyDictionary<string, string>? providerConnectionProperties = null,
        IReadOnlyDictionary<string, string>? kafkaSecurityProperties = null
    ) =>
        new(
            BuildBinding(provider),
            new CdcConnectorProviderSetupEvidence(bindingGeneration: 7, BuildProviderSetupResult(provider)),
            new CdcConnectorTemplateDeploymentPolicy(
                "broker-1:9092,broker-2:9092",
                maxRecordBytes: 67_108_864,
                heartbeatInterval: TimeSpan.FromSeconds(5),
                sqlServerPollInterval: provider == CdcProvider.SqlServer ? TimeSpan.FromSeconds(2) : null
            ),
            new CdcProviderConnectionProperties(
                provider,
                providerConnectionProperties ?? BuildProviderConnectionProperties(provider)
            ),
            new CdcKafkaClientSecurityProperties(kafkaSecurityProperties ?? new Dictionary<string, string>())
        );

    private static CdcConnectorTemplateBindingIdentity BuildBinding(CdcProvider provider) =>
        new(
            provider,
            new CdcSafeName("dms_binding_connector"),
            "edfi.documents",
            bindingGeneration: 7,
            SourceFingerprint
        );

    private static IReadOnlyDictionary<string, string> BuildProviderConnectionProperties(
        CdcProvider provider
    ) =>
        provider switch
        {
            CdcProvider.Postgresql => new Dictionary<string, string>
            {
                ["database.hostname"] = "postgresql.internal",
                ["database.port"] = "5432",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.dbname"] = "edfi_datastore",
            },
            CdcProvider.SqlServer => new Dictionary<string, string>
            {
                ["database.hostname"] = "sqlserver.internal",
                ["database.port"] = "1433",
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

    private static CdcProviderSetupResult BuildProviderSetupResult(
        CdcProvider provider,
        CdcProviderSetupOutcome outcome = CdcProviderSetupOutcome.CreatedOrMatched,
        CdcSourceFingerprint? fingerprint = null
    )
    {
        CdcSourceFingerprint sourceFingerprint = fingerprint ?? SourceFingerprint;

        return new(
            Provider: provider,
            Mode: CdcProviderSetupMode.InitialCreateOrExactMatch,
            Outcome: outcome,
            BoundPhysicalSourceFingerprint: sourceFingerprint,
            ObservedSourceFingerprint: sourceFingerprint,
            ArtifactInventory: BuildArtifactInventory(provider),
            GrantInventory: [],
            SourceTableInventory: BuildRequiredSourceTableInventory(provider),
            ExpectedMessageKeyColumns: BuildExpectedMessageKeyColumns(),
            HeartbeatActionQuery: new CdcHeartbeatActionQuery("select 1", "sha256-safe"),
            ProviderHistoryObservations: [],
            ManifestPayload: null,
            Diagnostics: []
        );
    }

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
            "text",
            IsNullable: false
        );

    private static IReadOnlyList<CdcExpectedMessageKeyColumns> BuildExpectedMessageKeyColumns() =>
        [
            new(CdcSourceTableKind.DocumentCache, [new DbColumnName("DocumentUuid")]),
            new(CdcSourceTableKind.Document, [new DbColumnName("DocumentUuid")]),
        ];
}
