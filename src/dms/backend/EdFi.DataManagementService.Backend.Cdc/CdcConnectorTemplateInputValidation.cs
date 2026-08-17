// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc;

public interface ICdcConnectorTemplateInputValidator
{
    CdcConnectorTemplateValidationResult ValidateRequest(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.RequestValidation
    );

    void ValidateRequestOrThrow(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.RequestValidation
    );
}

public sealed record CdcConnectorTemplateValidationResult
{
    public CdcConnectorTemplateValidationResult(IReadOnlyList<CdcConnectorTemplateDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        Diagnostics = diagnostics.ToArray();
    }

    public IReadOnlyList<CdcConnectorTemplateDiagnostic> Diagnostics { get; }

    public bool IsValid =>
        Diagnostics.All(diagnostic => diagnostic.Severity != CdcConnectorTemplateDiagnosticSeverity.Error);

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new CdcConnectorTemplateValidationException(Diagnostics);
        }
    }
}

public sealed class CdcConnectorTemplateValidationException : Exception
{
    public CdcConnectorTemplateValidationException(IReadOnlyList<CdcConnectorTemplateDiagnostic> diagnostics)
        : base(BuildMessage(diagnostics))
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        Diagnostics = diagnostics;
    }

    public IReadOnlyList<CdcConnectorTemplateDiagnostic> Diagnostics { get; }

    private static string BuildMessage(IReadOnlyList<CdcConnectorTemplateDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        string summary = string.Join(
            ", ",
            diagnostics
                .Where(diagnostic => diagnostic.Severity == CdcConnectorTemplateDiagnosticSeverity.Error)
                .Take(8)
                .Select(diagnostic =>
                    diagnostic.PropertyName is null
                        ? diagnostic.Code
                        : $"{diagnostic.Code}({diagnostic.PropertyName})"
                )
        );

        return summary.Length == 0
            ? "CDC connector template validation failed."
            : $"CDC connector template validation failed: {summary}.";
    }
}

public static class CdcConnectorTemplateDiagnosticCodes
{
    public const string ReservedKey = "CDC_TEMPLATE_RESERVED_KEY";
    public const string ConnectionPropertyNotAllowed = "CDC_TEMPLATE_CONNECTION_PROPERTY_NOT_ALLOWED";
    public const string KafkaSecurityPropertyNotAllowed = "CDC_TEMPLATE_KAFKA_SECURITY_PROPERTY_NOT_ALLOWED";
    public const string ExternalizedSecretReferenceRequired =
        "CDC_TEMPLATE_EXTERNALIZED_SECRET_REFERENCE_REQUIRED";
    public const string SqlServerDatabaseNamesRequired = "CDC_TEMPLATE_SQLSERVER_DATABASE_NAMES_REQUIRED";
    public const string SqlServerSingleDatabaseRequired = "CDC_TEMPLATE_SQLSERVER_SINGLE_DATABASE_REQUIRED";
    public const string PostgresqlPublicationMetadataRequired =
        "CDC_TEMPLATE_POSTGRESQL_PUBLICATION_METADATA_REQUIRED";
    public const string PostgresqlReplicationSlotMetadataRequired =
        "CDC_TEMPLATE_POSTGRESQL_REPLICATION_SLOT_METADATA_REQUIRED";
    public const string SourceTableInventoryMismatch = "CDC_TEMPLATE_SOURCE_TABLE_INVENTORY_MISMATCH";
    public const string SqlServerPollIntervalExceedsHeartbeatInterval =
        "CDC_TEMPLATE_SQLSERVER_POLL_INTERVAL_EXCEEDS_HEARTBEAT_INTERVAL";
}

internal sealed class CdcConnectorTemplateInputValidator : ICdcConnectorTemplateInputValidator
{
    private const string RedactedValue = "[redacted]";

    private static readonly IReadOnlyDictionary<
        CdcProvider,
        IReadOnlySet<string>
    > _connectionAllowListByProvider = new Dictionary<CdcProvider, IReadOnlySet<string>>
    {
        [CdcProvider.Postgresql] = new HashSet<string>(StringComparer.Ordinal)
        {
            "database.hostname",
            "database.port",
            "database.user",
            "database.password",
            "database.dbname",
            "database.sslmode",
            "database.sslrootcert",
            "database.sslcert",
            "database.sslkey",
            "database.sslpassword",
        },
        [CdcProvider.SqlServer] = new HashSet<string>(StringComparer.Ordinal)
        {
            "database.hostname",
            "database.port",
            "database.user",
            "database.password",
            "database.names",
            "database.encrypt",
            "database.trustServerCertificate",
            "database.ssl.truststore",
            "database.ssl.truststore.password",
            "database.ssl.truststore.type",
            "database.ssl.hostnameInCertificate",
        },
    };

