// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;

namespace EdFi.DataManagementService.Backend.Cdc;

public enum CdcConnectorTemplateOutcome
{
    Rendered,
    ValidationFailed,
}

public enum CdcConnectorTemplateDiagnosticCategory
{
    BindingIdentity,
    ProviderSetupResult,
    MissingInput,
    ReservedKey,
    ConnectionProperty,
    KafkaSecurityProperty,
    ProducerPolicy,
    Heartbeat,
    TopicNaming,
    Transform,
    Converter,
    IncludeList,
    MessageKey,
    SchemaHistory,
    LiveReadBack,
    SecretRedactionFailure,
}

public enum CdcConnectorTemplateDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public enum CdcConnectorTemplateSourcePhase
{
    RequestValidation,
    Rendering,
    RegistrationPreflight,
    LiveReadBack,
    ArtifactOutput,
}

public enum CdcConnectorTemplateRedactionClassification
{
    None,
    Safe,
    ExternalizedSecretReference,
    MaskedSecret,
    SecretValue,
    ConnectionString,
    DocumentPayload,
    TenantDisplayName,
    PhysicalIdentifier,
}

public sealed record CdcConnectorTemplateBindingIdentity
{
    public CdcConnectorTemplateBindingIdentity(
        CdcProvider provider,
        CdcSafeName connectorName,
        string publicTopicName,
        long bindingGeneration,
        CdcSourceFingerprint boundPhysicalSourceFingerprint
    )
    {
        ArgumentNullException.ThrowIfNull(boundPhysicalSourceFingerprint);

        Provider = provider;
        ConnectorName = connectorName;
        PublicTopicName = CdcConnectorTemplateContractValidation.ValidateRequiredSafeText(
            publicTopicName,
            nameof(publicTopicName)
        );
        BindingGeneration =
            bindingGeneration >= 0
                ? bindingGeneration
                : throw new ArgumentOutOfRangeException(
                    nameof(bindingGeneration),
                    bindingGeneration,
                    "CDC binding generation must be zero or greater."
                );
        BoundPhysicalSourceFingerprint = CdcConnectorTemplateContractValidation.ValidateSourceFingerprint(
            boundPhysicalSourceFingerprint,
            nameof(boundPhysicalSourceFingerprint)
        );
    }

    public CdcProvider Provider { get; }

    public CdcSafeName ConnectorName { get; }

    public string PublicTopicName { get; }

    public long BindingGeneration { get; }

    public CdcSourceFingerprint BoundPhysicalSourceFingerprint { get; }

    public string ProgressTopicName => CdcConnectorTemplateTopicNames.ProgressTopicName(PublicTopicName);

    public string? SchemaHistoryTopicName =>
        Provider == CdcProvider.SqlServer
            ? CdcConnectorTemplateTopicNames.SqlServerSchemaHistoryTopicName(PublicTopicName)
            : null;
}

public sealed record CdcConnectorProviderSetupEvidence
{
    public CdcConnectorProviderSetupEvidence(long bindingGeneration, CdcProviderSetupResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        BindingGeneration =
            bindingGeneration >= 0
                ? bindingGeneration
                : throw new ArgumentOutOfRangeException(
                    nameof(bindingGeneration),
                    bindingGeneration,
                    "CDC provider setup binding generation must be zero or greater."
                );
        Result = result;
    }

    public long BindingGeneration { get; }

    public CdcProviderSetupResult Result { get; }
}

public sealed record CdcConnectorTemplateDeploymentPolicy
{
    public CdcConnectorTemplateDeploymentPolicy(
        string kafkaBootstrapServers,
        int maxRecordBytes,
        int? producerBufferBytes = null,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? sqlServerPollInterval = null
    )
    {
        KafkaBootstrapServers = CdcConnectorTemplateContractValidation.ValidateRequiredSafeText(
            kafkaBootstrapServers,
            nameof(kafkaBootstrapServers)
        );
        MaxRecordBytes = ValidatePositive(maxRecordBytes, nameof(maxRecordBytes));
        ProducerBufferBytes = producerBufferBytes.HasValue
            ? ValidatePositive(producerBufferBytes.Value, nameof(producerBufferBytes))
            : null;
        HeartbeatInterval = heartbeatInterval.HasValue
            ? ValidatePositive(heartbeatInterval.Value, nameof(heartbeatInterval))
            : null;
        SqlServerPollInterval = sqlServerPollInterval.HasValue
            ? ValidatePositive(sqlServerPollInterval.Value, nameof(sqlServerPollInterval))
            : null;
    }

