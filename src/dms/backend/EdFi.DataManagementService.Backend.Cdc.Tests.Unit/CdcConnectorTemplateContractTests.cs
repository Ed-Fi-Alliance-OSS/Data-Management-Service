// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateContract")]
public class Given_CdcConnectorTemplateContractTests
{
    [Test]
    public void It_derives_progress_and_sqlserver_schema_history_topics_from_the_binding_topic()
    {
        CdcConnectorTemplateBindingIdentity postgresqlBinding = BuildBinding(CdcProvider.Postgresql);
        CdcConnectorTemplateBindingIdentity sqlServerBinding = BuildBinding(CdcProvider.SqlServer);
        CdcConnectorTemplateRequest postgresqlRequest = BuildRequest(
            BuildProviderSetupResult(CdcProvider.Postgresql),
            binding: postgresqlBinding
        );

        using var _ = new AssertionScope();
        postgresqlBinding.ProgressTopicName.Should().Be("edfi.documents.cdc-progress");
        postgresqlBinding.SchemaHistoryTopicName.Should().BeNull();
        sqlServerBinding.ProgressTopicName.Should().Be("edfi.documents.cdc-progress");
        sqlServerBinding.SchemaHistoryTopicName.Should().Be("edfi.documents.schema-history");
        postgresqlRequest.ProgressTopicName.Should().Be(postgresqlBinding.ProgressTopicName);
    }

    [Test]
    public void It_accepts_valid_connector_and_kafka_topic_binding_names()
    {
        CdcConnectorTemplateBindingIdentity binding = BuildBindingWithIdentityValues(
            CdcProvider.SqlServer,
            connectorName: "dms.binding-connector_01",
            publicTopicName: "edfi.documents-v1_2026"
        );

        using var _ = new AssertionScope();
        binding.ConnectorName.Should().Be(new CdcSafeName("dms.binding-connector_01"));
        binding.PublicTopicName.Should().Be("edfi.documents-v1_2026");
        binding.ProgressTopicName.Should().Be("edfi.documents-v1_2026.cdc-progress");
        binding.SchemaHistoryTopicName.Should().Be("edfi.documents-v1_2026.schema-history");
    }

    [Test]
    public void It_rejects_connector_names_that_are_not_valid_debezium_topic_prefixes()
    {
        string[] invalidConnectorNames =
        [
            "dms binding connector",
            "dms/binding/connector",
            "dms:binding:connector",
            "dms(binding)connector",
            new('a', 250),
            ".",
            "..",
        ];

        using var _ = new AssertionScope();
        foreach (string invalidConnectorName in invalidConnectorNames)
        {
            Action act = () =>
                BuildBindingWithIdentityValues(
                    CdcProvider.Postgresql,
                    connectorName: invalidConnectorName,
                    publicTopicName: "edfi.documents"
                );

            act.Should()
                .Throw<ArgumentException>()
                .WithParameterName("connectorName")
                .WithMessage("*Kafka topic names*");
        }
    }

    [Test]
    public void It_rejects_public_topic_names_that_are_not_valid_kafka_topic_names()
    {
        string[] invalidPublicTopicNames =
        [
            "edfi documents",
            "edfi/documents",
            "edfi:documents",
            "edfi(documents)",
            new('a', 250),
            ".",
            "..",
        ];

        using var _ = new AssertionScope();
        foreach (string invalidPublicTopicName in invalidPublicTopicNames)
        {
            Action act = () =>
                BuildBindingWithIdentityValues(
                    CdcProvider.Postgresql,
                    connectorName: "dms_binding_connector",
                    publicTopicName: invalidPublicTopicName
                );

            act.Should()
                .Throw<ArgumentException>()
                .WithParameterName("publicTopicName")
                .WithMessage("*Kafka topic names*");
        }
    }

    [Test]
    public void It_rejects_derived_topic_names_that_are_not_valid_kafka_topic_names()
    {
        Action overlongProgressTopic = () =>
            BuildBindingWithIdentityValues(
                CdcProvider.Postgresql,
                connectorName: "dms_binding_connector",
                publicTopicName: new string('a', 237)
            );
        Action overlongSchemaHistoryTopic = () =>
            BuildBindingWithIdentityValues(
                CdcProvider.SqlServer,
                connectorName: "dms_binding_connector",
                publicTopicName: new string('a', 235)
            );

        using var _ = new AssertionScope();
        overlongProgressTopic
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("progressTopicName")
            .WithMessage("*Kafka topic names*");
        overlongSchemaHistoryTopic
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("schemaHistoryTopicName")
            .WithMessage("*Kafka topic names*");
    }

