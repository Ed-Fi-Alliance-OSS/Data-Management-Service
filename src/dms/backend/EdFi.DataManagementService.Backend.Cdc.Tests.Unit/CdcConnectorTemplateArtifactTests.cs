// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateArtifacts")]
public class Given_CdcConnectorTemplateArtifactTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Test]
    public void It_returns_a_deterministic_postgresql_redacted_manifest_payload_when_requested()
    {
        CdcConnectorTemplateRequest request = BuildRequest(
            CdcProvider.Postgresql,
            artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                includeRedactedArtifactPayload: true
            ),
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
        );
        CdcConnectorTemplateResult result = Render(request);
        CdcConnectorTemplateResult repeatedResult = Render(request);

        result.RedactedArtifactPayload.Should().NotBeNull();
        CdcConnectorTemplateArtifactPayload payload = result.RedactedArtifactPayload!;
        using JsonDocument document = JsonDocument.Parse(payload.Json);
        JsonElement root = document.RootElement;
        JsonElement redactedConfig = root.GetProperty("redactedConfig");

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        payload
            .FileName.Should()
            .Be(new CdcSafeName("cdc-connector-template.postgresql.dms-binding-g7.manifest.json"));
        root.EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .Equal(
                "version",
                "generatedAt",
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
        root.GetProperty("generatedAt").GetString().Should().Be("2026-01-02T03:04:05+00:00");
        root.GetProperty("provider").GetString().Should().Be("postgresql");
        root.GetProperty("connectorName").GetString().Should().Be(request.ConnectorName.Value);
        root.GetProperty("publicTopicName").GetString().Should().Be(request.PublicTopicName);
        root.GetProperty("progressTopicName").GetString().Should().Be(request.ProgressTopicName);
        root.GetProperty("schemaHistoryTopicName").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("configSha256").GetString().Should().Be(result.ConfigSha256);
        repeatedResult.RedactedArtifactPayload!.Json.Should().Be(payload.Json);
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
        redactedConfig.GetProperty("publication.name").GetString().Should().Be("edfi_dms_dms_binding_g7_pub");
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
                artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                    includeRedactedArtifactPayload: false,
                    manifestOutputDirectoryPath: artifactDirectory
                ),
                providerConnectionProperties: new Dictionary<string, string>(
                    BuildSqlServerConnectionProperties()
                )
                {
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

        result.RedactedArtifactPayload.Should().NotBeNull();
        CdcConnectorTemplateArtifactPayload payload = result.RedactedArtifactPayload!;
        string manifestPath = Path.Combine(artifactDirectory, payload.FileName.Value);
        using JsonDocument document = JsonDocument.Parse(payload.Json);
        JsonElement root = document.RootElement;
        JsonElement redactedConfig = root.GetProperty("redactedConfig");

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        payload
            .FileName.Should()
            .Be(new CdcSafeName("cdc-connector-template.sqlserver.dms-binding-g7.manifest.json"));
        File.Exists(manifestPath).Should().BeTrue();
        File.ReadAllText(manifestPath).Should().Be(payload.Json);
        root.GetProperty("provider").GetString().Should().Be("sqlserver");
        root.GetProperty("schemaHistoryTopicName").GetString().Should().Be(result.SchemaHistoryTopicName);
        redactedConfig.GetProperty("driver.trustServerCertificate").GetString().Should().Be("[redacted]");
        redactedConfig.GetProperty("driver.trustStorePassword").GetString().Should().Be("[redacted]");
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
    public void It_returns_diagnostics_and_keeps_the_redacted_payload_when_the_manifest_directory_cannot_be_written()
    {
        string artifactDirectoryPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"cdc-connector-template-artifacts-{Guid.NewGuid():N}"
        );
        File.WriteAllText(artifactDirectoryPath, "not a directory");

        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                    includeRedactedArtifactPayload: false,
                    manifestOutputDirectoryPath: artifactDirectoryPath
                )
            )
        );

        CdcConnectorTemplateDiagnostic diagnostic = result.Diagnostics.Should().ContainSingle().Subject;

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().NotBeEmpty();
        result.RegistrationPayload.Should().NotBeNull();
        result.ConfigSha256.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        result.RedactedArtifactPayload.Should().NotBeNull();
        result
            .RedactedArtifactPayload!.FileName.Should()
            .Be(new CdcSafeName("cdc-connector-template.postgresql.dms-binding-g7.manifest.json"));
        result.RedactedArtifactPayload.Json.Should().Contain("\"redactedConfig\"");
        diagnostic.Code.Should().Be(CdcConnectorTemplateDiagnosticCodes.ArtifactOutputFailed);
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.ArtifactOutputFailure);
        diagnostic.Severity.Should().Be(CdcConnectorTemplateDiagnosticSeverity.Error);
        diagnostic.PropertyName.Should().Be("artifactOutput.manifestOutputDirectoryPath");
        diagnostic.SafeArtifactOrObjectName.Should().Be(result.RedactedArtifactPayload.FileName);
        diagnostic.ExpectedValue.Should().Be("writable-artifact-directory");
        diagnostic.ObservedValue.Should().Be("IOException");
        diagnostic.Provider.Should().Be(CdcProvider.Postgresql);
        diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.Render);
        diagnostic.RedactionClassification.Should().Be(CdcConnectorTemplateRedactionClassification.Safe);
        diagnostic.ObservedValue.Should().NotContain(artifactDirectoryPath);
    }

    [Test]
    public void It_writes_same_provider_manifest_files_by_connector_name_without_overwriting()
    {
        string artifactDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"cdc-connector-template-artifacts-{Guid.NewGuid():N}"
        );
        var artifactOutput = new CdcConnectorTemplateArtifactOutputRequest(
            includeRedactedArtifactPayload: false,
            manifestOutputDirectoryPath: artifactDirectory
        );

        CdcConnectorTemplateResult first = Render(
            BuildRequest(CdcProvider.Postgresql, artifactOutput: artifactOutput, instanceKey: "binding")
        );
        CdcConnectorTemplateResult second = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                artifactOutput: artifactOutput,
                instanceKey: "second-binding"
            )
        );

        string firstManifestFileName = "cdc-connector-template.postgresql.dms-binding-g7.manifest.json";
        string secondManifestFileName =
            "cdc-connector-template.postgresql.dms-second-binding-g7.manifest.json";
        string firstManifestPath = Path.Combine(artifactDirectory, firstManifestFileName);
        string secondManifestPath = Path.Combine(artifactDirectory, secondManifestFileName);
        using JsonDocument firstDocument = JsonDocument.Parse(File.ReadAllText(firstManifestPath));
        using JsonDocument secondDocument = JsonDocument.Parse(File.ReadAllText(secondManifestPath));

        using var _ = new AssertionScope();
        first.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        second.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        first.RedactedArtifactPayload.Should().NotBeNull();
        second.RedactedArtifactPayload.Should().NotBeNull();
        first.RedactedArtifactPayload!.FileName.Should().Be(new CdcSafeName(firstManifestFileName));
        second.RedactedArtifactPayload!.FileName.Should().Be(new CdcSafeName(secondManifestFileName));
        File.Exists(firstManifestPath).Should().BeTrue();
        File.Exists(secondManifestPath).Should().BeTrue();
        File.ReadAllText(firstManifestPath).Should().Be(first.RedactedArtifactPayload.Json);
        File.ReadAllText(secondManifestPath).Should().Be(second.RedactedArtifactPayload.Json);
        firstDocument.RootElement.GetProperty("connectorName").GetString().Should().Be("dms-binding-g7");
        secondDocument
            .RootElement.GetProperty("connectorName")
            .GetString()
            .Should()
            .Be("dms-second-binding-g7");
        Directory
            .GetFiles(artifactDirectory, "cdc-connector-template.postgresql.*.manifest.json")
            .Select(Path.GetFileName)
            .Should()
            .BeEquivalentTo(firstManifestFileName, secondManifestFileName);
        File.Exists(Path.Combine(artifactDirectory, "cdc-connector-template.postgresql.manifest.json"))
            .Should()
            .BeFalse();
    }

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_writes_a_bounded_manifest_file_for_a_maximum_length_connector_name(CdcProvider provider)
    {
        string artifactDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"cdc-connector-template-artifacts-{Guid.NewGuid():N}"
        );
        string connectorName = $"{new string('a', 244)}-i-g7";

        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                provider,
                artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                    includeRedactedArtifactPayload: true,
                    manifestOutputDirectoryPath: artifactDirectory
                ),
                deploymentKey: new string('a', 244),
                instanceKey: "i"
            )
        );

        result.RedactedArtifactPayload.Should().NotBeNull();
        CdcConnectorTemplateArtifactPayload payload = result.RedactedArtifactPayload!;
        string manifestPath = Path.Combine(artifactDirectory, payload.FileName.Value);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        payload.FileName.Value.Length.Should().BeLessThanOrEqualTo(255);
        payload.FileName.Value.Should().StartWith($"cdc-connector-template.{ProviderToken(provider)}.");
        payload.FileName.Value.Should().Contain(".sha256-");
        payload.FileName.Value.Should().EndWith(".manifest.json");
        File.Exists(manifestPath).Should().BeTrue();
        File.ReadAllText(manifestPath).Should().Be(payload.Json);
        document.RootElement.GetProperty("connectorName").GetString().Should().Be(connectorName);
        result.RegistrationPayload.Should().NotBeNull();
        result.RegistrationPayload!.Name.Should().Be(connectorName);
        result.RegistrationPayload.Config["name"].Should().Be(connectorName);
        result.RegistrationPayload.Config["topic.prefix"].Should().Be(connectorName);
    }

    [Test]
    public void It_writes_distinct_bounded_manifest_files_for_long_connector_names_with_the_same_prefix()
    {
        string artifactDirectory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"cdc-connector-template-artifacts-{Guid.NewGuid():N}"
        );
        var artifactOutput = new CdcConnectorTemplateArtifactOutputRequest(
            includeRedactedArtifactPayload: true,
            manifestOutputDirectoryPath: artifactDirectory
        );
        string firstConnectorName = $"{new string('b', 243)}a-i-g7";
        string secondConnectorName = $"{new string('b', 243)}c-i-g7";

        CdcConnectorTemplateResult first = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                artifactOutput: artifactOutput,
                deploymentKey: $"{new string('b', 243)}a",
                instanceKey: "i"
            )
        );
        CdcConnectorTemplateResult second = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                artifactOutput: artifactOutput,
                deploymentKey: $"{new string('b', 243)}c",
                instanceKey: "i"
            )
        );

        string firstManifestPath = Path.Combine(
            artifactDirectory,
            first.RedactedArtifactPayload!.FileName.Value
        );
        string secondManifestPath = Path.Combine(
            artifactDirectory,
            second.RedactedArtifactPayload!.FileName.Value
        );
        using JsonDocument firstDocument = JsonDocument.Parse(File.ReadAllText(firstManifestPath));
        using JsonDocument secondDocument = JsonDocument.Parse(File.ReadAllText(secondManifestPath));

        using var _ = new AssertionScope();
        first.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        second.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        first.RedactedArtifactPayload.FileName.Value.Length.Should().BeLessThanOrEqualTo(255);
        second.RedactedArtifactPayload.FileName.Value.Length.Should().BeLessThanOrEqualTo(255);
        first
            .RedactedArtifactPayload.FileName.Value.Should()
            .NotBe(second.RedactedArtifactPayload.FileName.Value);
        File.Exists(firstManifestPath).Should().BeTrue();
        File.Exists(secondManifestPath).Should().BeTrue();
        firstDocument.RootElement.GetProperty("connectorName").GetString().Should().Be(firstConnectorName);
        secondDocument.RootElement.GetProperty("connectorName").GetString().Should().Be(secondConnectorName);
        Directory
            .GetFiles(artifactDirectory, "cdc-connector-template.postgresql.*.manifest.json")
            .Select(Path.GetFileName)
            .Should()
            .BeEquivalentTo(
                first.RedactedArtifactPayload.FileName.Value,
                second.RedactedArtifactPayload.FileName.Value
            );
    }

    [Test]
    public void It_preserves_and_redacts_multiline_kafka_certificate_chain_material()
    {
        const string truststoreCertificateChain =
            "-----BEGIN CERTIFICATE-----\nMIIDTRUSTSTORE\n-----END CERTIFICATE-----";
        const string keystoreCertificateChain =
            "-----BEGIN CERTIFICATE-----\r\nMIIDKEYSTORE\r\n-----END CERTIFICATE-----";

        CdcConnectorTemplateResult result = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                    includeRedactedArtifactPayload: true
                ),
                kafkaSecurityProperties: new Dictionary<string, string>
                {
                    ["security.protocol"] = "SSL",
                    ["ssl.truststore.certificates"] = truststoreCertificateChain,
                    ["ssl.keystore.certificate.chain"] = keystoreCertificateChain,
                }
            )
        );

        result.RedactedArtifactPayload.Should().NotBeNull();
        CdcConnectorTemplateArtifactPayload payload = result.RedactedArtifactPayload!;
        using JsonDocument document = JsonDocument.Parse(payload.Json);
        JsonElement redactedConfig = document.RootElement.GetProperty("redactedConfig");

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result
            .Config["producer.override.ssl.truststore.certificates"]
            .Should()
            .Be(truststoreCertificateChain);
        result
            .Config["producer.override.ssl.keystore.certificate.chain"]
            .Should()
            .Be(keystoreCertificateChain);
        redactedConfig
            .GetProperty("producer.override.ssl.truststore.certificates")
            .GetString()
            .Should()
            .Be("[redacted]");
        redactedConfig
            .GetProperty("producer.override.ssl.keystore.certificate.chain")
            .GetString()
            .Should()
            .Be("[redacted]");
        payload.Json.Should().NotContain(truststoreCertificateChain);
        payload.Json.Should().NotContain(keystoreCertificateChain);
        payload.Json.Should().NotContain("MIIDTRUSTSTORE");
        payload.Json.Should().NotContain("MIIDKEYSTORE");
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
                artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                    includeRedactedArtifactPayload: true
                ),
                providerConnectionProperties: BuildPostgresqlConnectionProperties(
                    "${env:CDC_DATABASE_PASSWORD_A}"
                )
            )
        );
        CdcConnectorTemplateResult second = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                    includeRedactedArtifactPayload: true
                ),
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

    [Test]
    public void It_excludes_manifest_metadata_from_config_hash()
    {
        CdcConnectorTemplateResult withManifest = Render(
            BuildRequest(
                CdcProvider.Postgresql,
                artifactOutput: new CdcConnectorTemplateArtifactOutputRequest(
                    includeRedactedArtifactPayload: true
                )
            )
        );
        CdcConnectorTemplateResult withoutManifest = Render(
            BuildRequest(CdcProvider.Postgresql, artifactOutput: null)
        );

        using JsonDocument document = JsonDocument.Parse(withManifest.RedactedArtifactPayload!.Json);

        using var _ = new AssertionScope();
        withManifest.ConfigSha256.Should().Be(withoutManifest.ConfigSha256);
        document.RootElement.GetProperty("configSha256").GetString().Should().Be(withManifest.ConfigSha256);
    }

    private static CdcConnectorTemplateResult Render(CdcConnectorTemplateRequest request)
    {
        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<TimeProvider>(new FixedTimeProvider(GeneratedAt))
            .AddCdcConnectorTemplates()
            .BuildServiceProvider();

        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        return service.Render(request);
    }

    private sealed class FixedTimeProvider(DateTimeOffset generatedAt) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => generatedAt;
    }

    private static string ProviderToken(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql => "postgresql",
            CdcProvider.SqlServer => "sqlserver",
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };
}
