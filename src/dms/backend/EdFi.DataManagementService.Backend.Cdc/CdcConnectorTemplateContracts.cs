// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using EdFi.DataManagementService.Backend.Ddl;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

public enum CdcConnectorTemplateOutcome
{
    Rendered,
    ValidationFailed,
}

public enum CdcConnectorTemplateDiagnosticCategory
{
    BindingIdentityFailure,
    ProviderSetupResultFailure,
    MissingRequiredInput,
    ReservedKeyViolation,
    ConnectionPropertyViolation,
    KafkaSecurityPropertyViolation,
    ProducerPolicyViolation,
    HeartbeatConfigurationViolation,
    TopicNamingConfigurationViolation,
    TransformConfigurationViolation,
    ConverterConfigurationViolation,
    IncludeListViolation,
    MessageKeyViolation,
    SchemaHistoryConfigurationViolation,
    LiveReadBackMismatch,
    SecretRedactionViolation,
    ArtifactOutputFailure,
}

public enum CdcConnectorTemplateDiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

public enum CdcConnectorTemplateSourcePhase
{
    Render,
    Preflight,
    LiveReadBack,
    PinnedImageSmoke,
}

public enum CdcConnectorTemplateRedactionClassification
{
    Safe,
    SecretValue,
    PhysicalIdentifier,
}

public sealed record CdcConnectorProviderSetupEvidence
{
    public CdcConnectorProviderSetupEvidence(long bindingGeneration, CdcProviderSetupResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        CdcConnectorTemplateContractValidation.ValidateSourceFingerprint(
            result.BoundPhysicalSourceFingerprint,
            $"{nameof(result)}.{nameof(result.BoundPhysicalSourceFingerprint)}"
        );
        if (result.ObservedSourceFingerprint is not null)
        {
            CdcConnectorTemplateContractValidation.ValidateSourceFingerprint(
                result.ObservedSourceFingerprint,
                $"{nameof(result)}.{nameof(result.ObservedSourceFingerprint)}"
            );
        }

        BindingGeneration =
            bindingGeneration > 0
                ? bindingGeneration
                : throw new ArgumentOutOfRangeException(
                    nameof(bindingGeneration),
                    bindingGeneration,
                    "CDC provider setup binding generation must be a positive integer."
                );
        Result = result;
    }

    public long BindingGeneration { get; }

    public CdcProviderSetupResult Result { get; }
}

public sealed record CdcConnectorTemplateDeploymentPolicy
{
    public const int MinimumProducerBufferBytes = 33_554_432;

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
        int validatedMaxRecordBytes = ValidatePositive(maxRecordBytes, nameof(maxRecordBytes));
        int? validatedProducerBufferBytes = producerBufferBytes.HasValue
            ? ValidatePositive(producerBufferBytes.Value, nameof(producerBufferBytes))
            : null;
        int minimumProducerBufferBytes = Math.Max(MinimumProducerBufferBytes, validatedMaxRecordBytes);