    public string KafkaBootstrapServers { get; }

    public int MaxRecordBytes { get; }

    public int? ProducerBufferBytes { get; }

    public TimeSpan? HeartbeatInterval { get; }

    public TimeSpan? SqlServerPollInterval { get; }

    private static int ValidatePositive(int value, string parameterName) =>
        value > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "CDC connector template policy values must be positive."
            );

    private static TimeSpan ValidatePositive(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "CDC connector template policy intervals must be positive."
            );
}

public sealed record CdcProviderConnectionProperties
{
    public CdcProviderConnectionProperties(
        CdcProvider provider,
        IReadOnlyDictionary<string, string> properties
    )
    {
        Provider = provider;
        Properties = CdcConnectorTemplateContractValidation.NormalizeStringProperties(
            properties,
            nameof(properties)
        );
    }

    public CdcProvider Provider { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }

    public static CdcProviderConnectionProperties Empty(CdcProvider provider) =>
        new(provider, new Dictionary<string, string>());
}

public sealed record CdcKafkaClientSecurityProperties
{
    public CdcKafkaClientSecurityProperties(IReadOnlyDictionary<string, string> properties)
    {
        Properties = CdcConnectorTemplateContractValidation.NormalizeStringProperties(
            properties,
            nameof(properties)
        );
    }

    public IReadOnlyDictionary<string, string> Properties { get; }

    public static CdcKafkaClientSecurityProperties Empty { get; } = new(new Dictionary<string, string>());
}

public sealed record CdcConnectorTemplateArtifactOutputRequest
{
    public CdcConnectorTemplateArtifactOutputRequest(
        bool includeRedactedArtifactPayload,
        string? manifestOutputDirectoryPath = null
    )
    {
        if (manifestOutputDirectoryPath is not null && string.IsNullOrWhiteSpace(manifestOutputDirectoryPath))
        {
            throw new ArgumentException(
                "CDC connector template manifest output directory must not be empty when supplied.",
                nameof(manifestOutputDirectoryPath)
            );
        }

        IncludeRedactedArtifactPayload =
            includeRedactedArtifactPayload || manifestOutputDirectoryPath is not null;
        ManifestOutputDirectoryPath = manifestOutputDirectoryPath;
    }

    public bool IncludeRedactedArtifactPayload { get; }

    public string? ManifestOutputDirectoryPath { get; }
}

public sealed record CdcConnectorTemplateRequest
{
    public CdcConnectorTemplateRequest(
        CdcConnectorTemplateBindingIdentity bindingIdentity,
        CdcConnectorProviderSetupEvidence providerSetupEvidence,
        CdcConnectorTemplateDeploymentPolicy deploymentPolicy,
        CdcProviderConnectionProperties providerConnectionProperties,
        CdcKafkaClientSecurityProperties kafkaClientSecurityProperties,
        CdcConnectorTemplateArtifactOutputRequest? artifactOutput = null
    )
    {
        ArgumentNullException.ThrowIfNull(bindingIdentity);
        ArgumentNullException.ThrowIfNull(providerSetupEvidence);
        ArgumentNullException.ThrowIfNull(deploymentPolicy);
        ArgumentNullException.ThrowIfNull(providerConnectionProperties);
        ArgumentNullException.ThrowIfNull(kafkaClientSecurityProperties);

        ValidateProviderSetupEvidence(bindingIdentity, providerSetupEvidence);
        if (providerConnectionProperties.Provider != bindingIdentity.Provider)
        {
            throw new ArgumentException(
                "CDC connector template provider connection properties must match the binding provider.",
                nameof(providerConnectionProperties)
            );
        }

        BindingIdentity = bindingIdentity;
        ProviderSetupEvidence = providerSetupEvidence;
        DeploymentPolicy = deploymentPolicy;
        ProviderConnectionProperties = providerConnectionProperties;
        KafkaClientSecurityProperties = kafkaClientSecurityProperties;
        ArtifactOutput = artifactOutput;
    }

