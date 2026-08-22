// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Unit.CdcConnectorTemplateTestData;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("CdcConnectorTemplateValidation")]
public class Given_CdcConnectorTemplateValidationTests
{
    [Test]
    public void It_accepts_allow_listed_postgresql_connection_and_unprefixed_kafka_security_properties()
    {
        CdcConnectorTemplateValidationResult result = Validate(
            CdcProvider.Postgresql,
            new Dictionary<string, string>
            {
                ["database.hostname"] = "postgresql.internal",
                ["database.port"] = "5432",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.dbname"] = "edfi_datastore",
                ["database.sslmode"] = "verify-full",
                ["database.sslrootcert"] = "/connect/secrets/postgresql/root.crt",
                ["database.sslcert"] = "/connect/secrets/postgresql/client.crt",
                ["database.sslkey"] = "${file:/connect/secrets/postgresql.properties:sslkey}",
                ["database.sslpassword"] = "${env:CDC_DATABASE_SSL_PASSWORD}",
            },
            new Dictionary<string, string>
            {
                ["security.protocol"] = "SASL_SSL",
                ["sasl.mechanism"] = "PLAIN",
                ["sasl.jaas.config"] = "${env:CDC_KAFKA_JAAS_CONFIG}",
                ["ssl.truststore.location"] = "/connect/secrets/kafka.truststore.p12",
                ["ssl.truststore.password"] = "${file:/connect/secrets/kafka.properties:truststorePassword}",
                ["ssl.truststore.type"] = "PKCS12",
                ["ssl.keystore.location"] = "/connect/secrets/kafka.keystore.p12",
                ["ssl.keystore.password"] = "${env:CDC_KAFKA_KEYSTORE_PASSWORD}",
                ["ssl.key.password"] = "${env:CDC_KAFKA_KEY_PASSWORD}",
                ["ssl.keystore.type"] = "PKCS12",
                ["ssl.keystore.key"] = "${file:/connect/secrets/kafka.properties:keystoreKey}",
                ["ssl.endpoint.identification.algorithm"] = "https",
                ["ssl.protocol"] = "TLSv1.3",
                ["ssl.enabled.protocols"] = "TLSv1.3,TLSv1.2",
            }
        );

        using var _ = new AssertionScope();
        result.IsValid.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_accepts_multiline_kafka_certificate_chain_material()
    {
        const string truststoreCertificateChain =
            "-----BEGIN CERTIFICATE-----\nMIIDTRUSTSTORE\n-----END CERTIFICATE-----";
        const string keystoreCertificateChain =
            "-----BEGIN CERTIFICATE-----\r\nMIIDKEYSTORE\r\n-----END CERTIFICATE-----";

        CdcConnectorTemplateValidationResult result = Validate(
            CdcProvider.Postgresql,
            kafkaSecurityProperties: new Dictionary<string, string>
            {
                ["security.protocol"] = "SSL",
                ["ssl.truststore.certificates"] = truststoreCertificateChain,
                ["ssl.keystore.certificate.chain"] = keystoreCertificateChain,
            }
        );

        using var _ = new AssertionScope();
        result.IsValid.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_control_characters_outside_kafka_certificate_chain_material()
    {
        const string multilineValue = "first line\nsecond line";

        Action providerConnectionValue = () =>
        {
            var properties = new CdcProviderConnectionProperties(
                CdcProvider.Postgresql,
                new Dictionary<string, string> { ["database.hostname"] = multilineValue }
            );
            GC.KeepAlive(properties);
        };
        Action kafkaPropertyName = () =>
        {
            var properties = new CdcKafkaClientSecurityProperties(
                new Dictionary<string, string> { ["ssl.protocol\n"] = "TLSv1.3" }
            );
            GC.KeepAlive(properties);
        };
        Action unrelatedKafkaSecurityValue = () =>
        {
            var properties = new CdcKafkaClientSecurityProperties(
                new Dictionary<string, string> { ["ssl.protocol"] = multilineValue }
            );
            GC.KeepAlive(properties);
        };
        Action secretReferenceValue = () =>
        {
            var properties = new CdcKafkaClientSecurityProperties(
                new Dictionary<string, string> { ["sasl.jaas.config"] = "${env:CDC\nPASSWORD}" }
            );
            GC.KeepAlive(properties);
        };

        using var _ = new AssertionScope();
        providerConnectionValue.Should().Throw<ArgumentException>().WithMessage("*control characters*");
        kafkaPropertyName.Should().Throw<ArgumentException>().WithMessage("*control characters*");
        unrelatedKafkaSecurityValue.Should().Throw<ArgumentException>().WithMessage("*control characters*");
        secretReferenceValue.Should().Throw<ArgumentException>().WithMessage("*control characters*");
    }

    [Test]
    public void It_accepts_allow_listed_sqlserver_driver_connection_properties()
    {
        CdcConnectorTemplateValidationResult result = Validate(
            CdcProvider.SqlServer,
            new Dictionary<string, string>
            {
                ["database.hostname"] = "sqlserver.internal",
                ["database.port"] = "1433",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.names"] = "edfi_datastore",
                ["driver.encrypt"] = "true",
                ["driver.trustServerCertificate"] = "false",
                ["driver.trustStore"] = "/connect/secrets/sqlserver.truststore.p12",
                ["driver.trustStorePassword"] = "${env:CDC_SQLSERVER_TRUSTSTORE_PASSWORD}",
                ["driver.trustStoreType"] = "PKCS12",
                ["driver.hostNameInCertificate"] = "sqlserver.internal",
            }
        );

        using var _ = new AssertionScope();
        result.IsValid.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_rejects_missing_required_provider_connection_properties()
    {
        CdcConnectorTemplateValidationResult postgresqlResult = Validate(
            CdcProvider.Postgresql,
            new Dictionary<string, string> { ["database.hostname"] = "postgresql.internal" }
        );
        CdcConnectorTemplateValidationResult sqlServerResult = Validate(
            CdcProvider.SqlServer,
            new Dictionary<string, string> { ["database.names"] = "edfi_datastore" }
        );

        CdcConnectorTemplateDiagnostic[] postgresqlRequiredDiagnostics = postgresqlResult
            .Diagnostics.Where(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.ConnectionPropertyRequired
            )
            .ToArray();
        CdcConnectorTemplateDiagnostic[] sqlServerRequiredDiagnostics = sqlServerResult
            .Diagnostics.Where(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.ConnectionPropertyRequired
            )
            .ToArray();
        CdcConnectorTemplateDiagnostic[] requiredDiagnostics = postgresqlRequiredDiagnostics
            .Concat(sqlServerRequiredDiagnostics)
            .ToArray();

        using var _ = new AssertionScope();
        postgresqlRequiredDiagnostics
            .Select(diagnostic => diagnostic.PropertyName)
            .Should()
            .BeEquivalentTo("database.user", "database.password", "database.dbname");
        sqlServerRequiredDiagnostics
            .Select(diagnostic => diagnostic.PropertyName)
            .Should()
            .BeEquivalentTo("database.hostname", "database.user", "database.password");
        requiredDiagnostics
            .Should()
            .AllSatisfy(diagnostic =>
            {
                diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.MissingRequiredInput);
                diagnostic
                    .ExpectedValue.Should()
                    .BeOneOf(
                        "required PostgreSQL connection property",
                        "required SQL Server connection property"
                    );
                diagnostic.ObservedValue.Should().BeNull();
            });
        requiredDiagnostics
            .Where(diagnostic => diagnostic.PropertyName == "database.password")
            .Should()
            .OnlyContain(diagnostic =>
                diagnostic.RedactionClassification == CdcConnectorTemplateRedactionClassification.SecretValue
            );
        requiredDiagnostics
            .Where(diagnostic => diagnostic.PropertyName != "database.password")
            .Should()
            .OnlyContain(diagnostic =>
                diagnostic.RedactionClassification
                == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
    }

    [Test]
    public void It_rejects_connection_and_security_properties_outside_the_provider_allow_lists()
    {
        CdcConnectorTemplateValidationResult postgresqlResult = Validate(
            CdcProvider.Postgresql,
            new Dictionary<string, string>
            {
                ["database.hostname"] = "postgresql.internal",
                ["database.server.name"] = "not-allowed",
            }
        );
        CdcConnectorTemplateValidationResult sqlServerResult = Validate(
            CdcProvider.SqlServer,
            new Dictionary<string, string>
            {
                ["database.names"] = "edfi_datastore",
                ["database.dbname"] = "not-allowed",
            }
        );
        CdcConnectorTemplateValidationResult kafkaResult = Validate(
            CdcProvider.Postgresql,
            kafkaSecurityProperties: new Dictionary<string, string>
            {
                ["security.protocol"] = "SSL",
                ["ssl.unknown"] = "not-allowed",
            }
        );

        using var _ = new AssertionScope();
        postgresqlResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.ConnectionPropertyNotAllowed
                && diagnostic.PropertyName == "database.server.name"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation
            );
        sqlServerResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.ConnectionPropertyNotAllowed
                && diagnostic.PropertyName == "database.dbname"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation
            );
        kafkaResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.KafkaSecurityPropertyNotAllowed
                && diagnostic.PropertyName == "ssl.unknown"
                && diagnostic.Category
                    == CdcConnectorTemplateDiagnosticCategory.KafkaSecurityPropertyViolation
            );
    }

