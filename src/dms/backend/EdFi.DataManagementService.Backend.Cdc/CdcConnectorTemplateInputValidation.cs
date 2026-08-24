// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc;

internal interface ICdcConnectorTemplateInputValidator
{
    CdcConnectorTemplateValidationResult ValidateRequest(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.Render
    );
}

internal sealed record CdcConnectorTemplateValidationResult
{
    public CdcConnectorTemplateValidationResult(IReadOnlyList<CdcConnectorTemplateDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        Diagnostics = diagnostics.ToArray();
    }

    public IReadOnlyList<CdcConnectorTemplateDiagnostic> Diagnostics { get; }

    public bool IsValid =>
        Diagnostics.All(diagnostic => diagnostic.Severity != CdcConnectorTemplateDiagnosticSeverity.Error);
}

public static class CdcConnectorTemplateDiagnosticCodes
{
    public const string BindingIdentityMismatch = "CDC_TEMPLATE_BINDING_IDENTITY_MISMATCH";
    public const string ProviderSetupResultNotReady = "CDC_TEMPLATE_PROVIDER_SETUP_RESULT_NOT_READY";
    public const string SourceFingerprintEvidenceRequired =
        "CDC_TEMPLATE_SOURCE_FINGERPRINT_EVIDENCE_REQUIRED";
    public const string HeartbeatActionQueryRequired = "CDC_TEMPLATE_HEARTBEAT_ACTION_QUERY_REQUIRED";
    public const string ReservedKey = "CDC_TEMPLATE_RESERVED_KEY";
    public const string ConnectionPropertyRequired = "CDC_TEMPLATE_CONNECTION_PROPERTY_REQUIRED";
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
    public const string ProviderSetupArtifactInventoryMalformed =
        "CDC_TEMPLATE_PROVIDER_SETUP_ARTIFACT_INVENTORY_MALFORMED";
    public const string SqlServerGatingRoleMetadataRequired =
        "CDC_TEMPLATE_SQLSERVER_GATING_ROLE_METADATA_REQUIRED";
    public const string SqlServerSnapshotIsolationMetadataRequired =
        "CDC_TEMPLATE_SQLSERVER_SNAPSHOT_ISOLATION_METADATA_REQUIRED";
    public const string SqlServerCaptureInstanceMetadataRequired =
        "CDC_TEMPLATE_SQLSERVER_CAPTURE_INSTANCE_METADATA_REQUIRED";
    public const string SourceTableInventoryMismatch = "CDC_TEMPLATE_SOURCE_TABLE_INVENTORY_MISMATCH";
    public const string SourceColumnInventoryMismatch = "CDC_TEMPLATE_SOURCE_COLUMN_INVENTORY_MISMATCH";
    public const string SqlServerPollIntervalRequired = "CDC_TEMPLATE_SQLSERVER_POLL_INTERVAL_REQUIRED";
    public const string SqlServerPollIntervalExceedsHeartbeatInterval =
        "CDC_TEMPLATE_SQLSERVER_POLL_INTERVAL_EXCEEDS_HEARTBEAT_INTERVAL";
    public const string LiveReadBackProviderSetupMismatch =
        "CDC_TEMPLATE_LIVE_READBACK_PROVIDER_SETUP_MISMATCH";
    public const string LiveReadBackPropertyMissing = "CDC_TEMPLATE_LIVE_READBACK_PROPERTY_MISSING";
    public const string LiveReadBackPropertyMismatch = "CDC_TEMPLATE_LIVE_READBACK_PROPERTY_MISMATCH";
    public const string LiveReadBackUnexpectedProperty = "CDC_TEMPLATE_LIVE_READBACK_UNEXPECTED_PROPERTY";
    public const string LiveReadBackSecretMismatch = "CDC_TEMPLATE_LIVE_READBACK_SECRET_MISMATCH";
    public const string LiveReadBackSourcePartitionMismatch =
        "CDC_TEMPLATE_LIVE_READBACK_SOURCE_PARTITION_MISMATCH";
    public const string ArtifactOutputFailed = "CDC_TEMPLATE_ARTIFACT_OUTPUT_FAILED";
    public const string PinnedImageDockerPrerequisiteFailure =
        "CDC_TEMPLATE_PINNED_IMAGE_DOCKER_PREREQUISITE_FAILURE";
    public const string PinnedImageConnectorConfigValidationFailure =
        "CDC_TEMPLATE_PINNED_IMAGE_CONNECTOR_CONFIG_VALIDATION_FAILURE";
    public const string PinnedImageConnectorRegistrationFailure =
        "CDC_TEMPLATE_PINNED_IMAGE_CONNECTOR_REGISTRATION_FAILURE";
    public const string PinnedImageConnectorStatusFailure =
        "CDC_TEMPLATE_PINNED_IMAGE_CONNECTOR_STATUS_FAILURE";
    public const string PinnedImageRuntimeClassLoadFailure =
        "CDC_TEMPLATE_PINNED_IMAGE_RUNTIME_CLASS_LOAD_FAILURE";
    public const string PinnedImageOffsetProgressFailure =
        "CDC_TEMPLATE_PINNED_IMAGE_OFFSET_PROGRESS_FAILURE";
    public const string PinnedImageReadBackValidationFailure =
        "CDC_TEMPLATE_PINNED_IMAGE_READBACK_VALIDATION_FAILURE";
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
            "driver.encrypt",
            "driver.trustServerCertificate",
            "driver.trustStore",
            "driver.trustStorePassword",
            "driver.trustStoreType",
            "driver.hostNameInCertificate",
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