    public CdcConnectorTemplateBindingIdentity BindingIdentity { get; }

    public CdcConnectorProviderSetupEvidence ProviderSetupEvidence { get; }

    public CdcConnectorTemplateDeploymentPolicy DeploymentPolicy { get; }

    public CdcProviderConnectionProperties ProviderConnectionProperties { get; }

    public CdcKafkaClientSecurityProperties KafkaClientSecurityProperties { get; }

    public CdcConnectorTemplateArtifactOutputRequest? ArtifactOutput { get; }

    public CdcProvider Provider => BindingIdentity.Provider;

    public CdcSafeName ConnectorName => BindingIdentity.ConnectorName;

    public string PublicTopicName => BindingIdentity.PublicTopicName;

    public string ProgressTopicName => BindingIdentity.ProgressTopicName;

    public string? SchemaHistoryTopicName => BindingIdentity.SchemaHistoryTopicName;

    private static void ValidateProviderSetupEvidence(
        CdcConnectorTemplateBindingIdentity bindingIdentity,
        CdcConnectorProviderSetupEvidence providerSetupEvidence
    )
    {
        CdcProviderSetupResult result = providerSetupEvidence.Result;

        if (result.Provider != bindingIdentity.Provider)
        {
            throw new ArgumentException(
                "CDC provider setup result must match the binding provider.",
                nameof(providerSetupEvidence)
            );
        }

        if (
            result.Outcome
            is not (CdcProviderSetupOutcome.CreatedOrMatched or CdcProviderSetupOutcome.ExactMatch)
        )
        {
            throw new ArgumentException(
                "CDC connector templates require a successful provider setup result.",
                nameof(providerSetupEvidence)
            );
        }

        if (providerSetupEvidence.BindingGeneration != bindingIdentity.BindingGeneration)
        {
            throw new ArgumentException(
                "CDC provider setup evidence must be for the same binding generation.",
                nameof(providerSetupEvidence)
            );
        }

        if (!result.BoundPhysicalSourceFingerprint.Equals(bindingIdentity.BoundPhysicalSourceFingerprint))
        {
            throw new ArgumentException(
                "CDC provider setup result must be for the same bound physical source fingerprint.",
                nameof(providerSetupEvidence)
            );
        }

        if (
            result.ObservedSourceFingerprint is null
            || !result.ObservedSourceFingerprint.Equals(bindingIdentity.BoundPhysicalSourceFingerprint)
        )
        {
            throw new ArgumentException(
                "CDC provider setup result must include the matching observed physical source fingerprint.",
                nameof(providerSetupEvidence)
            );
        }

        ValidateRequiredSourceInventory(result.SourceTableInventory, nameof(providerSetupEvidence));
        ValidateExpectedMessageKeyColumns(result.ExpectedMessageKeyColumns, nameof(providerSetupEvidence));

        if (result.HeartbeatActionQuery is null)
        {
            throw new ArgumentException(
                "CDC provider setup result must include the heartbeat action query.",
                nameof(providerSetupEvidence)
            );
        }
    }

    private static void ValidateRequiredSourceInventory(
        IReadOnlyList<CdcSourceTableInventory> sourceTableInventory,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(sourceTableInventory);

        var requiredKinds = new[]
        {
            CdcSourceTableKind.DocumentCache,
            CdcSourceTableKind.Document,
            CdcSourceTableKind.CdcHeartbeat,
        };
        var observedKinds = sourceTableInventory.Select(table => table.TableKind).ToArray();

        if (
            sourceTableInventory.Count != requiredKinds.Length
            || requiredKinds.Except(observedKinds).Any()
            || observedKinds.Except(requiredKinds).Any()
            || observedKinds.GroupBy(kind => kind).Any(group => group.Count() > 1)
        )
        {
            throw new ArgumentException(
                "CDC provider setup result source inventory must contain exactly dms.DocumentCache, dms.Document, and dms.CdcHeartbeat.",
                parameterName
            );
        }
    }