    [Test]
    public void It_rejects_obsolete_sqlserver_database_tls_connection_properties()
    {
        string[] obsoleteSqlServerProperties =
        [
            "database.encrypt",
            "database.trustServerCertificate",
            "database.ssl.truststore",
            "database.ssl.truststore.password",
            "database.ssl.truststore.type",
            "database.ssl.hostnameInCertificate",
        ];
        CdcConnectorTemplateValidationResult result = Validate(
            CdcProvider.SqlServer,
            new Dictionary<string, string>
            {
                ["database.hostname"] = "sqlserver.internal",
                ["database.port"] = "1433",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.names"] = "edfi_datastore",
                ["database.encrypt"] = "true",
                ["database.trustServerCertificate"] = "true",
                ["database.ssl.truststore"] = "/connect/secrets/sqlserver.truststore.p12",
                ["database.ssl.truststore.password"] = "${env:CDC_SQLSERVER_TRUSTSTORE_PASSWORD}",
                ["database.ssl.truststore.type"] = "PKCS12",
                ["database.ssl.hostnameInCertificate"] = "sqlserver.internal",
            }
        );

        using var _ = new AssertionScope();
        result.IsValid.Should().BeFalse();
        result
            .Diagnostics.Where(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.ConnectionPropertyNotAllowed
            )
            .Select(diagnostic => diagnostic.PropertyName)
            .Should()
            .BeEquivalentTo(obsoleteSqlServerProperties);
        result
            .Diagnostics.Should()
            .AllSatisfy(diagnostic =>
            {
                diagnostic
                    .Category.Should()
                    .Be(CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation);
                diagnostic.ObservedValue.Should().BeNull();
            });
    }