        if (
            validatedProducerBufferBytes.HasValue
            && validatedProducerBufferBytes.Value < minimumProducerBufferBytes
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(producerBufferBytes),
                validatedProducerBufferBytes.Value,
                "CDC connector template producerBufferBytes must be greater than or equal to max(33554432, maxRecordBytes)."
            );
        }

        MaxRecordBytes = validatedMaxRecordBytes;
        ProducerBufferBytes = validatedProducerBufferBytes;
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
        Properties = CdcConnectorTemplateContractValidation.NormalizeKafkaClientSecurityProperties(
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
        CoreCdc.CdcBinding binding,
        CdcConnectorProviderSetupEvidence providerSetupEvidence,
        CdcConnectorTemplateDeploymentPolicy deploymentPolicy,
        CdcProviderConnectionProperties providerConnectionProperties,
        CdcKafkaClientSecurityProperties kafkaClientSecurityProperties,
        CdcConnectorTemplateArtifactOutputRequest? artifactOutput = null
    )
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(providerSetupEvidence);
        ArgumentNullException.ThrowIfNull(deploymentPolicy);
        ArgumentNullException.ThrowIfNull(providerConnectionProperties);
        ArgumentNullException.ThrowIfNull(kafkaClientSecurityProperties);

        BindingArtifacts = CdcConnectorTemplateBindingArtifacts.From(binding, nameof(binding));
        Binding = binding;
        ArtifactInventory = BindingArtifacts.ArtifactInventory;
        ProviderSetupEvidence = providerSetupEvidence;
        DeploymentPolicy = deploymentPolicy;
        ProviderConnectionProperties = providerConnectionProperties;
        KafkaClientSecurityProperties = kafkaClientSecurityProperties;
        ArtifactOutput = artifactOutput;
    }

    public CoreCdc.CdcBinding Binding { get; }

    public CoreCdc.CdcArtifactInventory ArtifactInventory { get; }

    public CdcConnectorProviderSetupEvidence ProviderSetupEvidence { get; }

    public CdcConnectorTemplateDeploymentPolicy DeploymentPolicy { get; }

    public CdcProviderConnectionProperties ProviderConnectionProperties { get; }

    public CdcKafkaClientSecurityProperties KafkaClientSecurityProperties { get; }

    public CdcConnectorTemplateArtifactOutputRequest? ArtifactOutput { get; }

    internal CdcConnectorTemplateBindingArtifacts BindingArtifacts { get; }

    public CdcProvider Provider => BindingArtifacts.Provider;

    public CdcSafeName ConnectorName => BindingArtifacts.ConnectorName;

    public string PublicTopicName => ArtifactInventory.TopicName;

    public string ProgressTopicName => ArtifactInventory.ProgressTopicName;

    public string? SchemaHistoryTopicName => ArtifactInventory.SchemaHistoryTopicName;

    public long BindingGeneration => Binding.Generation;

    public int PartitionCount => Binding.PartitionCount;

    public string PartitionerAlgorithm => Binding.PartitionerAlgorithm;

    public CdcSourceFingerprint BoundPhysicalSourceFingerprint =>
        BindingArtifacts.BoundPhysicalSourceFingerprint;

    public CdcProviderArtifactNames ProviderArtifactNames => BindingArtifacts.ProviderArtifactNames;
}

internal sealed record CdcConnectorTemplateBindingArtifacts
{
    private CdcConnectorTemplateBindingArtifacts(
        CdcProvider provider,
        CdcSafeName connectorName,
        CoreCdc.CdcArtifactInventory artifactInventory,
        CdcSourceFingerprint boundPhysicalSourceFingerprint,
        CdcProviderArtifactNames providerArtifactNames
    )
    {
        Provider = provider;
        ConnectorName = connectorName;
        ArtifactInventory = artifactInventory;
        BoundPhysicalSourceFingerprint = boundPhysicalSourceFingerprint;
        ProviderArtifactNames = providerArtifactNames;
    }

    public CdcProvider Provider { get; }

    public CdcSafeName ConnectorName { get; }

    public CoreCdc.CdcArtifactInventory ArtifactInventory { get; }

    public CdcSourceFingerprint BoundPhysicalSourceFingerprint { get; }

    public CdcProviderArtifactNames ProviderArtifactNames { get; }

    public static CdcConnectorTemplateBindingArtifacts From(CoreCdc.CdcBinding binding, string parameterName)
    {
        CoreCdc.CdcContractValidationResult bindingValidationResult = CoreCdc.CdcBindingValidator.Validate(
            binding
        );
        if (!bindingValidationResult.Succeeded)
        {
            throw new ArgumentException(
                BuildValidationMessage(bindingValidationResult.Diagnostics),
                parameterName
            );
        }

        CoreCdc.CdcArtifactNameResult artifactNameResult =
            CoreCdc.CdcArtifactNameGenerator.RecoverFromBinding(binding);
        if (!artifactNameResult.Succeeded || artifactNameResult.Inventory is null)
        {
            throw new ArgumentException(
                BuildValidationMessage(artifactNameResult.Diagnostics),
                parameterName
            );
        }

        CdcProvider provider = ToDdlProvider(binding.Provider);
        return new(
            provider,
            new CdcSafeName(artifactNameResult.Inventory.ConnectorName),
            artifactNameResult.Inventory,
            CdcConnectorTemplateContractValidation.ValidateSourceFingerprint(
                new CdcSourceFingerprint(
                    CdcSourceFingerprintMetadata.Version,
                    binding.PhysicalSourceFingerprint
                ),
                $"{parameterName}.{nameof(binding.PhysicalSourceFingerprint)}"
            ),
            ProviderArtifactNamesFrom(provider, artifactNameResult.Inventory)
        );
    }

