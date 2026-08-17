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
public class Given_CdcConnectorTemplateContracts
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
                SourceFingerprint
            );
        Action negativeBindingGeneration = () =>
            new CdcConnectorTemplateBindingIdentity(
                CdcProvider.Postgresql,
                new CdcSafeName("dms_binding_connector"),
                "edfi.documents",
                bindingGeneration: -1,
                partitionerAlgorithm: "kafka-murmur2-v1",
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

    [Test]
    public void It_rejects_explicit_producer_buffer_bytes_below_max_record_bytes()
    {
        Action act = () =>
            new CdcConnectorTemplateDeploymentPolicy(
                kafkaBootstrapServers: "broker:9092",
                maxRecordBytes: 67_108_864,
                producerBufferBytes: 33_554_432
            );

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("producerBufferBytes")
            .WithMessage("*producerBufferBytes*greater than or equal to maxRecordBytes*");
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
                SourceFingerprint
            );
        Action emptyPartitionerAlgorithm = () =>
            new CdcConnectorTemplateBindingIdentity(
                CdcProvider.Postgresql,
                new CdcSafeName("dms_binding_connector"),
                "edfi.documents",
                bindingGeneration: 7,
                partitionerAlgorithm: "",
                SourceFingerprint
            );
        Action unsupportedPartitionerAlgorithm = () =>
            new CdcConnectorTemplateBindingIdentity(
                CdcProvider.Postgresql,
                new CdcSafeName("dms_binding_connector"),
                "edfi.documents",
                bindingGeneration: 7,
                partitionerAlgorithm: "round-robin",
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
    public void It_rejects_failed_provider_setup_results()
    {
        Action act = () =>
            BuildRequest(BuildProviderSetupResult(CdcProvider.Postgresql, CdcProviderSetupOutcome.Failed));

        act.Should().Throw<ArgumentException>().WithMessage("*successful provider setup*");
    }

    [Test]
    public void It_rejects_provider_setup_evidence_that_does_not_match_binding_identity()
    {
        Action wrongProvider = () =>
            BuildRequest(
                BuildProviderSetupResult(CdcProvider.SqlServer),
                binding: BuildBinding(CdcProvider.Postgresql),
                providerConnectionProperties: new CdcProviderConnectionProperties(
                    CdcProvider.Postgresql,
                    new Dictionary<string, string>()
                )
            );
        Action wrongGeneration = () =>
            BuildRequest(BuildProviderSetupResult(CdcProvider.Postgresql), providerSetupBindingGeneration: 8);
        Action wrongFingerprint = () =>
            BuildRequest(
                BuildProviderSetupResult(
                    CdcProvider.Postgresql,
                    boundPhysicalSourceFingerprint: OtherPostgresqlSourceFingerprint
                )
            );

        using var _ = new AssertionScope();
        wrongProvider.Should().Throw<ArgumentException>().WithMessage("*binding provider*");
        wrongGeneration.Should().Throw<ArgumentException>().WithMessage("*same binding generation*");
        wrongFingerprint.Should().Throw<ArgumentException>().WithMessage("*physical source fingerprint*");
    }

    [Test]
    public void It_rejects_provider_setup_evidence_without_required_source_key_and_heartbeat_inventory()
    {
        Action missingSourceInventory = () =>
            BuildRequest(BuildProviderSetupResult(CdcProvider.Postgresql, sourceTableInventory: []));
        Action missingDocumentUuidKeyInventory = () =>
            BuildRequest(BuildProviderSetupResult(CdcProvider.Postgresql, expectedMessageKeyColumns: []));
        Action missingHeartbeatActionQuery = () =>
            BuildRequest(BuildProviderSetupResult(CdcProvider.Postgresql, omitHeartbeatActionQuery: true));

        using var _ = new AssertionScope();
        missingSourceInventory.Should().Throw<ArgumentException>().WithMessage("*source inventory*");
        missingDocumentUuidKeyInventory
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*message-key inventory*");
        missingHeartbeatActionQuery
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*heartbeat action query*");
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
            new CdcSafeName("cdc-connector-template.sqlserver.manifest.json"),
            """{"redactedConfig":{"database.password":"[redacted]"}}"""
        );
        var diagnostic = new CdcConnectorTemplateDiagnostic(
            code: "CDC_TEMPLATE_CONTRACT_SENTINEL",
            category: CdcConnectorTemplateDiagnosticCategory.BindingIdentity,
            severity: CdcConnectorTemplateDiagnosticSeverity.Info,
            propertyName: "topic.prefix",
            safeArtifactOrObjectName: binding.ConnectorName,
            expectedValue: binding.ConnectorName.Value,
            observedValue: binding.ConnectorName.Value,
            provider: CdcProvider.SqlServer,
            sourcePhase: CdcConnectorTemplateSourcePhase.Rendering,
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

    private static string ValidFingerprintValue() =>
        CdcSourceFingerprintMetadata
            .Compute(CdcProvider.Postgresql, "f81d4fae-7dec-11d0-a765-00a0c91e6bf6")
            .Value;

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
            nameof(CdcConnectorTemplateDiagnosticCategory.BindingIdentity),
            nameof(CdcConnectorTemplateDiagnosticCategory.ProviderSetupResult),
            nameof(CdcConnectorTemplateDiagnosticCategory.MissingInput),
            nameof(CdcConnectorTemplateDiagnosticCategory.ReservedKey),
            nameof(CdcConnectorTemplateDiagnosticCategory.ConnectionProperty),
            nameof(CdcConnectorTemplateDiagnosticCategory.KafkaSecurityProperty),
            nameof(CdcConnectorTemplateDiagnosticCategory.ProducerPolicy),
            nameof(CdcConnectorTemplateDiagnosticCategory.Heartbeat),
            nameof(CdcConnectorTemplateDiagnosticCategory.TopicNaming),
            nameof(CdcConnectorTemplateDiagnosticCategory.Transform),
            nameof(CdcConnectorTemplateDiagnosticCategory.Converter),
            nameof(CdcConnectorTemplateDiagnosticCategory.IncludeList),
            nameof(CdcConnectorTemplateDiagnosticCategory.MessageKey),
            nameof(CdcConnectorTemplateDiagnosticCategory.SchemaHistory),
            nameof(CdcConnectorTemplateDiagnosticCategory.LiveReadBack),
            nameof(CdcConnectorTemplateDiagnosticCategory.SecretRedactionFailure),
        ];

        Enum.GetNames<CdcConnectorTemplateDiagnosticCategory>().Should().BeEquivalentTo(expectedCategories);
    }
}