    private static readonly IReadOnlyDictionary<
        CdcProvider,
        IReadOnlyList<string>
    > _requiredConnectionPropertiesByProvider = new Dictionary<CdcProvider, IReadOnlyList<string>>
    {
        [CdcProvider.Postgresql] =
        [
            "database.hostname",
            "database.user",
            "database.password",
            "database.dbname",
        ],
        [CdcProvider.SqlServer] =
        [
            "database.hostname",
            "database.user",
            "database.password",
            "database.names",
        ],
    };

    private static readonly IReadOnlySet<string> _secretPropertyNames = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "database.password",
        "database.sslpassword",
        "database.sslkey",
        "driver.trustStorePassword",
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
        "data.query.mode",
        "snapshot.isolation.mode",
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

    private static readonly IReadOnlySet<string> _generatedConnectorPropertyNames = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "transforms.documentState.type",
        "transforms.documentState.provider",
        "transforms.documentState.target.topic",
        "transforms.documentState.progress.topic",
        "producer.override.enable.idempotence",
        "producer.override.acks",
        "producer.override.retries",
        "producer.override.max.in.flight.requests.per.connection",
        "producer.override.max.request.size",
        "producer.override.buffer.memory",
        "producer.override.compression.type",
        "producer.override.partitioner.class",
        "schema.history.internal.kafka.bootstrap.servers",
        "schema.history.internal.kafka.topic",
        "schema.history.internal.producer.enable.idempotence",
        "schema.history.internal.producer.acks",
        "schema.history.internal.producer.retries",
        "schema.history.internal.producer.max.in.flight.requests.per.connection",
    };

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

    internal static bool IsKafkaSecurityMaterialRenderedProperty(string propertyName)
    {
        string? suffix = GeneratedKafkaSecurityPropertySuffix(propertyName) ?? propertyName;

        return suffix
            is "ssl.truststore.location"
                or "ssl.truststore.certificates"
                or "ssl.keystore.location"
                or "ssl.keystore.certificate.chain";
    }

    internal static string? GeneratedKafkaSecurityPropertySuffix(string propertyName)
    {
        string prefix =
            _generatedKafkaSecurityPrefixes.FirstOrDefault(prefix =>
                propertyName.StartsWith(prefix, StringComparison.Ordinal)
            ) ?? string.Empty;

        return prefix.Length == 0 ? null : propertyName[prefix.Length..];
    }

    internal static bool IsKafkaClientSecurityProperty(string propertyName) =>
        _kafkaSecurityAllowList.Contains(propertyName);

    internal static bool IsProviderConnectionProperty(CdcProvider provider, string propertyName) =>
        _connectionAllowListByProvider[provider].Contains(propertyName);

    internal static bool IsKnownGeneratedConnectorProperty(string propertyName) =>
        _reservedExactKeys.Contains(propertyName)
        || _generatedConnectorPropertyNames.Contains(propertyName)
        || IsGeneratedKafkaSecurityProperty(propertyName);

    private static bool IsGeneratedKafkaSecurityProperty(string propertyName)
    {
        string? suffix = GeneratedKafkaSecurityPropertySuffix(propertyName);

        return suffix is not null && _kafkaSecurityAllowList.Contains(suffix);
    }

    internal static bool IsSqlServerDriverConnectionProperty(string propertyName) =>
        propertyName.StartsWith("driver.", StringComparison.Ordinal)
        && _connectionAllowListByProvider[CdcProvider.SqlServer].Contains(propertyName);

    public CdcConnectorTemplateValidationResult ValidateRequest(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase = CdcConnectorTemplateSourcePhase.Render
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        List<CdcConnectorTemplateDiagnostic> diagnostics = [];

        ValidateBindingIdentity(request, sourcePhase, diagnostics);
        ValidateProviderConnectionProperties(request, sourcePhase, diagnostics);
        ValidateKafkaSecurityProperties(request, sourcePhase, diagnostics);
        ValidateProviderPrerequisites(request, sourcePhase, diagnostics);

        return new CdcConnectorTemplateValidationResult(diagnostics);
    }

    private static void ValidateBindingIdentity(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        CdcBindingIdentity bindingIdentity = request.BindingIdentity;
        CdcConnectorProviderSetupEvidence providerSetupEvidence = request.ProviderSetupEvidence;
        CdcProviderSetupResult providerSetupResult = providerSetupEvidence.Result;

        if (providerSetupResult.Provider != bindingIdentity.Provider)
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.BindingIdentityMismatch,
                    CdcConnectorTemplateDiagnosticCategory.BindingIdentityFailure,
                    "providerSetup.provider",
                    bindingIdentity.Provider.ToString(),
                    providerSetupResult.Provider.ToString(),
                    request,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.Safe
                )
            );
        }

        if (request.ProviderConnectionProperties.Provider != bindingIdentity.Provider)
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.BindingIdentityMismatch,
                    CdcConnectorTemplateDiagnosticCategory.BindingIdentityFailure,
                    "providerConnection.provider",
                    bindingIdentity.Provider.ToString(),
                    request.ProviderConnectionProperties.Provider.ToString(),
                    request,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.Safe
                )
            );
        }

        if (
            providerSetupResult.Outcome
            is not (CdcProviderSetupOutcome.CreatedOrMatched or CdcProviderSetupOutcome.ExactMatch)
        )
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.ProviderSetupResultNotReady,
                    CdcConnectorTemplateDiagnosticCategory.ProviderSetupResultFailure,
                    "providerSetup.outcome",
                    "CreatedOrMatched or ExactMatch",
                    providerSetupResult.Outcome.ToString(),
                    request,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.Safe
                )
            );
        }

        if (providerSetupEvidence.BindingGeneration != bindingIdentity.BindingGeneration)
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.BindingIdentityMismatch,
                    CdcConnectorTemplateDiagnosticCategory.BindingIdentityFailure,
                    "providerSetup.bindingGeneration",
                    bindingIdentity.BindingGeneration.ToString(),
                    providerSetupEvidence.BindingGeneration.ToString(),
                    request,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.Safe
                )
            );
        }

        if (
            !providerSetupResult.BoundPhysicalSourceFingerprint.Equals(
                bindingIdentity.BoundPhysicalSourceFingerprint
            )
        )
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.BindingIdentityMismatch,
                    CdcConnectorTemplateDiagnosticCategory.BindingIdentityFailure,
                    "providerSetup.boundPhysicalSourceFingerprint",
                    "binding physical-source fingerprint",
                    RedactedValue,
                    request,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        if (
            providerSetupResult.ObservedSourceFingerprint is null
            || !providerSetupResult.ObservedSourceFingerprint.Equals(
                bindingIdentity.BoundPhysicalSourceFingerprint
            )
        )
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.BindingIdentityMismatch,
                    CdcConnectorTemplateDiagnosticCategory.BindingIdentityFailure,
                    "providerSetup.observedPhysicalSourceFingerprint",
                    "binding physical-source fingerprint",
                    providerSetupResult.ObservedSourceFingerprint is null ? "missing" : RedactedValue,
                    request,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }

        if (providerSetupResult.HeartbeatActionQuery is null)
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.HeartbeatActionQueryRequired,
                    CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation,
                    "providerSetup.heartbeatActionQuery",
                    "fresh provider heartbeat action query",
                    "missing",
                    request,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.PhysicalIdentifier
                )
            );
        }
    }

    private static void ValidateProviderPrerequisites(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        diagnostics.AddRange(
            CdcProviderSetupPrerequisiteRules.Validate(
                request.ProviderSetupEvidence.Result,
                request.ConnectorName,
                sourcePhase,
                request.ProviderArtifactNames
            )
        );

        if (request.Provider == CdcProvider.SqlServer)
        {
            AddSqlServerPollIntervalDiagnosticIfNeeded(request, sourcePhase, diagnostics);
        }
    }

    private static void AddSqlServerPollIntervalDiagnosticIfNeeded(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        if (request.DeploymentPolicy.SqlServerPollInterval is null)
        {
            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.SqlServerPollIntervalRequired,
                    CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation,
                    "poll.interval.ms",
                    "positive SQL Server poll interval",
                    null,
                    request,
                    sourcePhase,
                    CdcConnectorTemplateRedactionClassification.Safe
                )
            );
            return;
        }

        long heartbeatMilliseconds = CdcConnectorTemplateSharedRules.HeartbeatIntervalMilliseconds(request);
        long pollMilliseconds = CdcConnectorTemplateSharedRules.PollIntervalMilliseconds(request);
        if (pollMilliseconds <= heartbeatMilliseconds)
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.SqlServerPollIntervalExceedsHeartbeatInterval,
                CdcConnectorTemplateDiagnosticCategory.HeartbeatConfigurationViolation,
                "poll.interval.ms",
                $"<= heartbeat.interval.ms ({heartbeatMilliseconds})",
                pollMilliseconds.ToString(),
                request,
                sourcePhase,
                CdcConnectorTemplateRedactionClassification.Safe
            )
        );
    }

    private static void ValidateProviderConnectionProperties(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        IReadOnlySet<string> allowList = _connectionAllowListByProvider[request.Provider];

        AddMissingRequiredConnectionPropertyDiagnostics(request, sourcePhase, diagnostics);

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
                        CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation,
                        CdcConnectorTemplateDiagnosticPropertyNames.UnexpectedProviderConnectionProperty(
                            property.Key
                        ),
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

    private static void AddMissingRequiredConnectionPropertyDiagnostics(
        CdcConnectorTemplateRequest request,
        CdcConnectorTemplateSourcePhase sourcePhase,
        List<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        foreach (string propertyName in _requiredConnectionPropertiesByProvider[request.Provider])
        {
            if (
                request.Provider == CdcProvider.SqlServer
                && string.Equals(propertyName, "database.names", StringComparison.Ordinal)
            )
            {
                continue;
            }

            if (request.ProviderConnectionProperties.Properties.ContainsKey(propertyName))
            {
                continue;
            }

            diagnostics.Add(
                BuildDiagnostic(
                    CdcConnectorTemplateDiagnosticCodes.ConnectionPropertyRequired,
                    CdcConnectorTemplateDiagnosticCategory.MissingRequiredInput,
                    propertyName,
                    RequiredConnectionPropertyMessage(request.Provider),
                    null,
                    request,
                    sourcePhase,
                    RedactionClassificationForConnectionProperty(propertyName)
                )
            );
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
                        CdcConnectorTemplateDiagnosticCategory.KafkaSecurityPropertyViolation,
                        CdcConnectorTemplateDiagnosticPropertyNames.UnexpectedKafkaSecurityProperty(
                            property.Key
                        ),
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
                    CdcConnectorTemplateDiagnosticCategory.MissingRequiredInput,
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

        string[] databaseNames = value.Split(',', StringSplitOptions.None);
        if (databaseNames.Length == 1 && databaseNames[0].Length > 0 && value == databaseNames[0].Trim())
        {
            return;
        }

        diagnostics.Add(
            BuildDiagnostic(
                CdcConnectorTemplateDiagnosticCodes.SqlServerSingleDatabaseRequired,
                CdcConnectorTemplateDiagnosticCategory.ConnectionPropertyViolation,
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
                CdcConnectorTemplateDiagnosticCategory.ReservedKeyViolation,
                IsKnownGeneratedConnectorProperty(propertyName)
                    ? propertyName
                    : CdcConnectorTemplateDiagnosticPropertyNames.ReservedConnectorProperty(propertyName),
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
                CdcConnectorTemplateDiagnosticCategory.SecretRedactionViolation,
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

    private static string RequiredConnectionPropertyMessage(CdcProvider provider) =>
        provider switch
        {
            CdcProvider.Postgresql => "required PostgreSQL connection property",
            CdcProvider.SqlServer => "required SQL Server connection property",
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static CdcConnectorTemplateRedactionClassification RedactionClassificationForConnectionProperty(
        string propertyName
    )
    {
        if (_secretPropertyNames.Contains(propertyName))
        {
            return CdcConnectorTemplateRedactionClassification.SecretValue;
        }

        if (
            propertyName.StartsWith("database.", StringComparison.Ordinal)
            || propertyName.StartsWith("driver.", StringComparison.Ordinal)
        )
        {
            return CdcConnectorTemplateRedactionClassification.PhysicalIdentifier;
        }

        return CdcConnectorTemplateRedactionClassification.Safe;
    }

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
            && !property.Any(char.IsControl)
            && !fileReference.Any(character => character is '{' or '}');
    }

    private static bool IsEnvironmentVariableStart(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsEnvironmentVariablePart(char character) =>
        IsEnvironmentVariableStart(character) || character is >= '0' and <= '9';
}