    private static string BuildValidationMessage(IReadOnlyList<CoreCdc.CdcDiagnostic> diagnostics)
    {
        string details = string.Join(
            "; ",
            diagnostics.Select(diagnostic => $"{diagnostic.Path}: {diagnostic.Message}")
        );

        return string.IsNullOrWhiteSpace(details)
            ? "CDC connector template binding must be a valid Core CDC binding."
            : $"CDC connector template binding must be a valid Core CDC binding. {details}";
    }

    private static CdcProvider ToDdlProvider(CoreCdc.CdcProvider provider) =>
        provider switch
        {
            CoreCdc.CdcProvider.Postgresql => CdcProvider.Postgresql,
            CoreCdc.CdcProvider.SqlServer => CdcProvider.SqlServer,
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static CdcProviderArtifactNames ProviderArtifactNamesFrom(
        CdcProvider provider,
        CoreCdc.CdcArtifactInventory inventory
    ) =>
        provider switch
        {
            CdcProvider.Postgresql => CdcProviderArtifactNames.ForPostgresql(
                RequiredSafeName(
                    inventory.PostgresqlPublicationName,
                    nameof(inventory.PostgresqlPublicationName)
                ),
                RequiredSafeName(
                    inventory.PostgresqlLogicalSlotName,
                    nameof(inventory.PostgresqlLogicalSlotName)
                )
            ),
            CdcProvider.SqlServer => CdcProviderArtifactNames.ForSqlServer(
                RequiredSafeName(
                    inventory.SqlServerCdcGatingRoleName,
                    nameof(inventory.SqlServerCdcGatingRoleName)
                ),
                new Dictionary<CdcSourceTableKind, CdcSafeName>
                {
                    [CdcSourceTableKind.Document] = RequiredSafeName(
                        inventory.SqlServerCaptureInstanceDocumentName,
                        nameof(inventory.SqlServerCaptureInstanceDocumentName)
                    ),
                    [CdcSourceTableKind.DocumentCache] = RequiredSafeName(
                        inventory.SqlServerCaptureInstanceDocumentCacheName,
                        nameof(inventory.SqlServerCaptureInstanceDocumentCacheName)
                    ),
                    [CdcSourceTableKind.CdcHeartbeat] = RequiredSafeName(
                        inventory.SqlServerCaptureInstanceCdcHeartbeatName,
                        nameof(inventory.SqlServerCaptureInstanceCdcHeartbeatName)
                    ),
                }
            ),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

    private static CdcSafeName RequiredSafeName(string? value, string fieldName) =>
        value is null
            ? throw new InvalidOperationException(
                $"Core CDC artifact inventory did not include required {fieldName}."
            )
            : new CdcSafeName(value);
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
        EffectiveConfig =
            CdcConnectorTemplateContractValidation.NormalizeConnectorStringPropertiesAllowingEmptyValues(
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
        Name = name.Value;
        Config = CdcConnectorTemplateContractValidation.NormalizeConnectorStringProperties(
            config,
            nameof(config)
        );
    }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("config")]
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
        CoreCdc.CdcBinding binding,
        CdcConnectorTemplateOutcome outcome,
        IReadOnlyDictionary<string, string> config,
        CdcKafkaConnectRegistrationPayload? registrationPayload,
        CdcConnectorTemplateArtifactPayload? redactedArtifactPayload,
        string? configSha256,
        IReadOnlyList<CdcConnectorTemplateDiagnostic> diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(diagnostics);

        CdcConnectorTemplateBindingArtifacts bindingArtifacts = CdcConnectorTemplateBindingArtifacts.From(
            binding,
            nameof(binding)
        );
        Config = CdcConnectorTemplateContractValidation.NormalizeConnectorStringProperties(
            config,
            nameof(config)
        );
        ValidateRegistrationPayload(bindingArtifacts, Config, registrationPayload);

        Binding = binding;
        ArtifactInventory = bindingArtifacts.ArtifactInventory;
        Provider = bindingArtifacts.Provider;
        ConnectorName = bindingArtifacts.ConnectorName;
        PublicTopicName = bindingArtifacts.ArtifactInventory.TopicName;
        ProgressTopicName = bindingArtifacts.ArtifactInventory.ProgressTopicName;
        SchemaHistoryTopicName = bindingArtifacts.ArtifactInventory.SchemaHistoryTopicName;
        Outcome = outcome;
        RegistrationPayload = registrationPayload;
        RedactedArtifactPayload = redactedArtifactPayload;
        ConfigSha256 = ValidateConfigHash(configSha256, nameof(configSha256));
        Diagnostics = diagnostics.ToArray();
    }

