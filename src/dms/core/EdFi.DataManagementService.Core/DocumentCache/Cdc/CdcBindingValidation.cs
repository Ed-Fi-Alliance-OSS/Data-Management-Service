// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

public static class CdcBindingValidator
{
    public static CdcContractValidationResult Validate(CdcBinding? binding, string path = "$")
    {
        CdcDiagnosticCollector diagnostics = new();

        if (binding is null)
        {
            diagnostics.MissingRequiredField(path, "binding");
            return diagnostics.ToValidationResult();
        }

        ValidateContractVersion(binding.Version, FieldPath(path, "version"), "binding version", diagnostics);
        ValidateContractVersion(
            binding.ContractVersion,
            FieldPath(path, "contractVersion"),
            "binding contract version",
            diagnostics
        );
        ValidateSafeToken(
            binding.DeploymentKey,
            FieldPath(path, "deploymentKey"),
            "deploymentKey",
            diagnostics
        );
        ValidateSafeToken(binding.TenantKey, FieldPath(path, "tenantKey"), "tenantKey", diagnostics);
        ValidateDataStoreId(binding.DataStoreId, FieldPath(path, "dataStoreId"), diagnostics);
        ValidateSafeToken(binding.InstanceKey, FieldPath(path, "instanceKey"), "instanceKey", diagnostics);
        ValidatePositive(binding.Generation, FieldPath(path, "generation"), "generation", diagnostics);
        ValidateProvider(binding.Provider, FieldPath(path, "provider"), diagnostics);
        CdcSha256ValueValidator.Validate(
            binding.PhysicalSourceFingerprint,
            FieldPath(path, "physicalSourceFingerprint"),
            "physicalSourceFingerprint",
            required: true,
            diagnostics,
            CdcDiagnosticCategory.MalformedPayload,
            "CDC physicalSourceFingerprint must be `sha256:` plus 64 lowercase hex characters.",
            emptyIsMissing: true
        );
        ValidateArtifactName(
            binding.ConnectorName,
            FieldPath(path, "connectorName"),
            "connectorName",
            diagnostics
        );
        ValidateArtifactName(binding.TopicName, FieldPath(path, "topicName"), "topicName", diagnostics);
        ValidatePositive(
            binding.PartitionCount,
            FieldPath(path, "partitionCount"),
            "partitionCount",
            diagnostics
        );
        ValidatePartitionerAlgorithm(
            binding.PartitionerAlgorithm,
            FieldPath(path, "partitionerAlgorithm"),
            diagnostics
        );

        if (!diagnostics.HasDiagnostics)
        {
            ValidateDeterministicArtifacts(binding, path, diagnostics);
        }

        return diagnostics.ToValidationResult();
    }

    private static string FieldPath(string path, string fieldName) =>
        path == "$" ? $"$.{fieldName}" : $"{path}.{fieldName}";

    private static void ValidateContractVersion(
        int value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value != CdcJsonContract.CurrentContractVersion)
        {
            diagnostics.InvalidContractVersion(
                path,
                $"CDC {fieldName} `{value}` is not supported. Expected `{CdcJsonContract.CurrentContractVersion}`."
            );
        }
    }

    private static void ValidateSafeToken(
        string? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    ) => CdcKafkaSafeTokenValidator.Validate(value, path, fieldName, diagnostics);

    private static void ValidateDataStoreId(
        string? dataStoreId,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (dataStoreId is null || dataStoreId.Length == 0)
        {
            diagnostics.MissingRequiredField(path, "dataStoreId");
            return;
        }

        if (dataStoreId.Length > 1 && dataStoreId[0] == '0')
        {
            diagnostics.MalformedPayload(path, "CDC dataStoreId must not contain leading zero padding.");
            return;
        }

        if (dataStoreId.Any(character => character is < '0' or > '9'))
        {
            diagnostics.MalformedPayload(
                path,
                "CDC dataStoreId must be the invariant-culture decimal string of a positive DataStoreId."
            );
            return;
        }

        if (!long.TryParse(dataStoreId, out long value) || value <= 0)
        {
            diagnostics.MalformedPayload(
                path,
                "CDC dataStoreId must be the invariant-culture decimal string of a positive DataStoreId."
            );
        }
    }

    private static void ValidatePositive(
        long value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (value <= 0)
        {
            diagnostics.MalformedPayload(path, $"CDC {fieldName} must be positive.");
        }
    }

    private static void ValidateProvider(
        CdcProvider provider,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!Enum.IsDefined(provider))
        {
            diagnostics.InvalidEnumValue(path, "CDC provider must be `postgresql` or `sqlServer`.");
        }
    }

    private static void ValidateArtifactName(
        string? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        string? artifactName = CdcKafkaSafeTokenValidator.Validate(value, path, fieldName, diagnostics);
        if (
            artifactName is not null
            && artifactName.Length > CdcArtifactNameGenerator.MaximumKafkaOrConnectNameLength
        )
        {
            diagnostics.MalformedPayload(
                path,
                $"CDC {fieldName} must be at most {CdcArtifactNameGenerator.MaximumKafkaOrConnectNameLength} characters."
            );
        }
    }

    private static void ValidatePartitionerAlgorithm(
        string? partitionerAlgorithm,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (partitionerAlgorithm is null || partitionerAlgorithm.Length == 0)
        {
            diagnostics.MissingRequiredField(path, "partitionerAlgorithm");
            return;
        }

        if (
            !string.Equals(
                partitionerAlgorithm,
                CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.InvalidEnumValue(
                path,
                $"CDC partitionerAlgorithm must be `{CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm}`."
            );
        }
    }

    private static void ValidateDeterministicArtifacts(
        CdcBinding binding,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        foreach (CdcDiagnostic diagnostic in artifactNameResult.Diagnostics)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MalformedPayload,
                $"{path}{TrimRootPath(diagnostic.Path)}",
                "CDC binding artifacts must match the deterministic inventory."
            );
        }
    }

    private static string TrimRootPath(string path)
    {
        if (path == "$")
        {
            return string.Empty;
        }

        return path.StartsWith("$.", StringComparison.Ordinal) ? path[1..] : path;
    }
}