    [Test]
    public void It_accepts_successful_provider_setup_evidence_that_matches_the_binding()
    {
        var policy = new CdcConnectorTemplateDeploymentPolicy(
            kafkaBootstrapServers: "broker-1:9092,broker-2:9092",
            maxRecordBytes: 1_048_576,
            producerBufferBytes: 33_554_432,
            heartbeatInterval: TimeSpan.FromSeconds(5),
            sqlServerPollInterval: TimeSpan.FromSeconds(1)
        );
        var providerConnectionProperties = new CdcProviderConnectionProperties(
            CdcProvider.Postgresql,
            new Dictionary<string, string>
            {
                ["database.hostname"] = "postgresql.internal",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
            }
        );
        var kafkaSecurityProperties = new CdcKafkaClientSecurityProperties(
            new Dictionary<string, string>
            {
                ["security.protocol"] = "SASL_SSL",
                ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS}",
            }
        );
        var artifactOutput = new CdcConnectorTemplateArtifactOutputRequest(
            includeRedactedArtifactPayload: false,
            manifestOutputDirectoryPath: "/tmp/cdc-template-artifacts"
        );

        var request = new CdcConnectorTemplateRequest(
            BuildBinding(CdcProvider.Postgresql),
            new CdcConnectorProviderSetupEvidence(
                bindingGeneration: 7,
                BuildProviderSetupResult(CdcProvider.Postgresql)
            ),
            policy,
            providerConnectionProperties,
            kafkaSecurityProperties,
            artifactOutput
        );