    private static void ValidateExpectedMessageKeyColumns(
        IReadOnlyList<CdcExpectedMessageKeyColumns> expectedMessageKeyColumns,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(expectedMessageKeyColumns);

        var observedKinds = expectedMessageKeyColumns.Select(columns => columns.TableKind).ToArray();
        var requiredKinds = new[] { CdcSourceTableKind.DocumentCache, CdcSourceTableKind.Document };

        if (
            expectedMessageKeyColumns.Count != requiredKinds.Length
            || requiredKinds.Except(observedKinds).Any()
            || observedKinds.Except(requiredKinds).Any()
            || observedKinds.GroupBy(kind => kind).Any(group => group.Count() > 1)
        )
        {
            throw new ArgumentException(
                "CDC provider setup result message-key inventory must contain only dms.DocumentCache and dms.Document.",
                parameterName
            );
        }

        bool hasInvalidKeyColumns = expectedMessageKeyColumns
            .Select(messageKeyColumns => messageKeyColumns.KeyColumns)
            .Any(keyColumns =>
                keyColumns.Count != 1
                || !string.Equals(keyColumns[0].Value, "DocumentUuid", StringComparison.Ordinal)
            );

        if (hasInvalidKeyColumns)
        {
            throw new ArgumentException(
                "CDC provider setup result message-key inventory must use DocumentUuid as the only document key column.",
                parameterName
            );
        }
    }
}

public sealed record CdcConnectorTemplateSourcePartitionEvidence
{
    public CdcConnectorTemplateSourcePartitionEvidence(IReadOnlyDictionary<string, string> properties)
    {
        Properties = CdcConnectorTemplateContractValidation.NormalizeStringPropertiesAllowingEmptyValues(
            properties,
            nameof(properties)
        );
    }

    public IReadOnlyDictionary<string, string> Properties { get; }
}

public sealed record CdcConnectorTemplateEffectiveConfigValidationRequest
{
    public CdcConnectorTemplateEffectiveConfigValidationRequest(
        CdcConnectorTemplateRequest templateRequest,
        IReadOnlyDictionary<string, string> effectiveConfig,
        CdcConnectorProviderSetupEvidence providerSetupEvidence,
        CdcConnectorTemplateSourcePartitionEvidence? sourcePartitionEvidence = null
    )
    {
        ArgumentNullException.ThrowIfNull(templateRequest);
        ArgumentNullException.ThrowIfNull(providerSetupEvidence);

        TemplateRequest = templateRequest;
        EffectiveConfig = CdcConnectorTemplateContractValidation.NormalizeStringPropertiesAllowingEmptyValues(
            effectiveConfig,
            nameof(effectiveConfig)
        );
        ProviderSetupEvidence = providerSetupEvidence;
        SourcePartitionEvidence = sourcePartitionEvidence;
    }

    public CdcConnectorTemplateRequest TemplateRequest { get; }

    public IReadOnlyDictionary<string, string> EffectiveConfig { get; }

    public CdcConnectorProviderSetupEvidence ProviderSetupEvidence { get; }

    public CdcConnectorTemplateSourcePartitionEvidence? SourcePartitionEvidence { get; }
}

public sealed record CdcKafkaConnectRegistrationPayload
{
    public CdcKafkaConnectRegistrationPayload(CdcSafeName name, IReadOnlyDictionary<string, string> config)
    {
        Name = name;
        Config = CdcConnectorTemplateContractValidation.NormalizeStringProperties(config, nameof(config));
    }

    public CdcSafeName Name { get; }

    public IReadOnlyDictionary<string, string> Config { get; }
}