    private static readonly IReadOnlySet<string> _kafkaSecurityAllowList = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "security.protocol",
        "sasl.mechanism",
        "sasl.jaas.config",
        "sasl.client.callback.handler.class",
        "sasl.login.callback.handler.class",
        "sasl.login.class",
        "sasl.kerberos.service.name",
        "ssl.truststore.location",
        "ssl.truststore.password",
        "ssl.truststore.type",
        "ssl.truststore.certificates",
        "ssl.keystore.location",
        "ssl.keystore.password",
        "ssl.key.password",
        "ssl.keystore.type",
        "ssl.keystore.certificate.chain",
        "ssl.keystore.key",
        "ssl.endpoint.identification.algorithm",
        "ssl.protocol",
        "ssl.enabled.protocols",
    };

    private static readonly IReadOnlySet<string> _secretPropertyNames = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "database.password",
        "database.sslpassword",
        "database.sslkey",
        "database.ssl.truststore.password",
        "sasl.jaas.config",
        "ssl.truststore.password",
        "ssl.keystore.password",
        "ssl.key.password",
        "ssl.keystore.key",
    };

    private static readonly IReadOnlySet<string> _reservedExactKeys = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "name",
        "connector.class",
        "tasks.max",
        "topic.prefix",
        "topic.delimiter",
        "topic.naming.strategy",
        "topic.heartbeat.prefix",
        "topic.heartbeat.name",
        "transforms",
        "key.converter",
        "key.converter.schemas.enable",
        "value.converter",
        "value.converter.schemas.enable",
        "value.converter.decimal.format",
        "tombstones.on.delete",
        "errors.tolerance",
        "statistics.metrics.enabled",
        "snapshot.mode",
        "heartbeat.interval.ms",
        "heartbeat.action.query",
        "message.key.columns",
        "table.include.list",
        "table.exclude.list",
        "schema.include.list",
        "schema.exclude.list",
        "database.include.list",
        "database.exclude.list",
        "column.include.list",
        "column.exclude.list",
        "include.schema.changes",
        "plugin.name",
        "publication.name",
        "publication.autocreate.mode",
        "slot.name",
        "unavailable.value.placeholder",
        "poll.interval.ms",
        "time.precision.mode",
    };

    private static readonly IReadOnlyList<string> _reservedKeyPrefixes =
    [
        "transforms.",
        "producer.override.",
        "schema.history.",
        "database.history.",
        "errors.deadletterqueue.",
        "topic.creation.",
    ];

    private static readonly IReadOnlyList<string> _generatedKafkaSecurityPrefixes =
    [
        "producer.override.",
        "schema.history.internal.producer.",
        "schema.history.internal.consumer.",
    ];

    internal static IReadOnlyList<string> ReservedManifestKeys { get; } =
        _reservedExactKeys
            .Concat(_reservedKeyPrefixes.Select(prefix => $"{prefix}*"))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

    internal static bool IsSecretBearingRenderedProperty(string propertyName)
    {
        if (_secretPropertyNames.Contains(propertyName))
        {
            return true;
        }

        string? suffix = GeneratedKafkaSecurityPropertySuffix(propertyName);
        return suffix is not null
            && (
                _secretPropertyNames.Contains(suffix)
                || suffix.EndsWith(".password", StringComparison.Ordinal)
            );
    }

    internal static string? GeneratedKafkaSecurityPropertySuffix(string propertyName)
    {
        string prefix =
            _generatedKafkaSecurityPrefixes.FirstOrDefault(prefix =>
                propertyName.StartsWith(prefix, StringComparison.Ordinal)
            ) ?? string.Empty;

        return prefix.Length == 0 ? null : propertyName[prefix.Length..];
    }

    public CdcConnectorTemplateValidationResult ValidateRequest(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.RequestValidation
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        List<CdcConnectorTemplateDiagnostic> diagnostics = [];

        ValidateProviderConnectionProperties(request, sourcePhase, diagnostics);
        ValidateKafkaSecurityProperties(request, sourcePhase, diagnostics);

        return new CdcConnectorTemplateValidationResult(diagnostics);
    }

    public void ValidateRequestOrThrow(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.RequestValidation
    ) => ValidateRequest(request, sourcePhase).ThrowIfInvalid();

    private static void ValidateProviderConnectionProperties(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        IReadOnlySet<string> allowList = _connectionAllowListByProvider[request.Provider];

        foreach (var property in request.ProviderConnectionProperties.Properties)
        {
            if (AddReservedKeyDiagnosticIfNeeded(request, sourcePhase, diagnostics, property.Key))
            {
                continue;
            }

            if (!allowList.Contains(property.Key))
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        CdcConnectorTemplateDiagnosticCodes.ConnectionPropertyNotAllowed,
                        CdcConnectorTemplateDiagnosticCategory.ConnectionProperty,
                        property.Key,
                        "allow-listed provider connection property",
                        null,
                        request,
                        sourcePhase,
                        CdcConnectorTemplateRedactionClassification.Safe
                    )
                );
                continue;
            }

            AddSecretReferenceDiagnosticIfNeeded(
                request,
                sourcePhase,
                diagnostics,
                property.Key,
                property.Value
            );
        }

        if (request.Provider == CdcProvider.SqlServer)
        {
            ValidateSqlServerDatabaseNames(request, sourcePhase, diagnostics);
        }
    }

    private static void ValidateKafkaSecurityProperties(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        foreach (var property in request.KafkaClientSecurityProperties.Properties)
        {
            if (AddReservedKeyDiagnosticIfNeeded(request, sourcePhase, diagnostics, property.Key))
            {
                continue;
            }

            if (!_kafkaSecurityAllowList.Contains(property.Key))
            {
                diagnostics.Add(
                    BuildDiagnostic(
                        CdcConnectorTemplateDiagnosticCodes.KafkaSecurityPropertyNotAllowed,
                        CdcConnectorTemplateDiagnosticCategory.KafkaSecurityProperty,
                        property.Key,
                        "unprefixed allow-listed Kafka security property",
                        null,
                        request,
                        sourcePhase,
                        CdcConnectorTemplateRedactionClassification.Safe
                    )
                );
                continue;
            }

            AddSecretReferenceDiagnosticIfNeeded(
                request,
                sourcePhase,
                diagnostics,
                property.Key,
                property.Value
            );
        }
    }

    private static void ValidateSqlServerDatabaseNames(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (!request.ProviderConnectionProperties.Properties.TryGetValue("database.names", out string? value))
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SqlServerDatabaseNamesRequired,
                    CdcConnectorTemplateDiagnosticCategory.MissingInput,
                    "database.names",
                    "exactly one SQL Server database name",
                    null,
                    request,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
            return;
        }

        string[] databaseNames = value.Split(',', StringSplitOptions.TrimEntries);
        if (databaseNames.Length == 1 && databaseNames[0].Length > 0)
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.SqlServerSingleDatabaseRequired,
                CdcConnectorTemplateDiagnosticCategory.ConnectionProperty,
                "database.names",
                "exactly one SQL Server database name",
                RedactedValue,
                request,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
            )
        );
    }

    private static bool AddReservedKeyDiagnosticIfNeeded(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics,
        string propertyName
    )
    {
        if (!IsReservedKey(propertyName))
        {
            return false;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.ReservedKey,
                CdcConnectorTemplateDiagnosticCategory.ReservedKey,
                propertyName,
                "renderer-owned connector property",
                null,
                request,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.Safe
            )
        );

        return true;
    }

    private static void AddSecretReferenceDiagnosticIfNeeded(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics,
        string propertyName,
        string value
    )
    {
        if (!_secretPropertyNames.Contains(propertyName) || IsExternalizedSecretReference(value))
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.ExternalizedSecretReferenceRequired,
                CdcConnectorTemplateDiagnosticCategory.SecretRedactionFailure,
                propertyName,
                "${env:NAME} or ${file:/absolute/path:property}",
                RedactedValue,
                request,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.SecretValue
            )
        );
    }

    private static CdcConnectorTemplateDiagnostic BuildDiagnostic(
        string code,
        CdcConnectorTemplateDiagnosticCategory category,
        string propertyName,
        string? expectedValue,
        string? observedValue,
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        CdcConnectorTemplateRedactionClassification redactionClassification
    ) =>
        new(
            code,
            category,
            CdcConnectorTemplateDiagnosticSeverity.Error,
            propertyName,
            request.ConnectorName,
            expectedValue,
            observedValue,
            request.Provider,
            sourcePhase,
            redactionClassification
        );

    internal static bool IsReservedKey(string propertyName) =>
        _reservedExactKeys.Contains(propertyName)
        || _reservedKeyPrefixes.Any(prefix => propertyName.StartsWith(prefix, StringComparison.Ordinal));

    private static bool IsExternalizedSecretReference(string value) =>
        IsEnvironmentSecretReference(value) || IsFileSecretReference(value);

    private static bool IsEnvironmentSecretReference(string value)
    {
        const string prefix = "${env:";

        if (
            !value.StartsWith(prefix, StringComparison.Ordinal)
            || !value.EndsWith("}", StringComparison.Ordinal)
        )
        {
            return false;
        }

        string variableName = value[prefix.Length..^1];
        if (variableName.Length == 0 || variableName.Any(char.IsControl))
        {
            return false;
        }

        if (!IsEnvironmentVariableStart(variableName[0]))
        {
            return false;
        }

        return variableName.Skip(1).All(IsEnvironmentVariablePart);
    }

    private static bool IsFileSecretReference(string value)
    {
        const string prefix = "${file:";

        if (
            !value.StartsWith(prefix, StringComparison.Ordinal)
            || !value.EndsWith("}", StringComparison.Ordinal)
        )
        {
            return false;
        }

        string fileReference = value[prefix.Length..^1];
        int propertySeparatorIndex = fileReference.LastIndexOf(':');
        if (propertySeparatorIndex <= 0 || propertySeparatorIndex == fileReference.Length - 1)
        {
            return false;
        }

        string path = fileReference[..propertySeparatorIndex];
        string property = fileReference[(propertySeparatorIndex + 1)..];

        return path.StartsWith("/", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(property)
            && !path.Any(char.IsControl)
            && !property.Any(char.IsControl);
    }

    private static bool IsEnvironmentVariableStart(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsEnvironmentVariablePart(char character) =>
        IsEnvironmentVariableStart(character) || character is >= '0' and <= '9';
}