    [Test]
    public void It_rejects_sqlserver_without_exactly_one_database_name()
    {
        CdcConnectorTemplateValidationResult missingDatabaseName = Validate(
            CdcProvider.SqlServer,
            new Dictionary<string, string> { ["database.hostname"] = "sqlserver.internal" }
        );
        CdcConnectorTemplateValidationResult multipleDatabaseNames = Validate(
            CdcProvider.SqlServer,
            new Dictionary<string, string>
            {
                ["database.hostname"] = "sqlserver.internal",
                ["database.names"] = "edfi_datastore, other_datastore",
            }
        );

        using var _ = new AssertionScope();
        missingDatabaseName
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SqlServerDatabaseNamesRequired
                && diagnostic.PropertyName == "database.names"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MissingRequiredInput
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        multipleDatabaseNames
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SqlServerSingleDatabaseRequired
                && diagnostic.PropertyName == "database.names"
                && diagnostic.ObservedValue == "[redacted]"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
    }

    [TestCase(" edfi_datastore")]
    [TestCase("edfi_datastore ")]
    [TestCase("edfi_datastore, other_datastore")]
    [TestCase("edfi_datastore,")]
    public void It_rejects_sqlserver_database_names_that_are_not_one_canonical_token(string databaseNames)
    {
        CdcConnectorTemplateValidationResult result = Validate(
            CdcProvider.SqlServer,
            new Dictionary<string, string>
            {
                ["database.hostname"] = "sqlserver.internal",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.names"] = databaseNames,
            }
        );

        CdcConnectorTemplateDiagnostic diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SqlServerSingleDatabaseRequired
                && diagnostic.PropertyName == "database.names"
            )
            .Subject;

