// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateLiveValidation")]
public class Given_CdcConnectorTemplateLiveValidation
{
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
    public void It_rejects_exact_live_read_back_config_without_source_partition_evidence()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);

        CdcConnectorTemplateResult result = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                )
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().Equal(rendered.Config);
        CdcConnectorTemplateDiagnostic diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackSourcePartitionMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.LiveReadBack
                && diagnostic.PropertyName == "source.partition"
                && diagnostic.ExpectedValue == "actual connector source partition evidence"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
                && diagnostic.RedactionClassification == CdcConnectorTemplateRedactionClassification.Safe
            )
            .Which;
        diagnostic.ObservedValue.Should().BeNull();
    }

    [Test]
    public void It_accepts_masked_secret_read_back_values_for_sqlserver_generated_clients()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(
            CdcProvider.SqlServer,
            providerConnectionProperties: new Dictionary<string, string>(BuildSqlServerConnectionProperties())
            {
                ["driver.trustStorePassword"] = "${env:CDC_SQLSERVER_TRUSTSTORE_PASSWORD}",
            },
            kafkaSecurityProperties: new Dictionary<string, string>
            {
                ["security.protocol"] = "SASL_SSL",
                ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS_CONFIG}",
            }
        );
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        effectiveConfig["database.password"] = "[hidden]";
        effectiveConfig["driver.trustStorePassword"] = "[hidden]";
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
                BuildSourcePartitionEvidence(request)
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
                ),
                BuildSourcePartitionEvidence(request)
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
                ),
                BuildSourcePartitionEvidence(request)
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
    public void It_rejects_arbitrary_unexpected_read_back_properties_without_an_allow_list()
    {
        const string rawPhysicalIdentifier = "Server=unsafe-prod;Password=should-not-leak;Tenant=GrandBend";
        const string rawDocumentPayload =
            "{\"documentUuid\":\"abc\",\"studentUniqueId\":\"123\",\"tenant\":\"GrandBend\"}";
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        effectiveConfig["skipped.operations"] = rawDocumentPayload;
        effectiveConfig["decimal.handling.mode"] = "double";
        effectiveConfig["signal.data.collection"] = "dms.CdcSignal";
        effectiveConfig["database.connection.string"] = rawPhysicalIdentifier;

        CdcConnectorTemplateResult result = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                ),
                BuildSourcePartitionEvidence(request)
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
                && diagnostic.PropertyName == "skipped.operations"
                && diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.LiveReadBack
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
                && diagnostic.PropertyName == "decimal.handling.mode"
                && diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.LiveReadBack
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
                && diagnostic.PropertyName == "signal.data.collection"
                && diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.LiveReadBack
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
                && diagnostic.PropertyName == "database.connection.string"
                && diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ConnectionProperty
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        result.ToString().Contains(rawPhysicalIdentifier, StringComparison.Ordinal).Should().BeFalse();
        result.ToString().Contains(rawDocumentPayload, StringComparison.Ordinal).Should().BeFalse();
    }

    [Test]
    public void It_redacts_unexpected_secret_and_source_partition_values_conservatively()
    {
        const string rawSecret = "Password=should-not-leak;Tenant=GrandBend";
        const string rawPhysicalIdentifier = "Server=unsafe-prod;Database=edfi_prod;Tenant=GrandBend";
        const string rawDocumentPayload =
            "{\"documentUuid\":\"abc\",\"studentUniqueId\":\"123\",\"tenant\":\"GrandBend\"}";
        const string rawSourcePartitionExtra = "unexpected-prod-source;Tenant=GrandBend";
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        effectiveConfig["unexpected.custom"] = rawPhysicalIdentifier;
        effectiveConfig["custom.password"] = rawSecret;
        effectiveConfig["sasl.jaas.config"] = rawSecret;
        effectiveConfig["database.connection.string"] = rawPhysicalIdentifier;
        effectiveConfig["document.payload"] = rawDocumentPayload;

        CdcConnectorTemplateResult result = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                ),
                new CdcConnectorTemplateSourcePartitionEvidence(
                    new Dictionary<string, string>
                    {
                        ["server"] = request.ConnectorName.Value,
                        ["extra"] = rawSourcePartitionExtra,
                        ["custom.password"] = rawSecret,
                    }
                )
            )
        );

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
                && diagnostic.PropertyName == "unexpected.custom"
                && diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "custom.password"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.SecretValue
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "sasl.jaas.config"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.KafkaSecurityProperty
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.SecretValue
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "database.connection.string"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ConnectionProperty
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "document.payload"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.LiveReadBack
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackSourcePartitionMismatch
                && diagnostic.PropertyName == "source.partition.extra"
                && diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackSourcePartitionMismatch
                && diagnostic.PropertyName == "source.partition.custom.password"
                && diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.SecretValue
            );
        result
            .Diagnostics.SelectMany(diagnostic =>
                new[] { diagnostic.ExpectedValue, diagnostic.ObservedValue }
            )
            .Where(value => value is not null)
            .Should()
            .NotContain(value =>
                value!.Contains(rawSecret, StringComparison.Ordinal)
                || value.Contains(rawPhysicalIdentifier, StringComparison.Ordinal)
                || value.Contains(rawDocumentPayload, StringComparison.Ordinal)
                || value.Contains(rawSourcePartitionExtra, StringComparison.Ordinal)
            );
        result.ToString().Contains(rawSecret, StringComparison.Ordinal).Should().BeFalse();
        result.ToString().Contains(rawPhysicalIdentifier, StringComparison.Ordinal).Should().BeFalse();
        result.ToString().Contains(rawDocumentPayload, StringComparison.Ordinal).Should().BeFalse();
        result.ToString().Contains(rawSourcePartitionExtra, StringComparison.Ordinal).Should().BeFalse();
    }

    [Test]
    public void It_redacts_rendered_kafka_security_material_read_back_mismatches()
    {
        string[] generatedPrefixes =
        [
            "producer.override.",
            "schema.history.internal.producer.",
            "schema.history.internal.consumer.",
        ];
        var kafkaMaterialProperties = new Dictionary<string, string>
        {
            ["ssl.truststore.location"] = "/unsafe/prod/kafka-truststore-should-not-leak.p12",
            ["ssl.truststore.certificates"] = "TRUSTSTORE_CERTIFICATE_CHAIN_SHOULD_NOT_LEAK",
            ["ssl.keystore.location"] = "/unsafe/prod/kafka-keystore-should-not-leak.p12",
            ["ssl.keystore.certificate.chain"] = "KEYSTORE_CERTIFICATE_CHAIN_SHOULD_NOT_LEAK",
        };
        var kafkaSecurityProperties = new Dictionary<string, string>
        {
            ["security.protocol"] = "SSL",
            ["ssl.protocol"] = "TLSv1.3",
            ["ssl.endpoint.identification.algorithm"] = "https",
        };
        foreach (var property in kafkaMaterialProperties)
        {
            kafkaSecurityProperties[property.Key] = property.Value;
        }

        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(
            CdcProvider.SqlServer,
            kafkaSecurityProperties: kafkaSecurityProperties
        );
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        var sentinelValues = new List<string>();

        foreach (string prefix in generatedPrefixes)
        {
            foreach (var property in kafkaMaterialProperties)
            {
                string observedValue = $"observed-{property.Value}";
                effectiveConfig[$"{prefix}{property.Key}"] = observedValue;
                sentinelValues.Add(property.Value);
                sentinelValues.Add(observedValue);
            }
        }

        effectiveConfig["producer.override.ssl.protocol"] = "TLSv1.2";
        effectiveConfig["schema.history.internal.consumer.ssl.endpoint.identification.algorithm"] =
            "disabled";

        CdcConnectorTemplateResult result = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.SqlServer)
                ),
                BuildSourcePartitionEvidence(request)
            )
        );
        var exception = new CdcConnectorTemplateValidationException(result.Diagnostics);

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        foreach (string prefix in generatedPrefixes)
        {
            foreach (string suffix in kafkaMaterialProperties.Keys)
            {
                CdcConnectorTemplateDiagnostic diagnostic = result
                    .Diagnostics.Should()
                    .ContainSingle(diagnostic =>
                        diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackPropertyMismatch
                        && diagnostic.PropertyName == $"{prefix}{suffix}"
                    )
                    .Which;
                diagnostic.ExpectedValue.Should().Be("[redacted]");
                diagnostic.ObservedValue.Should().Be("[redacted]");
                diagnostic
                    .RedactionClassification.Should()
                    .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
            }
        }

        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.PropertyName == "producer.override.ssl.protocol"
                && diagnostic.ExpectedValue == "TLSv1.3"
                && diagnostic.ObservedValue == "TLSv1.2"
                && diagnostic.RedactionClassification == CdcConnectorTemplateRedactionClassification.Safe
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName
                    == "schema.history.internal.consumer.ssl.endpoint.identification.algorithm"
                && diagnostic.ExpectedValue == "https"
                && diagnostic.ObservedValue == "disabled"
                && diagnostic.RedactionClassification == CdcConnectorTemplateRedactionClassification.Safe
            );
        result
            .Diagnostics.SelectMany(diagnostic =>
                new[] { diagnostic.ExpectedValue, diagnostic.ObservedValue }
            )
            .Where(value => value is not null)
            .Should()
            .NotContain(value =>
                sentinelValues.Any(sentinel => value!.Contains(sentinel, StringComparison.Ordinal))
            );
        result
            .ToString()
            .Should()
            .NotContainAny(sentinelValues.ToArray(), "result text should redact Kafka material");
        exception
            .Message.Should()
            .NotContainAny(sentinelValues.ToArray(), "exception messages should not include raw values");
    }

    [Test]
    public void It_rejects_provider_setup_evidence_that_no_longer_matches_the_binding()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);
        CdcSourceFingerprint differentFingerprint = OtherPostgresqlSourceFingerprint;

        CdcConnectorTemplateResult result = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 8,
                    BuildProviderSetupResult(
                        CdcProvider.Postgresql,
                        outcome: CdcProviderSetupOutcome.Failed,
                        boundPhysicalSourceFingerprint: differentFingerprint
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
    public void It_uses_sqlserver_capture_instance_prerequisites_for_preflight_and_live_read_back()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.SqlServer);
        CdcConnectorTemplateResult rendered = service.Render(request);
        CdcConnectorProviderSetupEvidence badProviderSetupEvidence = new(
            bindingGeneration: 7,
            BuildProviderSetupResult(
                CdcProvider.SqlServer,
                artifactInventory:
                [
                    BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.DocumentCache),
                    BuildSqlServerCaptureInstanceArtifact(
                        CdcSourceTableKind.Document,
                        CdcProviderArtifactState.Unavailable
                    ),
                    BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.CdcHeartbeat),
                ]
            )
        );

        CdcConnectorTemplateResult preflightResult = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                badProviderSetupEvidence
            )
        );
        CdcConnectorTemplateResult liveReadBackResult = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                badProviderSetupEvidence,
                BuildSourcePartitionEvidence(request)
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflightResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        liveReadBackResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        preflightResult.Config.Should().BeEmpty();
        liveReadBackResult.Config.Should().BeEmpty();
        preflightResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code
                    == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult
                && diagnostic.PropertyName == "providerSetup.artifactInventory.sqlServerCaptureInstance"
                && diagnostic.ExpectedValue
                    == "one usable SQL Server capture-instance artifact for dms.Document"
                && diagnostic.ObservedValue == "Unavailable"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.RegistrationPreflight
            );
        liveReadBackResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code
                    == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult
                && diagnostic.PropertyName == "providerSetup.artifactInventory.sqlServerCaptureInstance"
                && diagnostic.ExpectedValue
                    == "one usable SQL Server capture-instance artifact for dms.Document"
                && diagnostic.ObservedValue == "Unavailable"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
            );
    }

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_rejects_provider_setup_source_column_drift_for_preflight_and_live_read_back(
        CdcProvider provider
    )
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(provider);
        CdcConnectorTemplateResult rendered = service.Render(request);
        CdcConnectorProviderSetupEvidence badProviderSetupEvidence = new(
            bindingGeneration: 7,
            BuildProviderSetupResult(
                provider,
                sourceTableInventory:
                [
                    BuildSourceTable(
                        provider,
                        CdcSourceTableKind.DocumentCache,
                        "DocumentCache",
                        [BuildColumn(provider, "DocumentUuid;DROP_TABLE")]
                    ),
                    BuildSourceTable(
                        provider,
                        CdcSourceTableKind.Document,
                        "Document",
                        [BuildColumn(provider, "DocumentUuid"), BuildColumn(provider, "DocumentUuid", 2)]
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
                ]
            )
        );

        CdcConnectorTemplateResult preflightResult = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                badProviderSetupEvidence
            )
        );
        CdcConnectorTemplateResult liveReadBackResult = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                badProviderSetupEvidence,
                BuildSourcePartitionEvidence(request)
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflightResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        liveReadBackResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        preflightResult.Config.Should().BeEmpty();
        liveReadBackResult.Config.Should().BeEmpty();
        preflightResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKey
                && diagnostic.PropertyName == "message.key.columns"
                && diagnostic.ExpectedValue == "source column DocumentUuid for dms.DocumentCache"
                && diagnostic.ObservedValue == "missing"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.RegistrationPreflight
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKey
                && diagnostic.PropertyName == "message.key.columns"
                && diagnostic.ExpectedValue == "unique source column names for dms.Document"
                && diagnostic.ObservedValue == "duplicate"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.RegistrationPreflight
            );
        liveReadBackResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKey
                && diagnostic.PropertyName == "message.key.columns"
                && diagnostic.ExpectedValue == "source column DocumentUuid for dms.DocumentCache"
                && diagnostic.ObservedValue == "missing"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKey
                && diagnostic.PropertyName == "message.key.columns"
                && diagnostic.ExpectedValue == "unique source column names for dms.Document"
                && diagnostic.ObservedValue == "duplicate"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
            );
        preflightResult
            .Diagnostics.Concat(liveReadBackResult.Diagnostics)
            .Should()
            .OnlyContain(diagnostic =>
                diagnostic.RedactionClassification
                == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        string.Join(
                "|",
                preflightResult.Diagnostics.Concat(liveReadBackResult.Diagnostics).SelectMany(DiagnosticText)
            )
            .Should()
            .NotContain("DROP_TABLE", because: "raw source column names are redacted");
    }

    [Test]
    public void It_rejects_null_fresh_provider_setup_inventories_for_preflight_and_live_read_back()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);
        CdcConnectorProviderSetupEvidence malformedProviderSetupEvidence = new(
            BindingGeneration,
            BuildProviderSetupResult(CdcProvider.Postgresql) with
            {
                SourceTableInventory = null!,
                ExpectedMessageKeyColumns = null!,
            }
        );

        CdcConnectorTemplateResult preflightResult = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                malformedProviderSetupEvidence
            )
        );
        CdcConnectorTemplateResult liveReadBackResult = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                malformedProviderSetupEvidence,
                BuildSourcePartitionEvidence(request)
            )
        );

        using var _ = new AssertionScope();
        preflightResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        liveReadBackResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        preflightResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.RegistrationPreflight
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch
                && diagnostic.PropertyName == "providerSetup.expectedMessageKeyColumns"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.RegistrationPreflight
            );
        liveReadBackResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch
                && diagnostic.PropertyName == "providerSetup.expectedMessageKeyColumns"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
            );
        preflightResult
            .Diagnostics.Concat(liveReadBackResult.Diagnostics)
            .Should()
            .OnlyContain(diagnostic =>
                diagnostic.RedactionClassification
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
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
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

    private static IEnumerable<string> DiagnosticText(CdcConnectorTemplateDiagnostic diagnostic) =>
        [diagnostic.ExpectedValue ?? string.Empty, diagnostic.ObservedValue ?? string.Empty];
}