    public CoreCdc.CdcBinding Binding { get; }

    public CoreCdc.CdcArtifactInventory ArtifactInventory { get; }

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
        CdcConnectorTemplateBindingArtifacts bindingArtifacts,
        IReadOnlyDictionary<string, string> config,
        CdcKafkaConnectRegistrationPayload? registrationPayload
    )
    {
        if (registrationPayload is null)
        {
            return;
        }

        if (
            !string.Equals(
                registrationPayload.Name,
                bindingArtifacts.ConnectorName.Value,
                StringComparison.Ordinal
            )
        )
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

internal static class CdcConnectorTemplateContractValidation
{
    private static readonly IReadOnlySet<string> _kafkaCertificateChainPropertyNames = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "ssl.truststore.certificates",
        "ssl.keystore.certificate.chain",
    };

    private static readonly IReadOnlySet<string> _kafkaEmptyValuePropertyNames = new HashSet<string>(
        StringComparer.Ordinal
    )
    {
        "ssl.endpoint.identification.algorithm",
    };

    private static readonly IReadOnlyList<string> _generatedKafkaSecurityPrefixes =
    [
        "producer.override.",
        "schema.history.internal.producer.",
        "schema.history.internal.consumer.",
    ];

    public static CdcSourceFingerprint ValidateSourceFingerprint(
        CdcSourceFingerprint sourceFingerprint,
        string parameterName
    ) => CdcSourceFingerprintMetadata.Validate(sourceFingerprint, parameterName);

    public static IReadOnlyDictionary<string, string> NormalizeStringProperties(
        IReadOnlyDictionary<string, string> properties,
        string parameterName
    ) =>
        NormalizeStringProperties(
            properties,
            parameterName,
            allowEmptyValues: false,
            AllowsNoLineBreaks,
            AllowsNoEmptyValueExceptions
        );

    public static IReadOnlyDictionary<string, string> NormalizeStringPropertiesAllowingEmptyValues(
        IReadOnlyDictionary<string, string> properties,
        string parameterName
    ) =>
        NormalizeStringProperties(
            properties,
            parameterName,
            allowEmptyValues: true,
            AllowsNoLineBreaks,
            AllowsNoEmptyValueExceptions
        );

    public static IReadOnlyDictionary<string, string> NormalizeKafkaClientSecurityProperties(
        IReadOnlyDictionary<string, string> properties,
        string parameterName
    ) =>
        NormalizeStringProperties(
            properties,
            parameterName,
            allowEmptyValues: false,
            IsKafkaCertificateChainProperty,
            IsKafkaEmptyValueProperty
        );

    public static IReadOnlyDictionary<string, string> NormalizeConnectorStringProperties(
        IReadOnlyDictionary<string, string> properties,
        string parameterName
    ) =>
        NormalizeStringProperties(
            properties,
            parameterName,
            allowEmptyValues: false,
            IsConnectorCertificateChainProperty,
            IsConnectorKafkaEmptyValueProperty
        );

    public static IReadOnlyDictionary<string, string> NormalizeConnectorStringPropertiesAllowingEmptyValues(
        IReadOnlyDictionary<string, string> properties,
        string parameterName
    ) =>
        NormalizeStringProperties(
            properties,
            parameterName,
            allowEmptyValues: true,
            IsConnectorCertificateChainProperty,
            IsConnectorKafkaEmptyValueProperty
        );

    private static IReadOnlyDictionary<string, string> NormalizeStringProperties(
        IReadOnlyDictionary<string, string> properties,
        string parameterName,
        bool allowEmptyValues,
        Func<string, bool> allowsLineBreaks,
        Func<string, bool> allowsEmptyValue
    )
    {
        ArgumentNullException.ThrowIfNull(properties);

        var normalizedProperties = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string propertyName = ValidateRequiredSafeText(property.Key, $"{parameterName}.Key");
            bool allowEmptyPropertyValue =
                allowEmptyValues || (property.Value is { Length: 0 } && allowsEmptyValue(propertyName));
            string propertyValue = ValidateSafeText(
                property.Value,
                $"{parameterName}[{propertyName}]",
                allowEmptyPropertyValue,
                allowsLineBreaks(propertyName)
            );

            if (!normalizedProperties.TryAdd(propertyName, propertyValue))
            {
                throw new ArgumentException(
                    "CDC connector template property names must be unique.",
                    $"{parameterName}.Key"
                );
            }
        }

        return new ReadOnlyDictionary<string, string>(normalizedProperties);
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

        if (ContainsDisallowedControlCharacter(value, allowLineBreaks: false))
        {
            throw new ArgumentException(
                "CDC connector template text values must not contain control characters.",
                parameterName
            );
        }

        return value;
    }

    private static string ValidateSafeText(
        string? value,
        string parameterName,
        bool allowEmptyValues,
        bool allowLineBreaks
    )
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (!allowEmptyValues && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "CDC connector template text values must be supplied.",
                parameterName
            );
        }

        if (ContainsDisallowedControlCharacter(value, allowLineBreaks))
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

    internal static bool IsKafkaCertificateChainProperty(string propertyName) =>
        _kafkaCertificateChainPropertyNames.Contains(propertyName);

    internal static bool IsConnectorCertificateChainProperty(string propertyName)
    {
        if (IsKafkaCertificateChainProperty(propertyName))
        {
            return true;
        }

        string prefix =
            _generatedKafkaSecurityPrefixes.FirstOrDefault(prefix =>
                propertyName.StartsWith(prefix, StringComparison.Ordinal)
            ) ?? string.Empty;

        return prefix.Length > 0 && IsKafkaCertificateChainProperty(propertyName[prefix.Length..]);
    }

    private static bool IsKafkaEmptyValueProperty(string propertyName) =>
        _kafkaEmptyValuePropertyNames.Contains(propertyName);

    private static bool IsConnectorKafkaEmptyValueProperty(string propertyName)
    {
        string prefix =
            _generatedKafkaSecurityPrefixes.FirstOrDefault(prefix =>
                propertyName.StartsWith(prefix, StringComparison.Ordinal)
            ) ?? string.Empty;

        return prefix.Length > 0 && IsKafkaEmptyValueProperty(propertyName[prefix.Length..]);
    }

    private static bool AllowsNoLineBreaks(string propertyName) => false;

    private static bool AllowsNoEmptyValueExceptions(string propertyName) => false;

    private static bool ContainsDisallowedControlCharacter(string value, bool allowLineBreaks) =>
        value.Any(character => char.IsControl(character) && !IsAllowedLineBreak(character, allowLineBreaks));

    private static bool IsAllowedLineBreak(char character, bool allowLineBreaks) =>
        allowLineBreaks && (character is '\r' or '\n');
}