public sealed record CdcConnectorTemplateArtifactPayload
{
    public CdcConnectorTemplateArtifactPayload(CdcSafeName fileName, string json)
    {
        FileName = fileName;
        Json = string.IsNullOrWhiteSpace(json)
            ? throw new ArgumentException(
                "CDC connector template artifact payload JSON must be supplied.",
                nameof(json)
            )
            : json;
    }

    public CdcSafeName FileName { get; }

    public string Json { get; }
}

public sealed record CdcConnectorTemplateResult
{
    public CdcConnectorTemplateResult(
        CdcConnectorTemplateBindingIdentity bindingIdentity,
        CdcConnectorTemplateOutcome outcome,
        IReadOnlyDictionary<string, string> config,
        CdcKafkaConnectRegistrationPayload? registrationPayload,
        CdcConnectorTemplateArtifactPayload? redactedArtifactPayload,
        string? configSha256,
        IReadOnlyList<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(bindingIdentity);
        ArgumentNullException.ThrowIfNull(diagnostics);

        Config = CdcConnectorTemplateContractValidation.NormalizeStringProperties(config, nameof(config));
        ValidateRegistrationPayload(bindingIdentity, Config, registrationPayload);

        Provider = bindingIdentity.Provider;
        ConnectorName = bindingIdentity.ConnectorName;
        PublicTopicName = bindingIdentity.PublicTopicName;
        ProgressTopicName = bindingIdentity.ProgressTopicName;
        SchemaHistoryTopicName = bindingIdentity.SchemaHistoryTopicName;
        Outcome = outcome;
        RegistrationPayload = registrationPayload;
        RedactedArtifactPayload = redactedArtifactPayload;
        ConfigSha256 = ValidateConfigHash(configSha256, nameof(configSha256));
        Diagnostics = diagnostics;
    }

    public CdcProvider Provider { get; }

    public CdcSafeName ConnectorName { get; }

    public string PublicTopicName { get; }

    public string ProgressTopicName { get; }

    public string? SchemaHistoryTopicName { get; }

    public CdcConnectorTemplateOutcome Outcome { get; }

    public IReadOnlyDictionary<string, string> Config { get; }

    public CdcKafkaConnectRegistrationPayload? RegistrationPayload { get; }

    public CdcConnectorTemplateArtifactPayload? RedactedArtifactPayload { get; }

    public string? ConfigSha256 { get; }

    public IReadOnlyList<CdcConnectorTemplateDiagnostic> Diagnostics { get; }

    private static void ValidateRegistrationPayload(
        CdcConnectorTemplateBindingIdentity bindingIdentity,
        IReadOnlyDictionary<string, string> config,
        CdcKafkaConnectRegistrationPayload? registrationPayload
    )
    {
        if (registrationPayload is null)
        {
            return;
        }

        if (!registrationPayload.Name.Equals(bindingIdentity.ConnectorName))
        {
            throw new ArgumentException(
                "CDC connector template registration payload name must match the binding connector name.",
                nameof(registrationPayload)
            );
        }

        if (
            registrationPayload.Config.Count != config.Count
            || registrationPayload.Config.Any(pair =>
                !config.TryGetValue(pair.Key, out string? value)
                || !string.Equals(value, pair.Value, StringComparison.Ordinal)
            )
        )
        {
            throw new ArgumentException(
                "CDC connector template registration payload config must match the result config.",
                nameof(registrationPayload)
            );
        }
    }

    private static string? ValidateConfigHash(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        const string prefix = "sha256:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "CDC connector template config hash must use sha256 prefix.",
                parameterName
            );
        }

        string hash = value[prefix.Length..];
        if (hash.Length != 64 || hash.Any(character => !IsLowerHex(character)))
        {
            throw new ArgumentException(
                "CDC connector template config hash must contain a lowercase SHA-256 value.",
                parameterName
            );
        }

        return value;
    }

    private static bool IsLowerHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';
}