        using var _ = new AssertionScope();
        result.IsValid.Should().BeFalse();
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation);
        diagnostic.ExpectedValue.Should().Be("exactly one SQL Server database name");
        diagnostic.ObservedValue.Should().Be("[redacted]");
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
        result
            .Diagnostics.SelectMany(DiagnosticText)
            .Should()
            .NotContain(value => value.Contains("edfi_datastore", StringComparison.Ordinal));
    }

    [Test]
    public void It_rejects_reserved_generated_connector_keys_even_when_the_value_matches_the_contract()
    {
        CdcConnectorTemplateValidationResult result = Validate(
            CdcProvider.Postgresql,
            new Dictionary<string, string>
            {
                ["database.hostname"] = "postgresql.internal",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.dbname"] = "edfi_datastore",
                ["connector.class"] = "io.debezium.connector.postgresql.PostgresConnector",
                ["heartbeat.interval.ms"] = "5000",
                ["topic.prefix"] = "dms_binding_connector",
                ["producer.override.acks"] = "all",
            },
            new Dictionary<string, string> { ["schema.history.internal.producer.security.protocol"] = "SSL" }
        );

        using var _ = new AssertionScope();
        result.Diagnostics.Should().HaveCount(5);
        result
            .Diagnostics.Select(diagnostic => diagnostic.Code)
            .Should()
            .OnlyContain(code => code == CdcConnectorTemplateDiagnosticCodes.ReservedKey);
        result
            .Diagnostics.Select(diagnostic => diagnostic.Category)
            .Should()
            .OnlyContain(category => category == CdcConnectorTemplateDiagnosticCategory.ReservedKeyViolation);
        result
            .Diagnostics.Select(diagnostic => diagnostic.PropertyName)
            .Should()
            .BeEquivalentTo(
                "connector.class",
                "heartbeat.interval.ms",
                "topic.prefix",
                "producer.override.acks",
                "schema.history.internal.producer.security.protocol"
            );
    }

    [Test]
    public void It_requires_externalized_secret_references_and_redacts_raw_secret_diagnostics()
    {
        const string rawSecret =
            "Server=unsafe-prod;Password=should-not-leak;Tenant=GrandBend;{\"documentUuid\":\"abc\"}";

        CdcConnectorTemplateValidationResult result = Validate(
            CdcProvider.Postgresql,
            new Dictionary<string, string>
            {
                ["database.hostname"] = "postgresql.internal",
                ["database.user"] = "connector_user",
                ["database.password"] = rawSecret,
                ["database.dbname"] = "edfi_datastore",
            },
            new Dictionary<string, string> { ["sasl.jaas.config"] = rawSecret }
        );
        Action act = () => result.ThrowIfInvalid();

        using var _ = new AssertionScope();
        result.IsValid.Should().BeFalse();
        result.Diagnostics.Should().HaveCount(2);
        result
            .Diagnostics.Should()
            .OnlyContain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.ExternalizedSecretReferenceRequired
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.SecretRedactionViolation
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
            .NotContain(value => value!.Contains(rawSecret, StringComparison.Ordinal));
        act.Should()
            .Throw<CdcConnectorTemplateValidationException>()
            .Where(exception => !exception.ToString().Contains(rawSecret, StringComparison.Ordinal));
    }

    [Test]
    public void It_requires_externalized_sqlserver_driver_truststore_password()
    {
        const string rawSecret =
            "Server=unsafe-prod;Password=should-not-leak;Tenant=GrandBend;{\"documentUuid\":\"abc\"}";
        CdcConnectorTemplateValidationResult result = Validate(
            CdcProvider.SqlServer,
            new Dictionary<string, string>
            {
                ["database.hostname"] = "sqlserver.internal",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.names"] = "edfi_datastore",
                ["driver.trustStorePassword"] = rawSecret,
            }
        );
        Action act = () => result.ThrowIfInvalid();

        CdcConnectorTemplateDiagnostic diagnostic = result.Diagnostics.Should().ContainSingle().Subject;

        using var _ = new AssertionScope();
        result.IsValid.Should().BeFalse();
        diagnostic.Code.Should().Be(CdcConnectorTemplateDiagnosticCodes.ExternalizedSecretReferenceRequired);
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.SecretRedactionViolation);
        diagnostic.PropertyName.Should().Be("driver.trustStorePassword");
        diagnostic.ObservedValue.Should().Be("[redacted]");
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.SecretValue);
        result
            .Diagnostics.SelectMany(DiagnosticText)
            .Should()
            .NotContain(value => value.Contains(rawSecret, StringComparison.Ordinal));
        act.Should()
            .Throw<CdcConnectorTemplateValidationException>()
            .Where(exception => !exception.ToString().Contains(rawSecret, StringComparison.Ordinal));
    }

    [Test]
    public void It_accepts_exact_file_secret_references()
    {
        CdcConnectorTemplateValidationResult result = Validate(
            CdcProvider.Postgresql,
            BuildPostgresqlConnectionProperties("${file:/run/secrets/db:password}")
        );

        using var _ = new AssertionScope();
        result.IsValid.Should().BeTrue();
        result.Diagnostics.Should().BeEmpty();
    }

    [TestCase("${file:/run/secrets/db:password}Passw0rd}")]
    [TestCase("${file:/run/secrets/db:password}}")]
    [TestCase("${file:relative/path:password}")]
    [TestCase("${file:/run/secrets/db:}")]
    [TestCase("${file:/run/secrets/db:password")]
    [TestCase("${env:CDC_DATABASE_PASSWORD}Passw0rd}")]
    public void It_rejects_malformed_externalized_secret_references_without_leaking_them(
        string malformedReference
    )
    {
        CdcConnectorTemplateValidationResult result = Validate(
            CdcProvider.Postgresql,
            BuildPostgresqlConnectionProperties(malformedReference)
        );
        Action act = () => result.ThrowIfInvalid();

        CdcConnectorTemplateDiagnostic diagnostic = result.Diagnostics.Should().ContainSingle().Subject;

        using var _ = new AssertionScope();
        result.IsValid.Should().BeFalse();
        diagnostic.Code.Should().Be(CdcConnectorTemplateDiagnosticCodes.ExternalizedSecretReferenceRequired);
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.SecretRedactionViolation);
        diagnostic.PropertyName.Should().Be("database.password");
        diagnostic.ObservedValue.Should().Be("[redacted]");
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.SecretValue);
        result
            .Diagnostics.SelectMany(DiagnosticText)
            .Should()
            .NotContain(value => value.Contains(malformedReference, StringComparison.Ordinal));
        act.Should()
            .Throw<CdcConnectorTemplateValidationException>()
            .Where(exception => !exception.ToString().Contains(malformedReference, StringComparison.Ordinal));
    }

    [Test]
    public void It_preserves_source_phase_provider_and_safe_object_name_in_diagnostics()
    {
        CdcConnectorTemplateValidationResult result = Validate(
            CdcProvider.SqlServer,
            new Dictionary<string, string>
            {
                ["database.hostname"] = "sqlserver.internal",
                ["database.user"] = "connector_user",
                ["database.password"] = "${env:CDC_DATABASE_PASSWORD}",
                ["database.names"] = "edfi_datastore",
                ["connector.class"] = "io.debezium.connector.sqlserver.SqlServerConnector",
            },
            sourcePhase: CdcConnectorTemplateSourcePhase.Preflight
        );

        CdcConnectorTemplateDiagnostic diagnostic = result.Diagnostics.Should().ContainSingle().Subject;

        using var _ = new AssertionScope();
        diagnostic.Provider.Should().Be(CdcProvider.SqlServer);
        diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.Preflight);
        diagnostic.SafeArtifactOrObjectName.Should().Be(new CdcSafeName("dms_binding_connector"));
        diagnostic.Severity.Should().Be(CdcConnectorTemplateDiagnosticSeverity.Error);
    }

    [Test]
    public void It_rejects_missing_postgresql_provider_artifacts_during_public_request_validation()
    {
        CdcConnectorTemplateValidationResult result = Validate(
            BuildRequest(CdcProvider.Postgresql, artifactInventory: [])
        );

        using var _ = new AssertionScope();
        result.IsValid.Should().BeFalse();
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
                diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Render
                && diagnostic.ExpectedValue == "one matched provider setup artifact"
            );
    }

    [Test]
    public void It_treats_null_postgresql_provider_artifacts_as_missing_during_public_request_validation()
    {
        CdcProviderSetupResult providerSetupResult = BuildProviderSetupResult(CdcProvider.Postgresql) with
        {
            ArtifactInventory = null!,
        };

        CdcConnectorTemplateValidationResult result = Validate(
            BuildRequest(
                providerSetupResult,
                providerConnectionProperties: new CdcProviderConnectionProperties(
                    CdcProvider.Postgresql,
                    BuildProviderConnectionProperties(CdcProvider.Postgresql)
                ),
                deploymentPolicy: BuildDeploymentPolicy(CdcProvider.Postgresql)
            )
        );

        using var _ = new AssertionScope();
        result.IsValid.Should().BeFalse();
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
                diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.ObservedValue == "missing"
            );
    }

    [Test]
    public void It_rejects_provider_setup_source_table_name_drift_during_public_request_validation()
    {
        CdcConnectorTemplateValidationResult result = Validate(
            BuildRequest(
                CdcProvider.Postgresql,
                sourceTableInventory: BuildSourceInventoryReplacing(
                    CdcProvider.Postgresql,
                    BuildSourceTable(
                        CdcProvider.Postgresql,
                        CdcSourceTableKind.Document,
                        "DocumentProjectionWork;DROP TABLE",
                        [BuildColumn(CdcProvider.Postgresql, "DocumentUuid")]
                    )
                )
            )
        );

        CdcConnectorTemplateDiagnostic diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceTableInventoryMismatch
            )
            .Subject;

        using var _ = new AssertionScope();
        result.IsValid.Should().BeFalse();
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.IncludeListViolation);
        diagnostic.PropertyName.Should().Be("table.include.list");
        diagnostic.ExpectedValue.Should().Be("dms.Document");
        diagnostic.ObservedValue.Should().Be("dms.DocumentProjectionWork_DROP_TABLE");
        diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.Render);
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
    }

    [Test]
    public void It_rejects_message_key_source_column_drift_during_public_request_validation()
    {
        CdcConnectorTemplateValidationResult result = Validate(
            BuildRequest(
                CdcProvider.Postgresql,
                sourceTableInventory:
                [
                    BuildSourceTable(
                        CdcProvider.Postgresql,
                        CdcSourceTableKind.DocumentCache,
                        "DocumentCache",
                        [BuildColumn(CdcProvider.Postgresql, "DocumentUuid;DROP_TABLE")]
                    ),
                    BuildSourceTable(
                        CdcProvider.Postgresql,
                        CdcSourceTableKind.Document,
                        "Document",
                        [
                            BuildColumn(CdcProvider.Postgresql, "DocumentUuid"),
                            BuildColumn(CdcProvider.Postgresql, "DocumentUuid", 2),
                        ]
                    ),
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
                ]
            )
        );

        using var _ = new AssertionScope();
        result.IsValid.Should().BeFalse();
        result
            .Diagnostics.Should()
            .Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
                && diagnostic.PropertyName == "message.key.columns"
                && diagnostic.ExpectedValue == "source column DocumentUuid for dms.DocumentCache"
                && diagnostic.ObservedValue == "missing"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Render
            )
            .And.Contain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.SourceColumnInventoryMismatch
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation
                && diagnostic.PropertyName == "message.key.columns"
                && diagnostic.ExpectedValue == "unique source column names for dms.Document"
                && diagnostic.ObservedValue == "duplicate"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Render
            );
        result
            .Diagnostics.Should()
            .OnlyContain(diagnostic =>
                diagnostic.RedactionClassification
                == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
        string.Join("|", result.Diagnostics.SelectMany(DiagnosticText))
            .Should()
            .NotContain("DROP_TABLE", because: "raw source column names are redacted");
    }

    [Test]
    public void It_rejects_malformed_source_column_inventory_during_public_request_validation()
    {
        CdcConnectorTemplateValidationResult result = Validate(
            BuildRequest(
                CdcProvider.Postgresql,
                sourceTableInventory: BuildSourceInventoryReplacing(
                    CdcProvider.Postgresql,
                    BuildSourceTableWithNullColumnEntry(
                        CdcProvider.Postgresql,
                        CdcSourceTableKind.DocumentCache,
                        "DocumentCache"
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
        result.IsValid.Should().BeFalse();
        diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.MessageKeyViolation);
        diagnostic.PropertyName.Should().Be("providerSetup.sourceTableInventory.columns");
        diagnostic.ExpectedValue.Should().Be("non-null source column inventory for dms.DocumentCache");
        diagnostic.ObservedValue.Should().Be("malformed");
        diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.Render);
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
    }

    [Test]
    public void It_rejects_sqlserver_capture_instance_artifact_inventory_drift_during_public_request_validation()
    {
        CdcConnectorTemplateValidationResult emptyInventory = Validate(
            BuildRequest(CdcProvider.SqlServer, artifactInventory: [])
        );
        CdcConnectorTemplateValidationResult missingDocument = Validate(
            BuildRequest(
                CdcProvider.SqlServer,
                artifactInventory:
                [
                    BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.DocumentCache),
                    BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.CdcHeartbeat),
                ]
            )
        );
        CdcConnectorTemplateValidationResult duplicateDocument = Validate(
            BuildRequest(
                CdcProvider.SqlServer,
                artifactInventory:
                [
                    BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.DocumentCache),
                    BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.Document),
                    BuildSqlServerCaptureInstanceArtifact(
                        CdcSourceTableKind.Document,
                        safeArtifactName: new CdcSafeName("dms_binding_document_duplicate_capture")
                    ),
                    BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.CdcHeartbeat),
                ]
            )
        );
        CdcConnectorTemplateValidationResult mismatchedDocument = Validate(
            BuildRequest(
                CdcProvider.SqlServer,
                artifactInventory:
                [
                    BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.DocumentCache),
                    BuildSqlServerCaptureInstanceArtifact(
                        CdcSourceTableKind.Document,
                        CdcProviderArtifactState.Mismatched
                    ),
                    BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.CdcHeartbeat),
                ]
            )
        );
        CdcConnectorTemplateValidationResult unavailableHeartbeat = Validate(
            BuildRequest(
                CdcProvider.SqlServer,
                artifactInventory:
                [
                    BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.DocumentCache),
                    BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.Document),
                    BuildSqlServerCaptureInstanceArtifact(
                        CdcSourceTableKind.CdcHeartbeat,
                        CdcProviderArtifactState.Unavailable
                    ),
                ]
            )
        );
        CdcConnectorTemplateValidationResult extraCaptureInstance = Validate(
            BuildRequest(
                CdcProvider.SqlServer,
                artifactInventory:
                [
                    .. BuildSqlServerArtifactInventory(),
                    BuildSqlServerCaptureInstanceArtifact(
                        CdcSourceTableKind.Document,
                        CdcProviderArtifactState.Mismatched,
                        new CdcSafeName("dms_binding_projection_work_capture"),
                        new Dictionary<string, string> { ["source_table_kind"] = "document_projection_work" }
                    ),
                ]
            )
        );

        using var _ = new AssertionScope();
        emptyInventory.IsValid.Should().BeFalse();
        emptyInventory
            .Diagnostics.Where(diagnostic =>
                diagnostic.Code
                == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
            )
            .Should()
            .HaveCount(3)
            .And.OnlyContain(diagnostic =>
                diagnostic.ObservedValue == "missing"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Render
            );
        missingDocument
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code
                    == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
                && diagnostic.ExpectedValue
                    == "one usable SQL Server capture-instance artifact for dms.Document"
                && diagnostic.ObservedValue == "missing"
            );
        duplicateDocument
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code
                    == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
                && diagnostic.ExpectedValue
                    == "one usable SQL Server capture-instance artifact for dms.Document"
                && diagnostic.ObservedValue == "2"
            );
        mismatchedDocument
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code
                    == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
                && diagnostic.ExpectedValue
                    == "one usable SQL Server capture-instance artifact for dms.Document"
                && diagnostic.ObservedValue == "Mismatched"
            );
        unavailableHeartbeat
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code
                    == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
                && diagnostic.ExpectedValue
                    == "one usable SQL Server capture-instance artifact for dms.CdcHeartbeat"
                && diagnostic.ObservedValue == "Unavailable"
            );
        extraCaptureInstance
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code
                    == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
                && diagnostic.ExpectedValue
                    == "only SQL Server capture-instance artifacts for dms.DocumentCache, dms.Document, and dms.CdcHeartbeat"
                && diagnostic.ObservedValue == "1"
            );
        new[]
        {
            missingDocument,
            duplicateDocument,
            mismatchedDocument,
            unavailableHeartbeat,
            extraCaptureInstance,
        }
            .SelectMany(result => result.Diagnostics)
            .Should()
            .OnlyContain(diagnostic =>
                diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure
                && diagnostic.PropertyName == "providerSetup.artifactInventory.sqlServerCaptureInstance"
                && diagnostic.RedactionClassification
                    == CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            );
    }

    [Test]
    public void It_treats_null_sqlserver_capture_instance_observed_values_as_missing_during_public_request_validation()
    {
        CdcProviderArtifactObservation malformedDocumentArtifact = BuildSqlServerCaptureInstanceArtifact(
            CdcSourceTableKind.Document
        ) with
        {
            SafeObservedValues = null!,
        };

        CdcConnectorTemplateValidationResult result = Validate(
            BuildRequest(
                CdcProvider.SqlServer,
                artifactInventory:
                [
                    BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.DocumentCache),
                    malformedDocumentArtifact,
                    BuildSqlServerCaptureInstanceArtifact(CdcSourceTableKind.CdcHeartbeat),
                ]
            )
        );

        using var _ = new AssertionScope();
        result.IsValid.Should().BeFalse();
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code
                    == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
                && diagnostic.ExpectedValue
                    == "one usable SQL Server capture-instance artifact for dms.Document"
                && diagnostic.ObservedValue == "missing"
                && diagnostic.SourcePhase == CdcConnectorTemplateSourcePhase.Render
            )
            .And.ContainSingle(diagnostic =>
                diagnostic.Code
                    == CdcConnectorTemplateDiagnosticCodes.SqlServerCaptureInstanceMetadataRequired
                && diagnostic.ExpectedValue
                    == "only SQL Server capture-instance artifacts for dms.DocumentCache, dms.Document, and dms.CdcHeartbeat"
                && diagnostic.ObservedValue == "1"
            );
        string.Join("|", result.Diagnostics.SelectMany(DiagnosticText))
            .Should()
            .NotContain("${env:CDC_DATABASE_PASSWORD}");
    }

    [Test]
    public void It_rejects_missing_sqlserver_poll_interval_during_public_request_validation()
    {
        CdcConnectorTemplateValidationResult result = Validate(
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
        result.IsValid.Should().BeFalse();
        diagnostic
            .Category.Should()
            .Be(CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation);
        diagnostic.PropertyName.Should().Be("poll.interval.ms");
        diagnostic.ExpectedValue.Should().Be("positive SQL Server poll interval");
        diagnostic.ObservedValue.Should().BeNull();
        diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.Render);
    }

    [Test]
    public void It_rejects_sqlserver_poll_interval_greater_than_heartbeat_during_public_request_validation()
    {
        CdcConnectorTemplateValidationResult result = Validate(
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
        result.IsValid.Should().BeFalse();
        diagnostic
            .Category.Should()
            .Be(CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation);
        diagnostic.PropertyName.Should().Be("poll.interval.ms");
        diagnostic.ExpectedValue.Should().Be("<= heartbeat.interval.ms (5000)");
        diagnostic.ObservedValue.Should().Be("6000");
        diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.Render);
    }

    [Test]
    public void It_reports_null_heartbeat_action_query_as_missing_provider_setup_readiness()
    {
        CdcProviderSetupReadiness readiness = GetProviderSetupReadiness(
            BuildProviderSetupResult(CdcProvider.Postgresql, omitHeartbeatActionQuery: true)
        );

        CdcConnectorTemplateDiagnostic diagnostic = readiness
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.HeartbeatActionQueryRequired
            )
            .Subject;

        using var _ = new AssertionScope();
        readiness.CanRenderTemplate.Should().BeFalse();
        diagnostic
            .Category.Should()
            .Be(CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation);
        diagnostic.PropertyName.Should().Be("providerSetup.heartbeatActionQuery");
        diagnostic.ExpectedValue.Should().Be("fresh provider heartbeat action query");
        diagnostic.ObservedValue.Should().Be("missing");
        diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.Render);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\t\r\n")]
    public void It_rejects_blank_heartbeat_action_sql_during_public_request_validation(string heartbeatSql)
    {
        CdcConnectorTemplateValidationResult result = Validate(
            BuildRequest(CdcProvider.Postgresql, heartbeatSql: heartbeatSql)
        );

        CdcConnectorTemplateDiagnostic diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.HeartbeatActionQueryRequired
            )
            .Subject;

        using var _ = new AssertionScope();
        result.IsValid.Should().BeFalse();
        diagnostic
            .Category.Should()
            .Be(CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation);
        diagnostic.PropertyName.Should().Be("providerSetup.heartbeatActionQuery");
        diagnostic.ExpectedValue.Should().Be("fresh provider heartbeat action query");
        diagnostic.ObservedValue.Should().Be("[redacted]");
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
        diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.Render);
    }

    [TestCase("update dms.CdcHeartbeat\nset HeartbeatSequence = HeartbeatSequence + 1")]
    [TestCase("update dms.CdcHeartbeat\rset HeartbeatSequence = HeartbeatSequence + 1")]
    [TestCase("update dms.CdcHeartbeat\tset HeartbeatSequence = HeartbeatSequence + 1")]
    [TestCase("update dms.CdcHeartbeat\u0001set HeartbeatSequence = HeartbeatSequence + 1")]
    public void It_rejects_control_characters_in_heartbeat_action_sql_during_public_request_validation(
        string heartbeatSql
    )
    {
        CdcConnectorTemplateValidationResult result = Validate(
            BuildRequest(CdcProvider.Postgresql, heartbeatSql: heartbeatSql)
        );

        CdcConnectorTemplateDiagnostic diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.HeartbeatActionQueryRequired
            )
            .Subject;

        using var _ = new AssertionScope();
        result.IsValid.Should().BeFalse();
        diagnostic
            .Category.Should()
            .Be(CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation);
        diagnostic.PropertyName.Should().Be("providerSetup.heartbeatActionQuery");
        diagnostic.ExpectedValue.Should().Be("fresh provider heartbeat action query");
        diagnostic.ObservedValue.Should().Be("[redacted]");
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
        diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.Render);
        result.Diagnostics.SelectMany(DiagnosticText).Should().NotContain(heartbeatSql);
    }

    [Test]
    public void It_returns_validation_failed_instead_of_throwing_when_rendering_blank_heartbeat_action_sql()
    {
        CdcConnectorTemplateResult result = Render(
            BuildRequest(CdcProvider.Postgresql, heartbeatSql: " \t ")
        );

        CdcConnectorTemplateDiagnostic diagnostic = result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.HeartbeatActionQueryRequired
            )
            .Subject;

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        result.RegistrationPayload.Should().BeNull();
        diagnostic.ObservedValue.Should().Be("[redacted]");
    }

    [Test]
    public void It_returns_validation_failed_instead_of_throwing_when_rendering_heartbeat_action_sql_with_control_characters()
    {
        const string heartbeatSql = "update dms.CdcHeartbeat\nset HeartbeatSequence = HeartbeatSequence + 1";
        CdcConnectorTemplateResult? result = null;

        Action render = () =>
            result = Render(BuildRequest(CdcProvider.Postgresql, heartbeatSql: heartbeatSql));

        render.Should().NotThrow();
        result.Should().NotBeNull();
        CdcConnectorTemplateDiagnostic diagnostic = result!
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.HeartbeatActionQueryRequired
            )
            .Subject;

        using var _ = new AssertionScope();
        result!.Outcome.Should().Be(CdcConnectorTemplateOutcome.ValidationFailed);
        result.Config.Should().BeEmpty();
        result.RegistrationPayload.Should().BeNull();
        diagnostic
            .Category.Should()
            .Be(CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation);
        diagnostic.ObservedValue.Should().Be("[redacted]");
        diagnostic
            .RedactionClassification.Should()
            .Be(CdcConnectorTemplateRedactionClassification.PhysicalIdentifier);
        result.Diagnostics.SelectMany(DiagnosticText).Should().NotContain(heartbeatSql);
    }

    [TestCase(CdcProvider.Postgresql, "select 1")]
    [TestCase(CdcProvider.SqlServer, "UPDATE [dms].[CdcHeartbeat] SET [LastSeenAt] = SYSUTCDATETIME()")]
    public void It_renders_valid_non_blank_heartbeat_action_sql(CdcProvider provider, string heartbeatSql)
    {
        CdcConnectorTemplateResult result = Render(BuildRequest(provider, heartbeatSql: heartbeatSql));

        using var _ = new AssertionScope();
        result.Outcome.Should().Be(CdcConnectorTemplateOutcome.Rendered);
        result.Config["heartbeat.action.query"].Should().Be(heartbeatSql);
        result
            .Diagnostics.Should()
            .NotContain(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.HeartbeatActionQueryRequired
            );
    }

    private static CdcConnectorTemplateValidationResult Validate(
        CdcProvider provider,
        IReadOnlyDictionary<string, string>? providerConnectionProperties = null,
        IReadOnlyDictionary<string, string>? kafkaSecurityProperties = null,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.Render
    )
    {
        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddCdcConnectorTemplates()
            .BuildServiceProvider();

        ICdcConnectorTemplateInputValidator validator =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateInputValidator>();

        return validator.ValidateRequest(
            BuildRequest(
                provider,
                providerConnectionProperties: providerConnectionProperties,
                kafkaSecurityProperties: kafkaSecurityProperties
            ),
            sourcePhase
        );
    }

    private static CdcConnectorTemplateValidationResult Validate(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.Render
    )
    {
        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddCdcConnectorTemplates()
            .BuildServiceProvider();

        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        return service.ValidateRequest(request, sourcePhase);
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

    private static CdcProviderSetupReadiness GetProviderSetupReadiness(
        CdcProviderSetupResult providerSetupResult
    )
    {
        using ServiceProvider serviceProvider = new ServiceCollection()
            .AddCdcConnectorTemplates()
            .BuildServiceProvider();

        ICdcConnectorTemplateService service =
            serviceProvider.GetRequiredService<ICdcConnectorTemplateService>();

        return service.GetProviderSetupReadiness(providerSetupResult);
    }

    private static IEnumerable<string> DiagnosticText(CdcConnectorTemplateDiagnostic diagnostic) =>
        [diagnostic.ExpectedValue ?? string.Empty, diagnostic.ObservedValue ?? string.Empty];

    private static CdcSourceTableInventory BuildSourceTableWithNullColumnEntry(
        CdcProvider provider,
        CdcSourceTableKind tableKind,
        string tableName
    )
    {
        CdcSourceTableInventory sourceTable = BuildSourceTable(
            provider,
            tableKind,
            tableName,
            [BuildColumn(provider, "DocumentUuid")]
        );
        FieldInfo columnsField =
            typeof(CdcSourceTableInventory).GetField(
                "<Columns>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic
            ) ?? throw new InvalidOperationException("Could not locate CdcSourceTableInventory.Columns.");
        columnsField.SetValue(sourceTable, new CdcSourceColumnInventory[] { null! });

        return sourceTable;
    }
}