        using var _ = new AssertionScope();
        request.Provider.Should().Be(CdcProvider.Postgresql);
        request.ConnectorName.Should().Be(new CdcSafeName("dms_binding_connector"));
        request.PublicTopicName.Should().Be("edfi.documents");
        request.ProgressTopicName.Should().Be("edfi.documents.cdc-progress");
        request.SchemaHistoryTopicName.Should().BeNull();
        request
            .PartitionerAlgorithm.Should()
            .Be(CdcConnectorTemplateBindingIdentity.KafkaMurmur2V1PartitionerAlgorithm);
        request.DeploymentPolicy.Should().BeSameAs(policy);
        request.ProviderConnectionProperties.Should().BeSameAs(providerConnectionProperties);
        request.KafkaClientSecurityProperties.Should().BeSameAs(kafkaSecurityProperties);
        request.ArtifactOutput.Should().BeSameAs(artifactOutput);
        request
            .ProviderArtifactNames.Postgresql!.PublicationName.Should()
            .Be(new CdcSafeName("dms_binding_publication"));
        artifactOutput.IncludeRedactedArtifactPayload.Should().BeTrue();
    }

    [Test]
    public void It_requires_positive_binding_generations()
    {
        Action zeroBindingGeneration = () =>
            new CdcConnectorTemplateBindingIdentity(
                CdcProvider.Postgresql,
                new CdcSafeName("dms_binding_connector"),
                "edfi.documents",
                bindingGeneration: 0,
                partitionerAlgorithm: "kafka-murmur2-v1",
                BuildProviderArtifactNames(CdcProvider.Postgresql),
                SourceFingerprint
            );
        Action negativeBindingGeneration = () =>
            new CdcConnectorTemplateBindingIdentity(
                CdcProvider.Postgresql,
                new CdcSafeName("dms_binding_connector"),
                "edfi.documents",
                bindingGeneration: -1,
                partitionerAlgorithm: "kafka-murmur2-v1",
                BuildProviderArtifactNames(CdcProvider.Postgresql),
                SourceFingerprint
            );
        Action zeroProviderSetupGeneration = () =>
            new CdcConnectorProviderSetupEvidence(
                bindingGeneration: 0,
                BuildProviderSetupResult(CdcProvider.Postgresql)
            );
        Action negativeProviderSetupGeneration = () =>
            new CdcConnectorProviderSetupEvidence(
                bindingGeneration: -1,
                BuildProviderSetupResult(CdcProvider.Postgresql)
            );

        using var _ = new AssertionScope();
        zeroBindingGeneration
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("bindingGeneration")
            .WithMessage("*positive integer*");
        negativeBindingGeneration
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("bindingGeneration")
            .WithMessage("*positive integer*");
        zeroProviderSetupGeneration
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("bindingGeneration")
            .WithMessage("*positive integer*");
        negativeProviderSetupGeneration
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("bindingGeneration")
            .WithMessage("*positive integer*");
    }

    [TestCase(
        1_048_576,
        2_097_152,
        TestName = "Explicit buffer above max record bytes but below design floor"
    )]
    [TestCase(
        67_108_864,
        33_554_432,
        TestName = "Explicit buffer below max record bytes when max record raises floor"
    )]
    public void It_rejects_explicit_producer_buffer_bytes_below_design_floor(
        int maxRecordBytes,
        int producerBufferBytes
    )
    {
        Action act = () =>
            new CdcConnectorTemplateDeploymentPolicy(
                kafkaBootstrapServers: "broker:9092",
                maxRecordBytes: maxRecordBytes,
                producerBufferBytes: producerBufferBytes
            );

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("producerBufferBytes")
            .WithMessage("*producerBufferBytes*greater than or equal to max(33554432, maxRecordBytes)*");
    }

    [Test]
    public void It_requires_the_binding_partitioner_algorithm_contract_token()
    {
        Action missingPartitionerAlgorithm = () =>
            new CdcConnectorTemplateBindingIdentity(
                CdcProvider.Postgresql,
                new CdcSafeName("dms_binding_connector"),
                "edfi.documents",
                bindingGeneration: 7,
                partitionerAlgorithm: null!,
                BuildProviderArtifactNames(CdcProvider.Postgresql),
                SourceFingerprint
            );
        Action emptyPartitionerAlgorithm = () =>
            new CdcConnectorTemplateBindingIdentity(
                CdcProvider.Postgresql,
                new CdcSafeName("dms_binding_connector"),
                "edfi.documents",
                bindingGeneration: 7,
                partitionerAlgorithm: "",
                BuildProviderArtifactNames(CdcProvider.Postgresql),
                SourceFingerprint
            );
        Action unsupportedPartitionerAlgorithm = () =>
            new CdcConnectorTemplateBindingIdentity(
                CdcProvider.Postgresql,
                new CdcSafeName("dms_binding_connector"),
                "edfi.documents",
                bindingGeneration: 7,
                partitionerAlgorithm: "round-robin",
                BuildProviderArtifactNames(CdcProvider.Postgresql),
                SourceFingerprint
            );

        using var _ = new AssertionScope();
        missingPartitionerAlgorithm
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("partitionerAlgorithm");
        emptyPartitionerAlgorithm
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("partitionerAlgorithm");
        unsupportedPartitionerAlgorithm
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*kafka-murmur2-v1*")
            .WithParameterName("partitionerAlgorithm");
    }

    [Test]
    public void It_requires_binding_artifact_names_for_the_binding_provider()
    {
        Action postgresqlWithSqlServerNames = () =>
            new CdcConnectorTemplateBindingIdentity(
                CdcProvider.Postgresql,
                new CdcSafeName("dms_binding_connector"),
                "edfi.documents",
                bindingGeneration: 7,
                partitionerAlgorithm: "kafka-murmur2-v1",
                BuildProviderArtifactNames(CdcProvider.SqlServer),
                SourceFingerprint
            );
        Action sqlServerWithPostgresqlNames = () =>
            new CdcConnectorTemplateBindingIdentity(
                CdcProvider.SqlServer,
                new CdcSafeName("dms_binding_connector"),
                "edfi.documents",
                bindingGeneration: 7,
                partitionerAlgorithm: "kafka-murmur2-v1",
                BuildProviderArtifactNames(CdcProvider.Postgresql),
                SourceFingerprintFor(CdcProvider.SqlServer)
            );

        using var _ = new AssertionScope();
        postgresqlWithSqlServerNames
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("providerArtifactNames")
            .WithMessage("*provider Postgresql*");
        sqlServerWithPostgresqlNames
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("providerArtifactNames")
            .WithMessage("*provider SqlServer*");
    }

    [Test]
    public void It_requires_the_binding_source_fingerprint_version_and_sha256_shape()
    {
        var invalidFingerprints = new[]
        {
            new CdcSourceFingerprint("dms-source-fingerprint-v0", ValidFingerprintValue()),
            new CdcSourceFingerprint(CdcSourceFingerprintMetadata.Version, new string('a', 64)),
            new CdcSourceFingerprint(CdcSourceFingerprintMetadata.Version, $"sha256:{new string('A', 64)}"),
            new CdcSourceFingerprint(CdcSourceFingerprintMetadata.Version, $"sha256:{new string('g', 64)}"),
            new CdcSourceFingerprint(CdcSourceFingerprintMetadata.Version, $"sha256:{new string('a', 63)}"),
        };

        using var _ = new AssertionScope();
        foreach (CdcSourceFingerprint invalidFingerprint in invalidFingerprints)
        {
            Action act = () =>
                new CdcConnectorTemplateBindingIdentity(
                    CdcProvider.Postgresql,
                    new CdcSafeName("dms_binding_connector"),
                    "edfi.documents",
                    bindingGeneration: 7,
                    partitionerAlgorithm: "kafka-murmur2-v1",
                    BuildProviderArtifactNames(CdcProvider.Postgresql),
                    invalidFingerprint
                );

            act.Should().Throw<ArgumentException>().WithParameterName("boundPhysicalSourceFingerprint");
        }
    }

    [Test]
    public void It_requires_provider_setup_evidence_source_fingerprints_to_have_valid_version_and_sha256_shape()
    {
        var malformedBoundFingerprint = new CdcSourceFingerprint(
            "dms-source-fingerprint-v0",
            ValidFingerprintValue()
        );
        var malformedObservedFingerprint = new CdcSourceFingerprint(
            CdcSourceFingerprintMetadata.Version,
            $"sha256:{new string('g', 64)}"
        );

        Action invalidBound = () =>
            new CdcConnectorProviderSetupEvidence(
                BindingGeneration,
                BuildProviderSetupResult(
                    CdcProvider.Postgresql,
                    boundPhysicalSourceFingerprint: malformedBoundFingerprint
                )
            );
        Action invalidObserved = () =>
            new CdcConnectorProviderSetupEvidence(
                BindingGeneration,
                BuildProviderSetupResult(
                    CdcProvider.Postgresql,
                    observedSourceFingerprint: malformedObservedFingerprint
                )
            );

        using var _ = new AssertionScope();
        invalidBound
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("result.BoundPhysicalSourceFingerprint");
        invalidObserved
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("result.ObservedSourceFingerprint");
    }

    [Test]
    public void It_preserves_failed_provider_setup_results_for_standard_validation()
    {
        CdcConnectorTemplateRequest request = BuildRequest(
            BuildProviderSetupResult(CdcProvider.Postgresql, CdcProviderSetupOutcome.Failed)
        );

        request.ProviderSetupEvidence.Result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
    }

    [Test]
    public void It_preserves_provider_setup_evidence_that_does_not_match_binding_identity_for_standard_validation()
    {
        CdcConnectorTemplateRequest wrongProvider = BuildRequest(
            BuildProviderSetupResult(CdcProvider.SqlServer),
            binding: BuildBinding(CdcProvider.Postgresql),
            providerConnectionProperties: new CdcProviderConnectionProperties(
                CdcProvider.Postgresql,
                BuildRequiredProviderConnectionProperties(CdcProvider.Postgresql)
            )
        );
        CdcConnectorTemplateRequest wrongGeneration = BuildRequest(
            BuildProviderSetupResult(CdcProvider.Postgresql),
            providerSetupBindingGeneration: 8
        );
        CdcConnectorTemplateRequest wrongFingerprint = BuildRequest(
            BuildProviderSetupResult(
                CdcProvider.Postgresql,
                boundPhysicalSourceFingerprint: OtherPostgresqlSourceFingerprint
            )
        );

        using var _ = new AssertionScope();
        wrongProvider.ProviderSetupEvidence.Result.Provider.Should().Be(CdcProvider.SqlServer);
        wrongProvider.Provider.Should().Be(CdcProvider.Postgresql);
        wrongGeneration.ProviderSetupEvidence.BindingGeneration.Should().Be(8);
        wrongFingerprint
            .ProviderSetupEvidence.Result.BoundPhysicalSourceFingerprint.Should()
            .Be(OtherPostgresqlSourceFingerprint);
    }

    [Test]
    public void It_preserves_provider_setup_evidence_without_required_source_key_and_heartbeat_inventory_for_standard_validation()
    {
        CdcConnectorTemplateRequest missingSourceInventory = BuildRequest(
            BuildProviderSetupResult(CdcProvider.Postgresql, sourceTableInventory: [])
        );
        CdcConnectorTemplateRequest missingDocumentUuidKeyInventory = BuildRequest(
            BuildProviderSetupResult(CdcProvider.Postgresql, expectedMessageKeyColumns: [])
        );
        CdcConnectorTemplateRequest missingHeartbeatActionQuery = BuildRequest(
            BuildProviderSetupResult(CdcProvider.Postgresql, omitHeartbeatActionQuery: true)
        );

        using var _ = new AssertionScope();
        missingSourceInventory.ProviderSetupEvidence.Result.SourceTableInventory.Should().BeEmpty();
        missingDocumentUuidKeyInventory
            .ProviderSetupEvidence.Result.ExpectedMessageKeyColumns.Should()
            .BeEmpty();
        missingHeartbeatActionQuery.ProviderSetupEvidence.Result.HeartbeatActionQuery.Should().BeNull();
    }

    [Test]
    public void It_does_not_accept_raw_connector_json_or_operator_configurable_derived_topics()
    {
        var constructorParameterNames = typeof(CdcConnectorTemplateRequest)
            .GetConstructors()
            .Should()
            .ContainSingle()
            .Subject.GetParameters()
            .Select(parameter => parameter.Name ?? string.Empty)
            .ToArray();
        string[] forbiddenParameterNames =
        [
            "rawConnectorJson",
            "connectorConfigJson",
            "tenantDisplayName",
            "connectionString",
            "partitionerClass",
            "progressTopicName",
            "schemaHistoryTopicName",
        ];

        using var _ = new AssertionScope();
        constructorParameterNames.Should().Contain("bindingIdentity");
        constructorParameterNames.Should().Contain("providerSetupEvidence");
        constructorParameterNames.Should().Contain("deploymentPolicy");
        constructorParameterNames.Should().Contain("providerConnectionProperties");
        constructorParameterNames.Should().Contain("kafkaClientSecurityProperties");
        constructorParameterNames.Should().NotContain(name => forbiddenParameterNames.Contains(name));
    }

    [Test]
    public void It_exposes_a_consumer_facing_result_with_registration_payload_artifact_hash_and_diagnostics()
    {
        CdcConnectorTemplateBindingIdentity binding = BuildBinding(CdcProvider.SqlServer);
        var config = new Dictionary<string, string>
        {
            ["name"] = binding.ConnectorName.Value,
            ["topic.prefix"] = binding.ConnectorName.Value,
        };
        var registrationPayload = new CdcKafkaConnectRegistrationPayload(binding.ConnectorName, config);
        var artifactPayload = new CdcConnectorTemplateArtifactPayload(
            new CdcSafeName("cdc-connector-template.sqlserver.dms_binding_connector.manifest.json"),
            """{"redactedConfig":{"database.password":"[redacted]"}}"""
        );
        var diagnostic = new CdcConnectorTemplateDiagnostic(
            code: "CDC_TEMPLATE_CONTRACT_SENTINEL",
            category: CdcConnectorTemplateDiagnosticCategory.BindingIdentityFailure,
            severity: CdcConnectorTemplateDiagnosticSeverity.Info,
            propertyName: "topic.prefix",
            safeArtifactOrObjectName: binding.ConnectorName,
            expectedValue: binding.ConnectorName.Value,
            observedValue: binding.ConnectorName.Value,
            provider: CdcProvider.SqlServer,
            sourcePhase: CdcConnectorTemplateSourcePhase.Render,
            redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
        );

        var result = new CdcConnectorTemplateResult(
            binding,
            CdcConnectorTemplateOutcome.Rendered,
            config,
            registrationPayload,
            artifactPayload,
            "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            [diagnostic]
        );

        using var _ = new AssertionScope();
        result.Provider.Should().Be(CdcProvider.SqlServer);
        result.ConnectorName.Should().Be(binding.ConnectorName);
        result.PublicTopicName.Should().Be("edfi.documents");
        result.ProgressTopicName.Should().Be("edfi.documents.cdc-progress");
        result.SchemaHistoryTopicName.Should().Be("edfi.documents.schema-history");
        result.RegistrationPayload.Should().BeSameAs(registrationPayload);
        result.RedactedArtifactPayload.Should().BeSameAs(artifactPayload);
        result
            .ConfigSha256.Should()
            .Be("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        result.Diagnostics.Should().ContainSingle().Which.Should().BeSameAs(diagnostic);
    }

    [Test]
    public void It_snapshots_result_diagnostics_from_caller_owned_collections()
    {
        CdcConnectorTemplateBindingIdentity binding = BuildBinding(CdcProvider.Postgresql);
        var config = new Dictionary<string, string> { ["name"] = binding.ConnectorName.Value };
        var diagnostic = new CdcConnectorTemplateDiagnostic(
            code: "CDC_TEMPLATE_CONTRACT_SENTINEL",
            category: CdcConnectorTemplateDiagnosticCategory.BindingIdentityFailure,
            severity: CdcConnectorTemplateDiagnosticSeverity.Info,
            propertyName: "topic.prefix",
            safeArtifactOrObjectName: binding.ConnectorName,
            expectedValue: binding.ConnectorName.Value,
            observedValue: binding.ConnectorName.Value,
            provider: CdcProvider.Postgresql,
            sourcePhase: CdcConnectorTemplateSourcePhase.Render,
            redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
        );
        var addedAfterConstruction = new CdcConnectorTemplateDiagnostic(
            code: "CDC_TEMPLATE_MUTATED_SENTINEL",
            category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
            severity: CdcConnectorTemplateDiagnosticSeverity.Error,
            propertyName: "database.password",
            safeArtifactOrObjectName: binding.ConnectorName,
            expectedValue: "[redacted]",
            observedValue: "[redacted]",
            provider: CdcProvider.Postgresql,
            sourcePhase: CdcConnectorTemplateSourcePhase.LiveReadBack,
            redactionClassification: CdcConnectorTemplateRedactionClassification.SecretValue
        );
        List<CdcConnectorTemplateDiagnostic> diagnostics = [diagnostic];

        var result = new CdcConnectorTemplateResult(
            binding,
            CdcConnectorTemplateOutcome.ValidationFailed,
            config,
            registrationPayload: null,
            redactedArtifactPayload: null,
            configSha256: null,
            diagnostics
        );
        diagnostics.Add(addedAfterConstruction);

        result.Diagnostics.Should().ContainSingle().Which.Should().BeSameAs(diagnostic);
    }

    private static string ValidFingerprintValue() =>
        CdcSourceFingerprintMetadata
            .Compute(CdcProvider.Postgresql, "f81d4fae-7dec-11d0-a765-00a0c91e6bf6")
            .Value;

    private static CdcConnectorTemplateBindingIdentity BuildBindingWithIdentityValues(
        CdcProvider provider,
        string connectorName,
        string publicTopicName
    ) =>
        new(
            provider,
            new CdcSafeName(connectorName),
            publicTopicName,
            bindingGeneration: 7,
            partitionerAlgorithm: CdcConnectorTemplateBindingIdentity.KafkaMurmur2V1PartitionerAlgorithm,
            BuildProviderArtifactNames(provider),
            SourceFingerprintFor(provider)
        );

    [Test]
    public void It_rejects_registration_payloads_that_conflict_with_binding_identity_or_result_config()
    {
        CdcConnectorTemplateBindingIdentity binding = BuildBinding(CdcProvider.Postgresql);
        var config = new Dictionary<string, string> { ["name"] = binding.ConnectorName.Value };
        var wrongNamePayload = new CdcKafkaConnectRegistrationPayload(
            new CdcSafeName("different_connector"),
            config
        );
        var wrongConfigPayload = new CdcKafkaConnectRegistrationPayload(
            binding.ConnectorName,
            new Dictionary<string, string> { ["name"] = "different_connector" }
        );

        Action wrongName = () =>
            new CdcConnectorTemplateResult(
                binding,
                CdcConnectorTemplateOutcome.Rendered,
                config,
                wrongNamePayload,
                null,
                null,
                []
            );
        Action wrongConfig = () =>
            new CdcConnectorTemplateResult(
                binding,
                CdcConnectorTemplateOutcome.Rendered,
                config,
                wrongConfigPayload,
                null,
                null,
                []
            );

        using var _ = new AssertionScope();
        wrongName.Should().Throw<ArgumentException>().WithMessage("*registration payload name*");
        wrongConfig.Should().Throw<ArgumentException>().WithMessage("*registration payload config*");
    }

    [Test]
    public void It_defines_the_required_stable_diagnostic_categories()
    {
        string[] expectedCategories =
        [
            nameof(CdcConnectorTemplateDiagnosticCategory.BindingIdentityFailure),
            nameof(CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure),
            nameof(CdcConnectorTemplateDiagnosticCategory.MissingRequiredInput),
            nameof(CdcConnectorTemplateDiagnosticCategory.ReservedKeyViolation),
            nameof(CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation),
            nameof(CdcConnectorTemplateDiagnosticCategory.KafkaSecurityPropertyViolation),
            nameof(CdcConnectorTemplateDiagnosticCategory.ProducerPolicyViolation),
            nameof(CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation),
            nameof(CdcConnectorTemplateDiagnosticCategory.TopicNamingConfigurationViolation),
            nameof(CdcConnectorTemplateDiagnosticCategory.TransformConfigurationViolation),
            nameof(CdcConnectorTemplateDiagnosticCategory.ConverterConfigurationViolation),
            nameof(CdcConnectorTemplateDiagnosticCategory.IncludeListViolation),
            nameof(CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation),
            nameof(CdcConnectorTemplateDiagnosticCategory.SchemaHistoryConfigurationViolation),
            nameof(CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch),
            nameof(CdcConnectorTemplateDiagnosticCategory.SecretRedactionViolation),
        ];

        Enum.GetNames<CdcConnectorTemplateDiagnosticCategory>().Should().BeEquivalentTo(expectedCategories);
    }

    [Test]
    public void It_defines_the_required_stable_diagnostic_source_phases()
    {
        string[] expectedSourcePhases =
        [
            nameof(CdcConnectorTemplateSourcePhase.Render),
            nameof(CdcConnectorTemplateSourcePhase.Preflight),
            nameof(CdcConnectorTemplateSourcePhase.LiveReadBack),
            nameof(CdcConnectorTemplateSourcePhase.PinnedImageSmoke),
        ];

        Enum.GetNames<CdcConnectorTemplateSourcePhase>().Should().BeEquivalentTo(expectedSourcePhases);
    }

    [Test]
    public void It_exposes_only_request_based_connector_template_service_methods()
    {
        string[] expectedServiceMethods =
        [
            nameof(ICdcConnectorTemplateService.ValidateRequest),
            nameof(ICdcConnectorTemplateService.Render),
            nameof(ICdcConnectorTemplateService.ValidateRegistrationPreflight),
            nameof(ICdcConnectorTemplateService.ValidateLiveReadBack),
        ];

        typeof(ICdcConnectorTemplateService)
            .GetMethods()
            .Select(method => method.Name)
            .Should()
            .BeEquivalentTo(expectedServiceMethods);
    }

    [Test]
    public void It_exposes_only_request_validation_on_the_input_validator()
    {
        string[] expectedInputValidatorMethods =
        [
            nameof(ICdcConnectorTemplateInputValidator.ValidateRequest),
        ];

        typeof(ICdcConnectorTemplateInputValidator)
            .GetMethods()
            .Select(method => method.Name)
            .Should()
            .BeEquivalentTo(expectedInputValidatorMethods);
    }

    [Test]
    public void It_defines_the_required_stable_redaction_classifications()
    {
        string[] expectedRedactionClassifications =
        [
            nameof(CdcConnectorTemplateRedactionClassification.Safe),
            nameof(CdcConnectorTemplateRedactionClassification.SecretValue),
            nameof(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier),
        ];

        Enum.GetNames<CdcConnectorTemplateRedactionClassification>()
            .Should()
            .Equal(expectedRedactionClassifications);
    }
}
