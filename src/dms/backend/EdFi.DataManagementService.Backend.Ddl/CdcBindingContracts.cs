// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Ddl;

public sealed record CdcBindingIdentity
{
    public const string KafkaMurmur2V1PartitionerAlgorithm = "kafka-murmur2-v1";

    public CdcBindingIdentity(
        CdcProvider provider,
        CdcSafeName connectorName,
        string publicTopicName,
        long bindingGeneration,
        string partitionerAlgorithm,
        CdcProviderArtifactNames providerArtifactNames,
        CdcSourceFingerprint boundPhysicalSourceFingerprint
    )
    {
        ArgumentNullException.ThrowIfNull(providerArtifactNames);
        ArgumentNullException.ThrowIfNull(boundPhysicalSourceFingerprint);

        Provider = provider;
        ConnectorName = CdcBindingTopicNames.ValidateDebeziumTopicPrefix(
            connectorName,
            nameof(connectorName)
        );
        PublicTopicName = CdcBindingTopicNames.ValidateKafkaTopicName(
            publicTopicName,
            nameof(publicTopicName)
        );
        CdcBindingTopicNames.ValidateDerivedTopicNames(provider, PublicTopicName);
        BindingGeneration =
            bindingGeneration > 0
                ? bindingGeneration
                : throw new ArgumentOutOfRangeException(
                    nameof(bindingGeneration),
                    bindingGeneration,
                    "CDC binding generation must be a positive integer."
                );
        PartitionerAlgorithm = ValidatePartitionerAlgorithm(
            partitionerAlgorithm,
            nameof(partitionerAlgorithm)
        );
        ProviderArtifactNames = ValidateProviderArtifactNames(
            provider,
            providerArtifactNames,
            nameof(providerArtifactNames)
        );
        BoundPhysicalSourceFingerprint = CdcSourceFingerprintMetadata.Validate(
            boundPhysicalSourceFingerprint,
            nameof(boundPhysicalSourceFingerprint)
        );
    }

    public CdcProvider Provider { get; }

    public CdcSafeName ConnectorName { get; }

    public string PublicTopicName { get; }

    public long BindingGeneration { get; }

    public string PartitionerAlgorithm { get; }

    public CdcProviderArtifactNames ProviderArtifactNames { get; }

    public CdcSourceFingerprint BoundPhysicalSourceFingerprint { get; }

    public string ProgressTopicName => CdcBindingTopicNames.ProgressTopicName(PublicTopicName);

    public string? SchemaHistoryTopicName =>
        Provider == CdcProvider.SqlServer
            ? CdcBindingTopicNames.SqlServerSchemaHistoryTopicName(PublicTopicName)
            : null;

    private static string ValidatePartitionerAlgorithm(string value, string parameterName)
    {
        string partitionerAlgorithm = CdcBindingContractValidation.ValidateRequiredSafeText(
            value,
            parameterName
        );

        if (
            !string.Equals(partitionerAlgorithm, KafkaMurmur2V1PartitionerAlgorithm, StringComparison.Ordinal)
        )
        {
            throw new ArgumentException(
                "CDC binding partitioner algorithm must be kafka-murmur2-v1.",
                parameterName
            );
        }

        return partitionerAlgorithm;
    }

    private static CdcProviderArtifactNames ValidateProviderArtifactNames(
        CdcProvider provider,
        CdcProviderArtifactNames artifactNames,
        string parameterName
    )
    {
        bool hasOnlyProviderArtifacts = provider switch
        {
            CdcProvider.Postgresql => artifactNames.Postgresql is not null && artifactNames.SqlServer is null,
            CdcProvider.SqlServer => artifactNames.SqlServer is not null && artifactNames.Postgresql is null,
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported CDC provider."
            ),
        };

        if (!hasOnlyProviderArtifacts)
        {
            throw new ArgumentException(
                $"CDC binding artifact names must contain only names for provider {provider}.",
                parameterName
            );
        }

        return artifactNames;
    }
}

public static class CdcBindingTopicNames
{
    private const int KafkaTopicNameMaxLength = 249;

    public static string ProgressTopicName(string publicTopicName) =>
        ValidateKafkaTopicName(
            $"{ValidateKafkaTopicName(publicTopicName, nameof(publicTopicName))}.cdc-progress",
            "progressTopicName"
        );

    public static string SqlServerSchemaHistoryTopicName(string publicTopicName) =>
        ValidateKafkaTopicName(
            $"{ValidateKafkaTopicName(publicTopicName, nameof(publicTopicName))}.schema-history",
            "schemaHistoryTopicName"
        );

    public static CdcSafeName ValidateDebeziumTopicPrefix(CdcSafeName connectorName, string parameterName)
    {
        ValidateKafkaTopicName(connectorName.Value, parameterName);

        return connectorName;
    }

    public static string ValidateKafkaTopicName(string topicName, string parameterName)
    {
        string value = CdcBindingContractValidation.ValidateRequiredSafeText(topicName, parameterName);

        if (value.Length > KafkaTopicNameMaxLength)
        {
            throw new ArgumentException(
                $"CDC binding Kafka topic names must be {KafkaTopicNameMaxLength} characters or fewer.",
                parameterName
            );
        }

        if (value is "." or "..")
        {
            throw new ArgumentException(
                "CDC binding Kafka topic names cannot be '.' or '..'.",
                parameterName
            );
        }

        if (value.Any(character => !IsKafkaTopicNameCharacter(character)))
        {
            throw new ArgumentException(
                "CDC binding Kafka topic names must contain only ASCII letters, digits, dots, underscores, and hyphens.",
                parameterName
            );
        }

        return value;
    }

    public static void ValidateDerivedTopicNames(CdcProvider provider, string publicTopicName)
    {
        _ = ProgressTopicName(publicTopicName);

        if (provider == CdcProvider.SqlServer)
        {
            _ = SqlServerSchemaHistoryTopicName(publicTopicName);
        }
    }

    private static bool IsKafkaTopicNameCharacter(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-';
}

internal static class CdcBindingContractValidation
{
    public static string ValidateRequiredSafeText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("CDC binding text values must be supplied.", parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "CDC binding text values must not contain control characters.",
                parameterName
            );
        }

        return value;
    }
}
