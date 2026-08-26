// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateContract")]
public class Given_CdcConnectorTemplateContractTests
{
    [Test]
    public void It_derives_progress_and_sqlserver_schema_history_topics_from_the_binding_topic()
    {
        CoreCdc.CdcBinding postgresqlBinding = BuildBinding(CdcProvider.Postgresql);
        CoreCdc.CdcBinding sqlServerBinding = BuildBinding(CdcProvider.SqlServer);
        CoreCdc.CdcArtifactInventory postgresqlArtifacts = BuildCoreArtifactInventory(postgresqlBinding);
        CoreCdc.CdcArtifactInventory sqlServerArtifacts = BuildCoreArtifactInventory(sqlServerBinding);
        CdcConnectorTemplateRequest postgresqlRequest = BuildRequest(
            BuildProviderSetupResult(CdcProvider.Postgresql),
            binding: postgresqlBinding
        );

        using var _ = new AssertionScope();
        postgresqlArtifacts
            .ProgressTopicName.Should()
            .Be("edfi.documents.instance.binding-g7.documents.v1.cdc-progress");
        postgresqlArtifacts.SchemaHistoryTopicName.Should().BeNull();
        sqlServerArtifacts
            .ProgressTopicName.Should()
            .Be("edfi.documents.instance.binding-g7.documents.v1.cdc-progress");
        sqlServerArtifacts
            .SchemaHistoryTopicName.Should()
            .Be("edfi.documents.instance.binding-g7.documents.v1.schema-history");
        postgresqlRequest.ProgressTopicName.Should().Be(postgresqlArtifacts.ProgressTopicName);
    }

    [Test]
    public void It_accepts_valid_connector_and_kafka_topic_binding_names()
    {
        CoreCdc.CdcBinding binding = BuildBindingWithTargetValues(
            CdcProvider.SqlServer,
            deploymentKey: "dms.binding-connector_01",
            topicPrefix: "edfi.documents-v1_2026",
            instanceKey: "data.store_01"
        );
        CoreCdc.CdcArtifactInventory artifactInventory = BuildCoreArtifactInventory(binding);

        using var _ = new AssertionScope();
        artifactInventory.ConnectorName.Should().Be("dms.binding-connector_01-data.store_01-g7");
        artifactInventory
            .TopicName.Should()
            .Be("edfi.documents-v1_2026.instance.data.store_01-g7.documents.v1");
        artifactInventory
            .ProgressTopicName.Should()
            .Be("edfi.documents-v1_2026.instance.data.store_01-g7.documents.v1.cdc-progress");
        artifactInventory
            .SchemaHistoryTopicName.Should()
            .Be("edfi.documents-v1_2026.instance.data.store_01-g7.documents.v1.schema-history");
    }

    [Test]
    public void It_rejects_binding_connector_names_that_are_not_core_artifacts()
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
                BuildRequest(
                    BuildProviderSetupResult(CdcProvider.Postgresql),
                    binding: BuildBinding(CdcProvider.Postgresql) with
                    {
                        ConnectorName = invalidConnectorName,
                    }
                );

