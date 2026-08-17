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
[Category("CdcConnectorTemplateValidation")]
public class Given_CdcConnectorTemplateInputValidation
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
                diagnostic.Category.Should().Be(CdcConnectorTemplateDiagnosticCategory.MissingInput);
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
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ConnectionProperty
            );
        sqlServerResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.ConnectionPropertyNotAllowed
                && diagnostic.PropertyName == "database.dbname"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.ConnectionProperty
            );
        kafkaResult
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == CdcConnectorTemplateDiagnosticCodes.KafkaSecurityPropertyNotAllowed
                && diagnostic.PropertyName == "ssl.unknown"
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.KafkaSecurityProperty
            );
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
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.MissingInput
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
            .OnlyContain(category => category == CdcConnectorTemplateDiagnosticCategory.ReservedKey);
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
                && diagnostic.Category == CdcConnectorTemplateDiagnosticCategory.SecretRedactionFailure
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
            sourcePhase: CdcConnectorTemplateSourcePhase.RegistrationPreflight
        );

        CdcConnectorTemplateDiagnostic diagnostic = result.Diagnostics.Should().ContainSingle().Subject;

        using var _ = new AssertionScope();
        diagnostic.Provider.Should().Be(CdcProvider.SqlServer);
        diagnostic.SourcePhase.Should().Be(CdcConnectorTemplateSourcePhase.RegistrationPreflight);
        diagnostic.SafeArtifactOrObjectName.Should().Be(new CdcSafeName("dms_binding_connector"));
        diagnostic.Severity.Should().Be(CdcConnectorTemplateDiagnosticSeverity.Error);
    }

    private static CdcConnectorTemplateValidationResult Validate(
        CdcProvider provider,
        IReadOnlyDictionary<string, string>? providerConnectionProperties = null,
        IReadOnlyDictionary<string, string>? kafkaSecurityProperties = null,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.RequestValidation
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
}
