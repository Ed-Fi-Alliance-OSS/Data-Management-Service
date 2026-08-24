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
[Category("CdcConnectorTemplateLiveValidation")]
public class Given_CdcConnectorTemplateLiveValidationTests
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
        rendered
            .Config["table.include.list"]
            .Should()
            .Be(@"dms\.DocumentCache,dms\.Document,dms\.CdcHeartbeat");
        rendered
            .Config["message.key.columns"]
            .Should()
            .Be(@"dms\.DocumentCache:DocumentUuid;dms\.Document:DocumentUuid");
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Config.Should().Equal(rendered.Config);
        result.RegistrationPayload!.Config.Should().Equal(rendered.RegistrationPayload!.Config);
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_renders_and_reads_back_empty_kafka_endpoint_identification_algorithm()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(
            CdcProvider.SqlServer,
            kafkaSecurityProperties: new Dictionary<string, string>
            {
                ["security.protocol"] = "SSL",
                ["ssl.endpoint.identification.algorithm"] = "",
            }
        );
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);

        CdcConnectorTemplateResult result = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                new CdcConnectorProviderSetupEvidence(
                    BindingGeneration,
                    BuildProviderSetupResult(CdcProvider.SqlServer)
                ),
                BuildSourcePartitionEvidence(request)
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        rendered
            .Config.Should()
            .Contain("producer.override.ssl.endpoint.identification.algorithm", "")
            .And.Contain("schema.history.internal.producer.ssl.endpoint.identification.algorithm", "")
            .And.Contain("schema.history.internal.consumer.ssl.endpoint.identification.algorithm", "");
        rendered
            .RegistrationPayload!.Config.Should()
            .Contain("producer.override.ssl.endpoint.identification.algorithm", "")
            .And.Contain("schema.history.internal.producer.ssl.endpoint.identification.algorithm", "")
            .And.Contain("schema.history.internal.consumer.ssl.endpoint.identification.algorithm", "");
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Config.Should().Equal(rendered.Config);
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_accepts_exact_externalized_secret_references_during_registration_preflight()
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
        IReadOnlyList<string> secretPropertyNames = SqlServerGeneratedSecretPropertyNames();

        CdcConnectorTemplateResult result = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.SqlServer)
                )
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        rendered.Config.Keys.Should().Contain(secretPropertyNames);
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Diagnostics.Should().BeEmpty();
    }

    [TestCase("[hidden]")]
    [TestCase("***")]
    public void It_rejects_masked_secret_placeholders_during_registration_preflight(string maskedValue)
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
        IReadOnlyList<string> secretPropertyNames = SqlServerGeneratedSecretPropertyNames();
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        rendered.Config.Keys.Should().Contain(secretPropertyNames);
        foreach (string propertyName in secretPropertyNames)
        {
            effectiveConfig[propertyName] = maskedValue;
        }

        CdcConnectorTemplateResult result = service.ValidateRegistrationPreflight(
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
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result
            .Diagnostics.Where(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackSecretMismatch
            )
            .Select(diagnostic => diagnostic.PropertyName)
            .Should()
            .BeEquivalentTo(secretPropertyNames);
        result
            .Diagnostics.Should()
            .OnlyContain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackSecretMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.SecretRedactionViolation
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
                && diagnostic.ExpectedValue == "[redacted]"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.SecretValue
            );
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
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch
                && diagnostic.PropertyName == "source.partition"
                && diagnostic.ExpectedValue == "actual connector source partition evidence"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
                && diagnostic.RedactionClassification == CdcConnectorTemplateRedactionClassification.Safe
            )
            .Which;
        diagnostic.ObservedValue.Should().BeNull();
    }

    [Test]
    public void It_uses_binding_artifact_name_prerequisites_for_preflight_and_live_read_back()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);
        var badProviderSetupEvidence = new CdcConnectorProviderSetupEvidence(
            BindingGeneration,
            BuildProviderSetupResult(
                CdcProvider.Postgresql,
                artifactInventory:
                [
                    BuildPostgresqlPublicationArtifact(),
                    BuildPostgresqlReplicationSlotArtifact(
                        safeArtifactName: new CdcSafeName("other_binding_slot")
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
            .ContainSingle(diagnostic =>
                diagnostic.Code
                    == CdcConnectorTemplateDiagnosticCodes.PostgresqlReplicationSlotMetadataRequired
                && diagnostic.PropertyName == "slot.name"
                && diagnostic.ObservedValue == "unexpected-name"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
            );
        liveReadBackResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code
                    == CdcConnectorTemplateDiagnosticCodes.PostgresqlReplicationSlotMetadataRequired
                && diagnostic.PropertyName == "slot.name"
                && diagnostic.ObservedValue == "unexpected-name"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
            );
        preflightResult.Diagnostics.SelectMany(DiagnosticText).Should().NotContain("other_binding_slot");
        liveReadBackResult.Diagnostics.SelectMany(DiagnosticText).Should().NotContain("other_binding_slot");
    }

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_accepts_initial_create_or_match_provider_setup_evidence_during_registration_preflight(
        CdcProvider provider
    )
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(provider);
        CdcConnectorTemplateResult rendered = service.Render(request);
        var initialProviderSetupEvidence = new CdcConnectorProviderSetupEvidence(
            BindingGeneration,
            BuildProviderSetupResult(
                provider,
                outcome: CdcProviderSetupOutcome.CreatedOrMatched,
                mode: CdcProviderSetupMode.InitialCreateOrExactMatch
            )
        );

        CdcConnectorTemplateResult result = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                initialProviderSetupEvidence
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Config.Should().Equal(rendered.Config);
        result.Diagnostics.Should().BeEmpty();
    }

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_accepts_validate_only_exact_match_provider_setup_evidence_during_live_read_back(
        CdcProvider provider
    )
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(provider);
        CdcConnectorTemplateResult rendered = service.Render(request);

        CdcConnectorTemplateResult result = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    BindingGeneration,
                    BuildProviderSetupResult(
                        provider,
                        outcome: CdcProviderSetupOutcome.ExactMatch,
                        mode: CdcProviderSetupMode.ValidateOnly
                    )
                ),
                BuildSourcePartitionEvidence(request)
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Config.Should().Equal(rendered.Config);
        result.Diagnostics.Should().BeEmpty();
    }

    [TestCase(
        CdcProviderSetupMode.InitialCreateOrExactMatch,
        CdcProviderSetupOutcome.CreatedOrMatched,
        "providerSetup.mode",
        "providerSetup.outcome",
        TestName = "Initial create-or-match evidence is not live read-back proof"
    )]
    [TestCase(
        CdcProviderSetupMode.InitialCreateOrExactMatch,
        CdcProviderSetupOutcome.ExactMatch,
        "providerSetup.mode",
        null,
        TestName = "Initial exact-match evidence is not fresh live read-back proof"
    )]
    [TestCase(
        CdcProviderSetupMode.ValidateOnly,
        CdcProviderSetupOutcome.CreatedOrMatched,
        null,
        "providerSetup.outcome",
        TestName = "Validate-only created-or-matched evidence is not exact live read-back proof"
    )]
    [TestCase(
        CdcProviderSetupMode.ValidateOnly,
        CdcProviderSetupOutcome.Failed,
        null,
        "providerSetup.outcome",
        TestName = "Failed validate-only evidence is not live read-back proof"
    )]
    public void It_rejects_provider_setup_evidence_that_is_not_validate_only_exact_match_for_live_read_back(
        CdcProviderSetupMode mode,
        CdcProviderSetupOutcome outcome,
        string? expectedModeDiagnosticPropertyName,
        string? expectedOutcomeDiagnosticPropertyName
    )
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
                    BindingGeneration,
                    BuildProviderSetupResult(CdcProvider.Postgresql, outcome: outcome, mode: mode)
                ),
                BuildSourcePartitionEvidence(request)
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();

        if (outcome == CdcProviderSetupOutcome.Failed)
        {
            result
                .Diagnostics.Should()
                .ContainSingle(diagnostic =>
                    diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.ProviderSetupResultNotReady
                    && diagnostic.Category
                        == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                    && diagnostic.PropertyName == "providerSetup.outcome"
                    && diagnostic.ExpectedValue == "CreatedOrMatched or ExactMatch"
                    && diagnostic.ObservedValue == CdcProviderSetupOutcome.Failed.ToString()
                    && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
                    && diagnostic.RedactionClassification == CdcConnectorTemplateRedactionClassification.Safe
                );
            return;
        }

        result
            .Diagnostics.Should()
            .OnlyContain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
                && diagnostic.RedactionClassification == CdcConnectorTemplateRedactionClassification.Safe
            );
        if (expectedModeDiagnosticPropertyName is not null)
        {
            result
                .Diagnostics.Should()
                .ContainSingle(diagnostic =>
                    diagnostic.PropertyName == expectedModeDiagnosticPropertyName
                    && diagnostic.ExpectedValue == CdcProviderSetupMode.ValidateOnly.ToString()
                    && diagnostic.ObservedValue == mode.ToString()
                );
        }

        if (expectedOutcomeDiagnosticPropertyName is not null)
        {
            result
                .Diagnostics.Should()
                .ContainSingle(diagnostic =>
                    diagnostic.PropertyName == expectedOutcomeDiagnosticPropertyName
                    && diagnostic.ExpectedValue == CdcProviderSetupOutcome.ExactMatch.ToString()
                    && diagnostic.ObservedValue == outcome.ToString()
                );
        }
    }

    [Test]
    public void It_accepts_exact_externalized_and_masked_secret_read_back_values_for_sqlserver_generated_clients()
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

        CdcConnectorTemplateResult exactReadBack = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.SqlServer)
                ),
                BuildSourcePartitionEvidence(request)
            )
        );

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
        exactReadBack.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        exactReadBack.Diagnostics.Should().BeEmpty();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_empty_secret_read_back_values_as_secret_value_failures()
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        effectiveConfig["database.password"] = "";

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

        CdcConnectorTemplateDiagnostic diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackSecretMismatch
                && diagnostic.PropertyName == "database.password"
            )
            .Which;

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.SecretRedactionViolation);
        diagnostic.ExpectedValue.Should().Be("[redacted]");
        diagnostic.ObservedValue.Should().Be("[redacted]");
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.SecretValue);
    }

    [Test]
    public void It_reports_sqlserver_driver_read_back_drift_as_connection_property_diagnostics()
    {
        const string unsafeHostName = "unsafe-sql.internal";
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(
            CdcProvider.SqlServer,
            providerConnectionProperties: new Dictionary<string, string>(BuildSqlServerConnectionProperties())
            {
                ["driver.encrypt"] = "true",
                ["driver.trustStorePassword"] = "${env:CDC_SQLSERVER_TRUSTSTORE_PASSWORD}",
                ["driver.hostNameInCertificate"] = unsafeHostName,
            }
        );
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        effectiveConfig["driver.encrypt"] = "false";
        effectiveConfig.Remove("driver.trustStorePassword");
        effectiveConfig.Remove("driver.hostNameInCertificate");
        effectiveConfig["driver.trustServerCertificate"] = "true";
        effectiveConfig["driver.loginTimeout"] = "30";

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
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackPropertyMismatch
                && diagnostic.PropertyName == "driver.encrypt"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation
                && diagnostic.ExpectedValue == "[redacted]"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackPropertyMissing
                && diagnostic.PropertyName == "driver.trustStorePassword"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation
                && diagnostic.ExpectedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.SecretValue
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackPropertyMissing
                && diagnostic.PropertyName == "driver.hostNameInCertificate"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation
                && diagnostic.ExpectedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
                && diagnostic.PropertyName == "driver.trustServerCertificate"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation
                && diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
                && HasPropertyNamePrefix(diagnostic, "effectiveConfig.unexpected#")
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch
            );
        result
            .Diagnostics.Select(diagnostic => diagnostic.PropertyName)
            .Should()
            .NotContain("driver.loginTimeout");
        result
            .Diagnostics.Single(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackPropertyMissing
                && diagnostic.PropertyName == "driver.trustStorePassword"
            )
            .ObservedValue.Should()
            .BeNull();
        result
            .Diagnostics.Single(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackPropertyMissing
                && diagnostic.PropertyName == "driver.hostNameInCertificate"
            )
            .ObservedValue.Should()
            .BeNull();
    }

    [Test]
    public void It_accepts_sqlserver_source_partition_for_the_canonical_single_database_name()
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
                        ["server"] = request.ConnectorName.Value,
                        ["database"] = "edfi_datastore",
                    }
                )
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        rendered.Config.Should().Contain("database.names", "edfi_datastore");
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
                && diagnostic.ExpectedValue == "[redacted]"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.SecretRedactionViolation
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.SecretValue
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
            $"{rendered.Config["table.include.list"]},dms\\.DocumentProjectionWork";
        effectiveConfig["message.key.columns"] =
            $"{rendered.Config["message.key.columns"]};dms\\.CdcHeartbeat:HeartbeatId";
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
                && diagnostic.Category
                    == CdcConnectorTemplateDiagnosticCategory.ConverterConfigurationViolation
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "tombstones.on.delete"
                && diagnostic.Category
                    == CdcConnectorTemplateDiagnosticCategory.ConverterConfigurationViolation
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "transforms.documentState.progress.topic"
                && diagnostic.Category
                    == CdcConnectorTemplateDiagnosticCategory.TransformConfigurationViolation
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "schema.history.internal.kafka.topic"
                && diagnostic.Category
                    == CdcConnectorTemplateDiagnosticCategory.SchemaHistoryConfigurationViolation
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "topic.heartbeat.name"
                && diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "table.include.list"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.IncludeListViolation
                && diagnostic.ObservedValue == "[redacted]"
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "message.key.columns"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
                && diagnostic.ObservedValue == "[redacted]"
            )
            .And.Contain(diagnostic =>
                HasPropertyNamePrefix(diagnostic, "effectiveConfig.unexpected#")
                && diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
            );
        result
            .Diagnostics.Select(diagnostic => diagnostic.PropertyName)
            .Should()
            .NotContain("topic.creation.default.replication.factor");
    }

    [Test]
    public void It_redacts_binding_derived_topic_and_source_partition_read_back_mismatches()
    {
        const string observedTopicPrefix = "TenantBeta_connector;DROP_TABLE";
        const string observedPublicTopic = "TenantBeta.documents;DROP_TABLE";
        const string observedProgressTopic = "TenantBeta.documents.cdc-progress;DROP_TABLE";
        const string observedSchemaHistoryTopic = "TenantBeta.documents.schema-history;DROP_TABLE";
        const string observedSourceDatabase = "TenantBeta_datastore;DROP_TABLE";
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.SqlServer);
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        effectiveConfig["topic.prefix"] = observedTopicPrefix;
        effectiveConfig["transforms.documentState.target.topic"] = observedPublicTopic;
        effectiveConfig["transforms.documentState.progress.topic"] = observedProgressTopic;
        effectiveConfig["schema.history.internal.kafka.topic"] = observedSchemaHistoryTopic;
        effectiveConfig["value.converter"] = "org.example.VisibleWrongConverter";
        effectiveConfig["tombstones.on.delete"] = "true";
        effectiveConfig["producer.override.max.request.size"] = "12345";

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
                        ["server"] = observedTopicPrefix,
                        ["database"] = observedSourceDatabase,
                    }
                )
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        foreach (
            string propertyName in new[]
            {
                "topic.prefix",
                "transforms.documentState.target.topic",
                "transforms.documentState.progress.topic",
                "schema.history.internal.kafka.topic",
                "source.partition.server",
                "source.partition.database",
            }
        )
        {
            CdcConnectorTemplateDiagnostic diagnostic = result
                .Diagnostics.Should()
                .ContainSingle(diagnostic => diagnostic.PropertyName == propertyName)
                .Which;
            diagnostic.ExpectedValue.Should().Be("[redacted]");
            diagnostic.ObservedValue.Should().Be("[redacted]");
            diagnostic
                .RedactionClassification.Should()
                .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
        }

        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.PropertyName == "value.converter"
                && diagnostic.ExpectedValue == "org.edfi.kafka.connect.converters.DocumentStateJsonConverter"
                && diagnostic.ObservedValue == "org.example.VisibleWrongConverter"
                && diagnostic.RedactionClassification == CdcConnectorTemplateRedactionClassification.Safe
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "tombstones.on.delete"
                && diagnostic.ExpectedValue == "false"
                && diagnostic.ObservedValue == "true"
                && diagnostic.RedactionClassification == CdcConnectorTemplateRedactionClassification.Safe
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "producer.override.max.request.size"
                && diagnostic.ExpectedValue == "67108864"
                && diagnostic.ObservedValue == "12345"
                && diagnostic.RedactionClassification == CdcConnectorTemplateRedactionClassification.Safe
            );
        string[] sentinelValues =
        [
            observedTopicPrefix,
            observedPublicTopic,
            observedProgressTopic,
            observedSchemaHistoryTopic,
            observedSourceDatabase,
            "TenantBeta",
            "DROP_TABLE",
        ];
        result
            .Diagnostics.SelectMany(DiagnosticText)
            .Should()
            .NotContain(value =>
                sentinelValues.Any(sentinel => value.Contains(sentinel, StringComparison.Ordinal))
            );
    }

    [Test]
    public void It_redacts_postgresql_publication_and_slot_read_back_mismatches()
    {
        const string observedPublicationName = "TenantBeta_publication;DROP_TABLE";
        const string observedSlotName = "TenantBeta_slot;DROP_TABLE";
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        effectiveConfig["publication.name"] = observedPublicationName;
        effectiveConfig["slot.name"] = observedSlotName;

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
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        foreach (string propertyName in new[] { "publication.name", "slot.name" })
        {
            CdcConnectorTemplateDiagnostic diagnostic = result
                .Diagnostics.Should()
                .ContainSingle(diagnostic => diagnostic.PropertyName == propertyName)
                .Which;
            diagnostic.ExpectedValue.Should().Be("[redacted]");
            diagnostic.ObservedValue.Should().Be("[redacted]");
            diagnostic
                .RedactionClassification.Should()
                .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
        }

        string[] sentinelValues = [observedPublicationName, observedSlotName, "TenantBeta", "DROP_TABLE"];
        result
            .Diagnostics.SelectMany(DiagnosticText)
            .Should()
            .NotContain(value =>
                sentinelValues.Any(sentinel => value.Contains(sentinel, StringComparison.Ordinal))
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
        CdcConnectorTemplateDiagnostic[] unexpectedDiagnostics = result
            .Diagnostics.Where(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
            )
            .ToArray();
        unexpectedDiagnostics.Should().HaveCount(4);
        unexpectedDiagnostics
            .Should()
            .OnlyContain(diagnostic =>
                HasPropertyNamePrefix(diagnostic, "effectiveConfig.unexpected#")
                && diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        unexpectedDiagnostics
            .Should()
            .Contain(diagnostic =>
                diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation
            )
            .And.Contain(diagnostic =>
                diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch
            );
        unexpectedDiagnostics
            .Select(diagnostic => diagnostic.PropertyName)
            .Should()
            .OnlyHaveUniqueItems()
            .And.NotContain([
                "skipped.operations",
                "decimal.handling.mode",
                "signal.data.collection",
                "database.connection.string",
            ]);
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
        CdcConnectorTemplateDiagnostic[] sanitizedEffectiveConfigDiagnostics = result
            .Diagnostics.Where(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
                && diagnostic.PropertyName is not null
                && diagnostic.PropertyName.StartsWith("effectiveConfig.unexpected#", StringComparison.Ordinal)
            )
            .ToArray();
        CdcConnectorTemplateDiagnostic[] sanitizedSourcePartitionDiagnostics = result
            .Diagnostics.Where(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackSourcePartitionMismatch
                && diagnostic.PropertyName is not null
                && diagnostic.PropertyName.StartsWith(
                    "source.partition.unexpected#",
                    StringComparison.Ordinal
                )
            )
            .ToArray();

        sanitizedEffectiveConfigDiagnostics.Should().HaveCount(4);
        sanitizedSourcePartitionDiagnostics.Should().HaveCount(2);
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
                && diagnostic.PropertyName == "sasl.jaas.config"
                && diagnostic.Category
                    == CdcConnectorTemplateDiagnosticCategory.KafkaSecurityPropertyViolation
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.SecretValue
            );
        sanitizedEffectiveConfigDiagnostics
            .Should()
            .Contain(diagnostic =>
                diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.SecretValue
            )
            .And.Contain(diagnostic =>
                diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
            .And.Contain(diagnostic =>
                diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        sanitizedSourcePartitionDiagnostics
            .Should()
            .Contain(diagnostic =>
                diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
            .And.Contain(diagnostic =>
                diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.SecretValue
            );
        result
            .Diagnostics.Select(diagnostic => diagnostic.PropertyName)
            .Should()
            .NotContain([
                "unexpected.custom",
                "custom.password",
                "database.connection.string",
                "document.payload",
                "source.partition.extra",
                "source.partition.custom.password",
            ]);
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
    }

    [Test]
    public void It_sanitizes_unexpected_live_read_back_property_names_before_diagnostics_and_exception_messages()
    {
        const string unsafeEffectiveConfigPropertyName =
            "Server=unsafe-prod;Password=should-not-leak;Tenant=GrandBend;{\"documentUuid\":\"abc\"}";
        const string unsafeSourcePartitionKey = "custom.password=should-not-leak;Tenant=GrandBend";
        const string rawDocumentPayload =
            "{\"documentUuid\":\"abc\",\"studentUniqueId\":\"123\",\"tenant\":\"GrandBend\"}";
        string[] sentinelText =
        [
            "Server=unsafe-prod",
            "Password=should-not-leak",
            "Tenant=GrandBend",
            "documentUuid",
            "studentUniqueId",
            unsafeSourcePartitionKey,
        ];
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(CdcProvider.Postgresql);
        CdcConnectorTemplateResult rendered = service.Render(request);
        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        effectiveConfig[unsafeEffectiveConfigPropertyName] = rawDocumentPayload;
        var sourcePartitionEvidence = new CdcConnectorTemplateSourcePartitionEvidence(
            new Dictionary<string, string>
            {
                ["server"] = request.ConnectorName.Value,
                [unsafeSourcePartitionKey] = rawDocumentPayload,
            }
        );

        CdcConnectorTemplateResult firstResult = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                ),
                sourcePartitionEvidence
            )
        );
        CdcConnectorTemplateResult secondResult = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                effectiveConfig,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                ),
                sourcePartitionEvidence
            )
        );
        CdcConnectorTemplateDiagnostic effectiveConfigDiagnostic = firstResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackUnexpectedProperty
            )
            .Which;
        CdcConnectorTemplateDiagnostic sourcePartitionDiagnostic = firstResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackSourcePartitionMismatch
            )
            .Which;

        using var _ = new AssertionScope();
        effectiveConfigDiagnostic
            .PropertyName.Should()
            .MatchRegex("^effectiveConfig\\.unexpected#[0-9a-f]{16}$");
        effectiveConfigDiagnostic.ExpectedValue.Should().Be("absent");
        effectiveConfigDiagnostic.ObservedValue.Should().Be("[redacted]");
        sourcePartitionDiagnostic
            .PropertyName.Should()
            .MatchRegex("^source\\.partition\\.unexpected#[0-9a-f]{16}$");
        sourcePartitionDiagnostic.ExpectedValue.Should().Be("absent");
        sourcePartitionDiagnostic.ObservedValue.Should().Be("[redacted]");
        secondResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == effectiveConfigDiagnostic.Code
                && diagnostic.PropertyName == effectiveConfigDiagnostic.PropertyName
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == sourcePartitionDiagnostic.Code
                && diagnostic.PropertyName == sourcePartitionDiagnostic.PropertyName
            );
        string.Join("|", firstResult.Diagnostics.SelectMany(DiagnosticSurface))
            .Should()
            .NotContainAny(sentinelText);
    }

    [Test]
    public void It_compares_multiline_kafka_certificate_chain_read_back_values_without_leaking_mismatches()
    {
        const string expectedTruststoreCertificateChain =
            "-----BEGIN CERTIFICATE-----\nMIIDEXPECTEDTRUST\n-----END CERTIFICATE-----";
        const string expectedKeystoreCertificateChain =
            "-----BEGIN CERTIFICATE-----\r\nMIIDEXPECTEDKEY\r\n-----END CERTIFICATE-----";
        const string observedTruststoreCertificateChain =
            "-----BEGIN CERTIFICATE-----\nMIIDOBSERVEDTRUST\n-----END CERTIFICATE-----";

        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(
            CdcProvider.Postgresql,
            kafkaSecurityProperties: new Dictionary<string, string>
            {
                ["security.protocol"] = "SSL",
                ["ssl.truststore.certificates"] = expectedTruststoreCertificateChain,
                ["ssl.keystore.certificate.chain"] = expectedKeystoreCertificateChain,
            }
        );
        CdcConnectorTemplateResult rendered = service.Render(request);

        CdcConnectorTemplateResult exactReadBack = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                new CdcConnectorProviderSetupEvidence(
                    bindingGeneration: 7,
                    BuildProviderSetupResult(CdcProvider.Postgresql)
                ),
                BuildSourcePartitionEvidence(request)
            )
        );

        Dictionary<string, string> effectiveConfig = CopyConfig(rendered.Config);
        effectiveConfig["producer.override.ssl.truststore.certificates"] = observedTruststoreCertificateChain;
        CdcConnectorTemplateResult mismatch = service.ValidateLiveReadBack(
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

        CdcConnectorTemplateDiagnostic diagnostic = mismatch
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackPropertyMismatch
                && diagnostic.PropertyName == "producer.override.ssl.truststore.certificates"
            )
            .Which;

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        rendered
            .Config["producer.override.ssl.truststore.certificates"]
            .Should()
            .Be(expectedTruststoreCertificateChain);
        rendered
            .Config["producer.override.ssl.keystore.certificate.chain"]
            .Should()
            .Be(expectedKeystoreCertificateChain);
        exactReadBack.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        exactReadBack.Diagnostics.Should().BeEmpty();
        mismatch.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.ProducerPolicyViolation);
        diagnostic.ExpectedValue.Should().Be("[redacted]");
        diagnostic.ObservedValue.Should().Be("[redacted]");
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
        mismatch
            .Diagnostics.SelectMany(DiagnosticText)
            .Should()
            .NotContain(value =>
                value.Contains(expectedTruststoreCertificateChain, StringComparison.Ordinal)
                || value.Contains(expectedKeystoreCertificateChain, StringComparison.Ordinal)
                || value.Contains(observedTruststoreCertificateChain, StringComparison.Ordinal)
                || value.Contains("MIIDEXPECTED", StringComparison.Ordinal)
                || value.Contains("MIIDOBSERVED", StringComparison.Ordinal)
            );
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
                (
                    diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.ProviderSetupResultNotReady
                    || diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.BindingIdentityMismatch
                )
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
                    BuildSqlServerSnapshotIsolationArtifact(),
                    BuildSqlServerGatingRoleArtifact(),
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
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.PropertyName == "providerSetup.artifactInventory.sqlServerCaptureInstance"
                && diagnostic.ExpectedValue
                    == "one usable SQL Server capture-instance artifact for dms.Document"
                && diagnostic.ObservedValue == "Unavailable"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
            );
        liveReadBackResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code
                    == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
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
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
                && diagnostic.PropertyName == "message.key.columns"
                && diagnostic.ExpectedValue == "source column DocumentUuid for dms.DocumentCache"
                && diagnostic.ObservedValue == "missing"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
                && diagnostic.PropertyName == "message.key.columns"
                && diagnostic.ExpectedValue == "unique source column names for dms.Document"
                && diagnostic.ObservedValue == "duplicate"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
            );
        liveReadBackResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
                && diagnostic.PropertyName == "message.key.columns"
                && diagnostic.ExpectedValue == "source column DocumentUuid for dms.DocumentCache"
                && diagnostic.ObservedValue == "missing"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
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

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_rejects_malformed_fresh_heartbeat_source_inventory_for_preflight_and_live_read_back(
        CdcProvider provider
    )
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(provider);
        CdcConnectorTemplateResult rendered = service.Render(request);
        CdcConnectorProviderSetupEvidence malformedProviderSetupEvidence = new(
            bindingGeneration: 7,
            BuildProviderSetupResult(
                provider,
                sourceTableInventory: BuildHeartbeatSourceInventory(
                    provider,
                    [BuildColumn(provider, "HeartbeatId"), BuildColumn(provider, "HeartbeatAt", 2)]
                )
            )
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
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflightResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        liveReadBackResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        preflightResult.Config.Should().BeEmpty();
        liveReadBackResult.Config.Should().BeEmpty();
        preflightResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ExpectedValue == "valid CDC source table contract inventory"
                && diagnostic.ObservedValue == "malformed"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        liveReadBackResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ExpectedValue == "valid CDC source table contract inventory"
                && diagnostic.ObservedValue == "malformed"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
    }

    [TestCase(CdcProvider.Postgresql, FreshSourceInventoryDrift.AddedNonKeyColumn)]
    [TestCase(CdcProvider.Postgresql, FreshSourceInventoryDrift.RemovedNonKeyColumn)]
    [TestCase(CdcProvider.Postgresql, FreshSourceInventoryDrift.ChangedNonKeyColumnOrdinal)]
    [TestCase(CdcProvider.Postgresql, FreshSourceInventoryDrift.ChangedNonKeyColumnType)]
    [TestCase(CdcProvider.Postgresql, FreshSourceInventoryDrift.ChangedNonKeyColumnNullability)]
    [TestCase(CdcProvider.SqlServer, FreshSourceInventoryDrift.AddedNonKeyColumn)]
    [TestCase(CdcProvider.SqlServer, FreshSourceInventoryDrift.RemovedNonKeyColumn)]
    [TestCase(CdcProvider.SqlServer, FreshSourceInventoryDrift.ChangedNonKeyColumnOrdinal)]
    [TestCase(CdcProvider.SqlServer, FreshSourceInventoryDrift.ChangedNonKeyColumnType)]
    [TestCase(CdcProvider.SqlServer, FreshSourceInventoryDrift.ChangedNonKeyColumnNullability)]
    public void It_rejects_fresh_provider_source_inventory_drift_for_preflight_and_live_read_back(
        CdcProvider provider,
        FreshSourceInventoryDrift drift
    )
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(
            provider,
            sourceTableInventory: BuildDocumentSourceInventory(
                provider,
                [
                    BuildColumn(provider, "DocumentUuid"),
                    BuildColumn(provider, "DocumentPayload", 2),
                    BuildColumn(provider, "DocumentVersion", 3),
                ]
            )
        );
        CdcConnectorTemplateResult rendered = service.Render(request);
        CdcConnectorProviderSetupEvidence driftedProviderSetupEvidence = new(
            BindingGeneration,
            BuildProviderSetupResult(
                provider,
                sourceTableInventory: BuildDriftedDocumentSourceInventory(provider, drift)
            )
        );

        CdcConnectorTemplateResult preflightResult = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                driftedProviderSetupEvidence
            )
        );
        CdcConnectorTemplateResult liveReadBackResult = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                driftedProviderSetupEvidence,
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
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ExpectedValue == "rendered request source-table inventory"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        liveReadBackResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ExpectedValue == "rendered request source-table inventory"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        string.Join(
                "|",
                preflightResult.Diagnostics.Concat(liveReadBackResult.Diagnostics).SelectMany(DiagnosticText)
            )
            .Should()
            .NotContainAny(
                ["DocumentPayload", "DocumentVersion", "DocumentFutureColumn"],
                "raw source column names are redacted"
            );
    }

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_accepts_reordered_fresh_provider_source_inventory_for_preflight_and_live_read_back(
        CdcProvider provider
    )
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(provider);
        CdcConnectorTemplateResult rendered = service.Render(request);
        CdcConnectorProviderSetupEvidence reorderedProviderSetupEvidence = new(
            BindingGeneration,
            BuildProviderSetupResult(
                provider,
                sourceTableInventory: BuildRequiredSourceTableInventory(provider).Reverse().ToArray()
            )
        );

        CdcConnectorTemplateResult preflightResult = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                reorderedProviderSetupEvidence
            )
        );
        CdcConnectorTemplateResult liveReadBackResult = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                reorderedProviderSetupEvidence,
                BuildSourcePartitionEvidence(request)
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflightResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        liveReadBackResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflightResult.Config.Should().Equal(rendered.Config);
        liveReadBackResult.Config.Should().Equal(rendered.Config);
        preflightResult.Diagnostics.Should().BeEmpty();
        liveReadBackResult.Diagnostics.Should().BeEmpty();
    }

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_rejects_reordered_fresh_provider_source_columns_as_malformed_contract_inventory(
        CdcProvider provider
    )
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(
            provider,
            sourceTableInventory: BuildDocumentSourceInventory(
                provider,
                [
                    BuildColumn(provider, "DocumentUuid"),
                    BuildColumn(provider, "DocumentPayload", 2),
                    BuildColumn(provider, "DocumentVersion", 3),
                ]
            )
        );
        CdcConnectorTemplateResult rendered = service.Render(request);
        CdcConnectorProviderSetupEvidence reorderedProviderSetupEvidence = new(
            BindingGeneration,
            BuildProviderSetupResult(
                provider,
                sourceTableInventory: BuildDocumentSourceInventory(
                    provider,
                    [
                        BuildColumn(provider, "DocumentUuid"),
                        BuildColumn(provider, "DocumentVersion", 3),
                        BuildColumn(provider, "DocumentPayload", 2),
                    ]
                )
            )
        );

        CdcConnectorTemplateResult preflightResult = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                reorderedProviderSetupEvidence
            )
        );
        CdcConnectorTemplateResult liveReadBackResult = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                reorderedProviderSetupEvidence,
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
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ExpectedValue == "valid CDC source table contract inventory"
                && diagnostic.ObservedValue == "malformed"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        liveReadBackResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ExpectedValue == "valid CDC source table contract inventory"
                && diagnostic.ObservedValue == "malformed"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
    }

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_accepts_reordered_fresh_provider_message_key_inventory_for_preflight_and_live_read_back(
        CdcProvider provider
    )
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(provider);
        CdcConnectorTemplateResult rendered = service.Render(request);
        CdcConnectorProviderSetupEvidence reorderedProviderSetupEvidence = new(
            BindingGeneration,
            BuildProviderSetupResult(
                provider,
                expectedMessageKeyColumns: BuildExpectedMessageKeyColumns().Reverse().ToArray()
            )
        );

        CdcConnectorTemplateResult preflightResult = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                reorderedProviderSetupEvidence
            )
        );
        CdcConnectorTemplateResult liveReadBackResult = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                reorderedProviderSetupEvidence,
                BuildSourcePartitionEvidence(request)
            )
        );

        using var _ = new AssertionScope();
        rendered.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflightResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        liveReadBackResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        preflightResult.Config.Should().Equal(rendered.Config);
        liveReadBackResult.Config.Should().Equal(rendered.Config);
        preflightResult.Diagnostics.Should().BeEmpty();
        liveReadBackResult.Diagnostics.Should().BeEmpty();
    }

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_rejects_invalid_fresh_provider_message_key_inventory_for_preflight_and_live_read_back(
        CdcProvider provider
    )
    {
        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(provider);
        CdcConnectorTemplateResult rendered = service.Render(request);
        CdcConnectorProviderSetupEvidence driftedProviderSetupEvidence = new(
            BindingGeneration,
            BuildProviderSetupResult(
                provider,
                expectedMessageKeyColumns:
                [
                    new(CdcSourceTableKind.DocumentCache, [new DbColumnName("DocumentUuid")]),
                    new(CdcSourceTableKind.Document, [new DbColumnName("DocumentId")]),
                ]
            )
        );

        CdcConnectorTemplateResult preflightResult = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                driftedProviderSetupEvidence
            )
        );
        CdcConnectorTemplateResult liveReadBackResult = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                driftedProviderSetupEvidence,
                BuildSourcePartitionEvidence(request)
            )
        );

        using var _ = new AssertionScope();
        preflightResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        liveReadBackResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        preflightResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
                && diagnostic.PropertyName == "providerSetup.expectedMessageKeyColumns"
                && diagnostic.ExpectedValue == "DocumentUuid keys for document sources"
                && diagnostic.ObservedValue == "2"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        liveReadBackResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
                && diagnostic.PropertyName == "providerSetup.expectedMessageKeyColumns"
                && diagnostic.ExpectedValue == "DocumentUuid keys for document sources"
                && diagnostic.ObservedValue == "2"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
    }

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_rejects_fresh_provider_heartbeat_action_query_that_no_longer_matches_rendered_request(
        CdcProvider provider
    )
    {
        const string renderedHeartbeatSql =
            "update dms.CdcHeartbeat set HeartbeatSequence = HeartbeatSequence + 1";
        const string freshHeartbeatSql =
            "update unsafe.TenantAlphaHeartbeat set SecretTenant = 'TenantAlpha'";

        using ServiceProvider serviceProvider = BuildServiceProvider();
        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();
        CdcConnectorTemplateRequest request = BuildRequest(provider, heartbeatSql: renderedHeartbeatSql);
        CdcConnectorTemplateResult rendered = service.Render(request);
        CdcConnectorProviderSetupEvidence driftedProviderSetupEvidence = new(
            BindingGeneration,
            BuildProviderSetupResult(
                provider,
                heartbeatActionQuery: new CdcHeartbeatActionQuery(freshHeartbeatSql, "sha256-drifted")
            )
        );

        CdcConnectorTemplateResult preflightResult = service.ValidateRegistrationPreflight(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                driftedProviderSetupEvidence
            )
        );
        CdcConnectorTemplateResult liveReadBackResult = service.ValidateLiveReadBack(
            new CdcConnectorTemplateEffectiveConfigValidationRequest(
                request,
                rendered.Config,
                driftedProviderSetupEvidence,
                BuildSourcePartitionEvidence(request)
            )
        );

        using var _ = new AssertionScope();
        preflightResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        liveReadBackResult.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        preflightResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch
                && diagnostic.PropertyName == "providerSetup.heartbeatActionQuery"
                && diagnostic.ExpectedValue == "rendered request heartbeat action query"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        liveReadBackResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.LiveReadBackProviderSetupMismatch
                && diagnostic.PropertyName == "providerSetup.heartbeatActionQuery"
                && diagnostic.ExpectedValue == "rendered request heartbeat action query"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        string.Join(
                "|",
                preflightResult.Diagnostics.Concat(liveReadBackResult.Diagnostics).SelectMany(DiagnosticText)
            )
            .Should()
            .NotContainAny(["TenantAlpha", "SecretTenant", "HeartbeatSequence"], "heartbeat SQL is redacted");
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
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ExpectedValue == "dms.DocumentCache, dms.Document, and dms.CdcHeartbeat"
                && diagnostic.ObservedValue == "missing"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
                && diagnostic.PropertyName == "providerSetup.expectedMessageKeyColumns"
                && diagnostic.ExpectedValue == "DocumentUuid keys for document sources"
                && diagnostic.ObservedValue == "missing"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
            );
        liveReadBackResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ExpectedValue == "dms.DocumentCache, dms.Document, and dms.CdcHeartbeat"
                && diagnostic.ObservedValue == "missing"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
                && diagnostic.PropertyName == "providerSetup.expectedMessageKeyColumns"
                && diagnostic.ExpectedValue == "DocumentUuid keys for document sources"
                && diagnostic.ObservedValue == "missing"
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
    public void It_rejects_null_nested_fresh_provider_setup_evidence_for_preflight_and_live_read_back()
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
                SourceTableInventory =
                [
                    BuildSourceTable(
                        CdcProvider.Postgresql,
                        CdcSourceTableKind.DocumentCache,
                        "DocumentCache",
                        [BuildColumn(CdcProvider.Postgresql, "DocumentUuid")]
                    ),
                    null!,
                    BuildSourceTable(
                        CdcProvider.Postgresql,
                        CdcSourceTableKind.CdcHeartbeat,
                        "CdcHeartbeat",
                        [
                            BuildColumn(CdcProvider.Postgresql, "HeartbeatId"),
                            BuildColumn(CdcProvider.Postgresql, "HeartbeatSequence", 2),
                            BuildColumn(CdcProvider.Postgresql, "HeartbeatAt", 3),
                        ]
                    ),
                ],
                ExpectedMessageKeyColumns =
                [
                    new(CdcSourceTableKind.DocumentCache, [new DbColumnName("DocumentUuid")]),
                    new(CdcSourceTableKind.Document, null!),
                ],
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
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ExpectedValue == "dms.DocumentCache, dms.Document, and dms.CdcHeartbeat"
                && diagnostic.ObservedValue == "3"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
                && diagnostic.PropertyName == "providerSetup.expectedMessageKeyColumns"
                && diagnostic.ExpectedValue == "DocumentUuid keys for document sources"
                && diagnostic.ObservedValue == "2"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Preflight
            );
        liveReadBackResult
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.PropertyName == "providerSetup.sourceTableInventory"
                && diagnostic.ExpectedValue == "dms.DocumentCache, dms.Document, and dms.CdcHeartbeat"
                && diagnostic.ObservedValue == "3"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.LiveReadBack
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
                && diagnostic.PropertyName == "providerSetup.expectedMessageKeyColumns"
                && diagnostic.ExpectedValue == "DocumentUuid keys for document sources"
                && diagnostic.ObservedValue == "2"
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
                && diagnostic.ExpectedValue == "[redacted]"
                && diagnostic.ObservedValue == "[redacted]"
            )
            .And.Contain(diagnostic =>
                diagnostic.PropertyName == "source.partition.database"
                && diagnostic.ExpectedValue == "[redacted]"
                && diagnostic.ObservedValue == "[redacted]"
            )
            .And.Contain(diagnostic =>
                HasPropertyNamePrefix(diagnostic, "source.partition.unexpected#")
                && diagnostic.ExpectedValue == "absent"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        result
            .Diagnostics.Select(diagnostic => diagnostic.PropertyName)
            .Should()
            .NotContain("source.partition.extra");
        result
            .Diagnostics.SelectMany(diagnostic =>
                new[] { diagnostic.ExpectedValue, diagnostic.ObservedValue }
            )
            .Where(value => value is not null)
            .Should()
            .NotContain(value =>
                value!.Contains("dms_binding_connector", StringComparison.Ordinal)
                || value.Contains("different_connector", StringComparison.Ordinal)
                || value.Contains("other_datastore", StringComparison.Ordinal)
            );
    }

    private static ServiceProvider BuildServiceProvider() =>
        new ServiceCollection().AddCdcConnectorTemplates().BuildServiceProvider();

    private static Dictionary<string, string> CopyConfig(IReadOnlyDictionary<string, string> config) =>
        config.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static IReadOnlyList<string> SqlServerGeneratedSecretPropertyNames() =>
        [
            "database.password",
            "driver.trustStorePassword",
            "producer.override.sasl.jaas.config",
            "schema.history.internal.consumer.sasl.jaas.config",
            "schema.history.internal.producer.sasl.jaas.config",
        ];

    private static IEnumerable<string> DiagnosticText(CdcConnectorTemplateDiagnostic diagnostic) =>
        [diagnostic.ExpectedValue ?? string.Empty, diagnostic.ObservedValue ?? string.Empty];

    private static IEnumerable<string> DiagnosticSurface(CdcConnectorTemplateDiagnostic diagnostic) =>
        [
            diagnostic.PropertyName ?? string.Empty,
            diagnostic.SafeArtifactOrObjectName?.Value ?? string.Empty,
            diagnostic.ExpectedValue ?? string.Empty,
            diagnostic.ObservedValue ?? string.Empty,
            diagnostic.ToString(),
        ];

    private static bool HasPropertyNamePrefix(
        CdcConnectorTemplateDiagnostic diagnostic,
        string propertyNamePrefix
    ) =>
        diagnostic.PropertyName is not null
        && diagnostic.PropertyName.StartsWith(propertyNamePrefix, StringComparison.Ordinal);

    private static IReadOnlyList<CdcSourceTableInventory> BuildDocumentSourceInventory(
        CdcProvider provider,
        IReadOnlyList<CdcSourceColumnInventory> documentColumns
    ) =>
        BuildSourceInventoryReplacing(
            provider,
            BuildSourceTable(provider, CdcSourceTableKind.Document, "Document", documentColumns)
        );

    private static IReadOnlyList<CdcSourceTableInventory> BuildDriftedDocumentSourceInventory(
        CdcProvider provider,
        FreshSourceInventoryDrift drift
    ) =>
        drift switch
        {
            FreshSourceInventoryDrift.AddedNonKeyColumn => BuildDocumentSourceInventory(
                provider,
                [
                    BuildColumn(provider, "DocumentUuid"),
                    BuildColumn(provider, "DocumentPayload", 2),
                    BuildColumn(provider, "DocumentVersion", 3),
                    BuildColumn(provider, "DocumentFutureColumn", 4),
                ]
            ),
            FreshSourceInventoryDrift.RemovedNonKeyColumn => BuildDocumentSourceInventory(
                provider,
                [BuildColumn(provider, "DocumentUuid"), BuildColumn(provider, "DocumentVersion", 2)]
            ),
            FreshSourceInventoryDrift.ChangedNonKeyColumnOrdinal => BuildDocumentSourceInventory(
                provider,
                [
                    BuildColumn(provider, "DocumentUuid"),
                    BuildColumn(provider, "DocumentVersion", 2),
                    BuildColumn(provider, "DocumentPayload", 3),
                ]
            ),
            FreshSourceInventoryDrift.ChangedNonKeyColumnType => BuildDocumentSourceInventory(
                provider,
                [
                    BuildColumn(provider, "DocumentUuid"),
                    BuildColumn(provider, "DocumentPayload", 2, providerDataType: "jsonb"),
                    BuildColumn(provider, "DocumentVersion", 3),
                ]
            ),
            FreshSourceInventoryDrift.ChangedNonKeyColumnNullability => BuildDocumentSourceInventory(
                provider,
                [
                    BuildColumn(provider, "DocumentUuid"),
                    BuildColumn(provider, "DocumentPayload", 2, isNullable: true),
                    BuildColumn(provider, "DocumentVersion", 3),
                ]
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(drift), drift, "Unsupported inventory drift."),
        };

    public enum FreshSourceInventoryDrift
    {
        AddedNonKeyColumn,
        RemovedNonKeyColumn,
        ChangedNonKeyColumnOrdinal,
        ChangedNonKeyColumnType,
        ChangedNonKeyColumnNullability,
    }
}