            act.Should()
                .Throw<ArgumentException>()
                .WithParameterName("binding")
                .WithMessage("*connectorName*");
        }
    }

    [Test]
    public void It_rejects_binding_topic_names_that_are_not_core_artifacts()
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
                BuildRequest(
                    BuildProviderSetupResult(CdcProvider.Postgresql),
                    binding: BuildBinding(CdcProvider.Postgresql) with
                    {
                        TopicName = invalidPublicTopicName,
                    }
                );

            act.Should().Throw<ArgumentException>().WithParameterName("binding").WithMessage("*topicName*");
        }
    }

    [Test]
    public void It_rejects_derived_topic_names_that_are_not_valid_kafka_topic_names()
    {
        Action overlongProgressTopic = () =>
            BuildRequest(
                BuildProviderSetupResult(CdcProvider.Postgresql),
                binding: BuildBinding(CdcProvider.Postgresql) with
                {
                    TopicName = new string('a', 237),
                }
            );
        Action overlongSchemaHistoryTopic = () =>
            BuildRequest(
                BuildProviderSetupResult(CdcProvider.SqlServer),
                binding: BuildBinding(CdcProvider.SqlServer) with
                {
                    TopicName = new string('a', 235),
                }
            );

        using var _ = new AssertionScope();
        overlongProgressTopic
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("binding")
            .WithMessage("*topicName*");
        overlongSchemaHistoryTopic
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("binding")
            .WithMessage("*topicName*");
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
        request.ConnectorName.Should().Be(new CdcSafeName("dms-binding-g7"));
        request.PublicTopicName.Should().Be("edfi.documents.instance.binding-g7.documents.v1");
        request.ProgressTopicName.Should().Be("edfi.documents.instance.binding-g7.documents.v1.cdc-progress");
        request.SchemaHistoryTopicName.Should().BeNull();
        request
            .PartitionerAlgorithm.Should()
            .Be(CoreCdc.CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm);
        request.DeploymentPolicy.Should().BeSameAs(policy);
        request.ProviderConnectionProperties.Should().BeSameAs(providerConnectionProperties);
        request.KafkaClientSecurityProperties.Should().BeSameAs(kafkaSecurityProperties);
        request.ArtifactOutput.Should().BeSameAs(artifactOutput);
        request
            .ProviderArtifactNames.Postgresql!.PublicationName.Should()
            .Be(new CdcSafeName("edfi_dms_dms_binding_g7_de1bb4313908_pub"));
        artifactOutput.IncludeRedactedArtifactPayload.Should().BeTrue();
    }

    [Test]
    public void It_exposes_normalized_property_maps_as_read_only()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CdcProvider.Postgresql);
        CdcSafeName connectorName = new(binding.ConnectorName);
        var config = new Dictionary<string, string>
        {
            ["connector.class"] = "io.debezium.connector.postgresql.PostgresConnector",
            ["name"] = connectorName.Value,
        };
        var result = new CdcConnectorTemplateResult(
            binding,
            CdcConnectorTemplateOutcome.Rendered,
            config,
            new CdcKafkaConnectRegistrationPayload(connectorName, config),
            redactedArtifactPayload: null,
            configSha256: $"sha256:{new string('a', 64)}",
            diagnostics: []
        );
        var providerConnectionProperties = new CdcProviderConnectionProperties(
            CdcProvider.Postgresql,
            new Dictionary<string, string> { ["database.hostname"] = "postgresql.internal" }
        );

        using var _ = new AssertionScope();
        ((Action)(() => ((IDictionary<string, string>)result.Config)["name"] = "mutated"))
            .Should()
            .Throw<NotSupportedException>();
        (
            (Action)(
                () => ((IDictionary<string, string>)result.RegistrationPayload!.Config)["name"] = "mutated"
            )
        )
            .Should()
            .Throw<NotSupportedException>();
        (
            (Action)(
                () =>
                    ((IDictionary<string, string>)providerConnectionProperties.Properties)[
                        "database.hostname"
                    ] = "mutated"
            )
        )
            .Should()
            .Throw<NotSupportedException>();
        (
            (Action)(
                () =>
                    ((IDictionary<string, string>)CdcKafkaClientSecurityProperties.Empty.Properties)[
                        "security.protocol"
                    ] = "SSL"
            )
        )
            .Should()
            .Throw<NotSupportedException>();
    }

    [Test]
    public void It_rejects_duplicate_string_property_names()
    {
        var properties = new DuplicateKeyReadOnlyDictionary(
            new("database.hostname", "postgresql-1.internal"),
            new("database.hostname", "postgresql-2.internal")
        );

        Action act = () => new CdcProviderConnectionProperties(CdcProvider.Postgresql, properties);

        act.Should()
            .Throw<ArgumentException>()
            .WithParameterName("properties.Key")
            .WithMessage("*property names must be unique*");
    }

    [Test]
    public void It_requires_positive_binding_generations()
    {
        Action zeroBindingGeneration = () =>
            BuildRequest(
                BuildProviderSetupResult(CdcProvider.Postgresql),
                binding: BuildBinding(CdcProvider.Postgresql) with
                {
                    Generation = 0,
                }
            );
        Action negativeBindingGeneration = () =>
            BuildRequest(
                BuildProviderSetupResult(CdcProvider.Postgresql),
                binding: BuildBinding(CdcProvider.Postgresql) with
                {
                    Generation = -1,
                }
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
            .Throw<ArgumentException>()
            .WithParameterName("binding")
            .WithMessage("*generation*positive*");
        negativeBindingGeneration
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("binding")
            .WithMessage("*generation*positive*");
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
            BuildRequest(
                BuildProviderSetupResult(CdcProvider.Postgresql),
                binding: BuildBinding(CdcProvider.Postgresql) with
                {
                    PartitionerAlgorithm = null!,
                }
            );
        Action emptyPartitionerAlgorithm = () =>
            BuildRequest(
                BuildProviderSetupResult(CdcProvider.Postgresql),
                binding: BuildBinding(CdcProvider.Postgresql) with
                {
                    PartitionerAlgorithm = "",
                }
            );
        Action unsupportedPartitionerAlgorithm = () =>
            BuildRequest(
                BuildProviderSetupResult(CdcProvider.Postgresql),
                binding: BuildBinding(CdcProvider.Postgresql) with
                {
                    PartitionerAlgorithm = "round-robin",
                }
            );

        using var _ = new AssertionScope();
        missingPartitionerAlgorithm.Should().Throw<ArgumentException>().WithParameterName("binding");
        emptyPartitionerAlgorithm.Should().Throw<ArgumentException>().WithParameterName("binding");
        unsupportedPartitionerAlgorithm
            .Should()
            .Throw<ArgumentException>()
            .WithMessage("*kafka-murmur2-v1*")
            .WithParameterName("binding");
    }

    [Test]
    public void It_requires_binding_artifact_names_to_match_the_core_inventory()
    {
        Action mismatchedConnectorName = () =>
            BuildRequest(
                BuildProviderSetupResult(CdcProvider.Postgresql),
                binding: BuildBinding(CdcProvider.Postgresql) with
                {
                    ConnectorName = "dms-other-g7",
                }
            );
        Action mismatchedTopicName = () =>
            BuildRequest(
                BuildProviderSetupResult(CdcProvider.SqlServer),
                binding: BuildBinding(CdcProvider.SqlServer) with
                {
                    TopicName = "edfi.documents.instance.other-g7.documents.v1",
                }
            );

        using var _ = new AssertionScope();
        mismatchedConnectorName
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("binding")
            .WithMessage("*connectorName*deterministic*");
        mismatchedTopicName
            .Should()
            .Throw<ArgumentException>()
            .WithParameterName("binding")
            .WithMessage("*topicName*deterministic*");
    }

    [Test]
    public void It_requires_the_binding_source_fingerprint_sha256_shape()
    {
        var invalidFingerprints = new[]
        {
            new CdcSourceFingerprint(CdcSourceFingerprintMetadata.Version, new string('a', 64)),
            new CdcSourceFingerprint(CdcSourceFingerprintMetadata.Version, $"sha256:{new string('A', 64)}"),
            new CdcSourceFingerprint(CdcSourceFingerprintMetadata.Version, $"sha256:{new string('g', 64)}"),
            new CdcSourceFingerprint(CdcSourceFingerprintMetadata.Version, $"sha256:{new string('a', 63)}"),
        };

        using var _ = new AssertionScope();
        foreach (CdcSourceFingerprint invalidFingerprint in invalidFingerprints)
        {
            Action act = () =>
                BuildRequest(
                    BuildProviderSetupResult(CdcProvider.Postgresql),
                    binding: BuildBinding(CdcProvider.Postgresql) with
                    {
                        PhysicalSourceFingerprint = invalidFingerprint.Value,
                    }
                );

            act.Should().Throw<ArgumentException>().WithParameterName("binding");
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
        constructorParameterNames.Should().Contain("binding");
        constructorParameterNames.Should().Contain("providerSetupEvidence");
        constructorParameterNames.Should().Contain("deploymentPolicy");
        constructorParameterNames.Should().Contain("providerConnectionProperties");
        constructorParameterNames.Should().Contain("kafkaClientSecurityProperties");
        constructorParameterNames.Should().NotContain(name => forbiddenParameterNames.Contains(name));
    }

    [Test]
    public void It_consumes_the_core_cdc_binding_and_artifact_inventory_contract()
    {
        var requestConstructor = typeof(CdcConnectorTemplateRequest)
            .GetConstructors()
            .Should()
            .ContainSingle()
            .Subject;

        using var _ = new AssertionScope();
        requestConstructor
            .GetParameters()
            .Single(parameter => parameter.Name == "binding")
            .ParameterType.Should()
            .Be(typeof(CoreCdc.CdcBinding));
        typeof(CdcConnectorTemplateRequest)
            .GetProperty(nameof(CdcConnectorTemplateRequest.ArtifactInventory))!
            .PropertyType.Should()
            .Be(typeof(CoreCdc.CdcArtifactInventory));
        typeof(CdcConnectorTemplateResult)
            .GetProperty(nameof(CdcConnectorTemplateResult.Binding))!
            .PropertyType.Should()
            .Be(typeof(CoreCdc.CdcBinding));
        string removedTemplateOnlyBindingTypeName = string.Concat(
            "EdFi.DataManagementService.Backend.Cdc.CdcConnector",
            "TemplateBindingIdentity"
        );
        typeof(CdcConnectorTemplateRequest)
            .Assembly.GetType(removedTemplateOnlyBindingTypeName)
            .Should()
            .BeNull();
    }

    [Test]
    public void It_exposes_a_consumer_facing_result_with_registration_payload_artifact_hash_and_diagnostics()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CdcProvider.SqlServer);
        CdcSafeName connectorName = new(binding.ConnectorName);
        var config = new Dictionary<string, string>
        {
            ["name"] = connectorName.Value,
            ["topic.prefix"] = connectorName.Value,
        };
        var registrationPayload = new CdcKafkaConnectRegistrationPayload(connectorName, config);
        var artifactPayload = new CdcConnectorTemplateArtifactPayload(
            new CdcSafeName("cdc-connector-template.sqlserver.dms-binding-g7.manifest.json"),
            """{"redactedConfig":{"database.password":"[redacted]"}}"""
        );
        var diagnostic = new CdcConnectorTemplateDiagnostic(
            code: "CDC_TEMPLATE_CONTRACT_SENTINEL",
            category: CdcConnectorTemplateDiagnosticCategory.BindingIdentityFailure,
            severity: CdcConnectorTemplateDiagnosticSeverity.Info,
            propertyName: "topic.prefix",
            safeArtifactOrObjectName: connectorName,
            expectedValue: connectorName.Value,
            observedValue: connectorName.Value,
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
        result.Binding.Should().Be(binding);
        result.ArtifactInventory.Should().BeEquivalentTo(BuildCoreArtifactInventory(binding));
        result.ConnectorName.Should().Be(connectorName);
        result.PublicTopicName.Should().Be("edfi.documents.instance.binding-g7.documents.v1");
        result.ProgressTopicName.Should().Be("edfi.documents.instance.binding-g7.documents.v1.cdc-progress");
        result
            .SchemaHistoryTopicName.Should()
            .Be("edfi.documents.instance.binding-g7.documents.v1.schema-history");
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
        CoreCdc.CdcBinding binding = BuildBinding(CdcProvider.Postgresql);
        CdcSafeName connectorName = new(binding.ConnectorName);
        var config = new Dictionary<string, string> { ["name"] = connectorName.Value };
        var diagnostic = new CdcConnectorTemplateDiagnostic(
            code: "CDC_TEMPLATE_CONTRACT_SENTINEL",
            category: CdcConnectorTemplateDiagnosticCategory.BindingIdentityFailure,
            severity: CdcConnectorTemplateDiagnosticSeverity.Info,
            propertyName: "topic.prefix",
            safeArtifactOrObjectName: connectorName,
            expectedValue: connectorName.Value,
            observedValue: connectorName.Value,
            provider: CdcProvider.Postgresql,
            sourcePhase: CdcConnectorTemplateSourcePhase.Render,
            redactionClassification: CdcConnectorTemplateRedactionClassification.Safe
        );
        var addedAfterConstruction = new CdcConnectorTemplateDiagnostic(
            code: "CDC_TEMPLATE_MUTATED_SENTINEL",
            category: CdcConnectorTemplateDiagnosticCategory.LiveReadBackMismatch,
            severity: CdcConnectorTemplateDiagnosticSeverity.Error,
            propertyName: "database.password",
            safeArtifactOrObjectName: connectorName,
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

    private static CoreCdc.CdcBinding BuildBindingWithTargetValues(
        CdcProvider provider,
        string deploymentKey,
        string topicPrefix,
        string instanceKey
    ) =>
        BuildBinding(
            provider,
            deploymentKey: deploymentKey,
            topicPrefix: topicPrefix,
            instanceKey: instanceKey
        );

    private sealed class DuplicateKeyReadOnlyDictionary(params KeyValuePair<string, string>[] properties)
        : IReadOnlyDictionary<string, string>
    {
        public string this[string key] => properties.Last(property => property.Key == key).Value;

        public IEnumerable<string> Keys => properties.Select(property => property.Key);

        public IEnumerable<string> Values => properties.Select(property => property.Value);

        public int Count => properties.Length;

        public bool ContainsKey(string key) => Array.Exists(properties, property => property.Key == key);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, string>>)properties).GetEnumerator();

        public bool TryGetValue(string key, out string value)
        {
            for (int index = properties.Length - 1; index >= 0; index--)
            {
                if (properties[index].Key == key)
                {
                    value = properties[index].Value;
                    return true;
                }
            }

            value = string.Empty;
            return false;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Test]
    public void It_rejects_registration_payloads_that_conflict_with_binding_identity_or_result_config()
    {
        CoreCdc.CdcBinding binding = BuildBinding(CdcProvider.Postgresql);
        CdcSafeName connectorName = new(binding.ConnectorName);
        var config = new Dictionary<string, string> { ["name"] = connectorName.Value };
        var wrongNamePayload = new CdcKafkaConnectRegistrationPayload(
            new CdcSafeName("different_connector"),
            config
        );
        var wrongConfigPayload = new CdcKafkaConnectRegistrationPayload(
            connectorName,
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
            nameof(CdcConnectorTemplateDiagnosticCategory.ArtifactOutputFailure),
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
    public void It_exposes_only_result_based_connector_template_service_methods()
    {
        string[] expectedServiceMethods =
        [
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
    public void It_keeps_validation_only_contracts_internal()
    {
        using var _ = new AssertionScope();
        typeof(ICdcConnectorTemplateInputValidator).IsNotPublic.Should().BeTrue();
        typeof(CdcConnectorTemplateValidationResult).IsNotPublic.Should().BeTrue();
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