public sealed record CdcConnectorTemplateDiagnostic
{
    public CdcConnectorTemplateDiagnostic(
        string code,
        CdcConnectorTemplateDiagnosticCategory category,
        CdcConnectorTemplateDiagnosticSeverity severity,
        string? propertyName,
        CdcSafeName? safeArtifactOrObjectName,
        string? expectedValue,
        string? observedValue,
        CdcProvider provider,
        CdcConnectorTemplateSourcePhase sourcePhase,
        CdcConnectorTemplateRedactionClassification redactionClassification
    )
    {
        Code = CdcConnectorTemplateContractValidation.ValidateRequiredSafeText(code, nameof(code));
        Category = category;
        Severity = severity;
        PropertyName = CdcConnectorTemplateContractValidation.ValidateOptionalSafeText(
            propertyName,
            nameof(propertyName)
        );
        SafeArtifactOrObjectName = safeArtifactOrObjectName;
        ExpectedValue = expectedValue;
        ObservedValue = observedValue;
        Provider = provider;
        SourcePhase = sourcePhase;
        RedactionClassification = redactionClassification;
    }

    public string Code { get; }

    public CdcConnectorTemplateDiagnosticCategory Category { get; }

    public CdcConnectorTemplateDiagnosticSeverity Severity { get; }

    public string? PropertyName { get; }

    public CdcSafeName? SafeArtifactOrObjectName { get; }

    public string? ExpectedValue { get; }

    public string? ObservedValue { get; }

    public CdcProvider Provider { get; }

    public CdcConnectorTemplateSourcePhase SourcePhase { get; }

    public CdcConnectorTemplateRedactionClassification RedactionClassification { get; }
}

public static class CdcConnectorTemplateTopicNames
{
    public static string ProgressTopicName(string publicTopicName) =>
        $"{CdcConnectorTemplateContractValidation.ValidateRequiredSafeText(publicTopicName, nameof(publicTopicName))}.cdc-progress";

    public static string SqlServerSchemaHistoryTopicName(string publicTopicName) =>
        $"{CdcConnectorTemplateContractValidation.ValidateRequiredSafeText(publicTopicName, nameof(publicTopicName))}.schema-history";
}

internal static class CdcConnectorTemplateContractValidation
{
    public static CdcSourceFingerprint ValidateSourceFingerprint(
        CdcSourceFingerprint sourceFingerprint,
        string parameterName
    )
    {
        ValidateRequiredSafeText(
            sourceFingerprint.Version,
            $"{parameterName}.{nameof(sourceFingerprint.Version)}"
        );
        ValidateRequiredSafeText(
            sourceFingerprint.Value,
            $"{parameterName}.{nameof(sourceFingerprint.Value)}"
        );

        return sourceFingerprint;
    }

    public static IReadOnlyDictionary<string, string> NormalizeStringProperties(
        IReadOnlyDictionary<string, string> properties,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(properties);

        return properties
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                pair => ValidateRequiredSafeText(pair.Key, $"{parameterName}.Key"),
                pair => ValidateRequiredSafeText(pair.Value, $"{parameterName}[{pair.Key}]"),
                StringComparer.Ordinal
            );
    }

    public static IReadOnlyDictionary<string, string> NormalizeStringPropertiesAllowingEmptyValues(
        IReadOnlyDictionary<string, string> properties,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(properties);

        return properties
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                pair => ValidateRequiredSafeText(pair.Key, $"{parameterName}.Key"),
                pair => ValidateSafeTextAllowingEmptyValues(pair.Value, $"{parameterName}[{pair.Key}]"),
                StringComparer.Ordinal
            );
    }

    public static string ValidateRequiredSafeText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "CDC connector template text values must be supplied.",
                parameterName
            );
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "CDC connector template text values must not contain control characters.",
                parameterName
            );
        }

        return value;
    }

    private static string ValidateSafeTextAllowingEmptyValues(string? value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "CDC connector template text values must not contain control characters.",
                parameterName
            );
        }

        return value;
    }

    public static string? ValidateOptionalSafeText(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        return ValidateRequiredSafeText(value, parameterName);
    }
}
