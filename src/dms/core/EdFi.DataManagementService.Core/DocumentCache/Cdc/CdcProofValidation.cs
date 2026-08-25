// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

public static class CdcAdoptionProofValidator
{
    private static readonly CdcAdoptionVerificationKind[] RequiredVerificationKinds =
        Enum.GetValues<CdcAdoptionVerificationKind>();

    public static CdcContractValidationResult Validate(CdcAdoptionProof proof, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(proof);

        CdcDiagnosticCollector diagnostics = new();

        CdcProofValidationRules.ValidateContractVersion(
            proof.ContractVersion,
            "$.contractVersion",
            diagnostics
        );
        CdcProofValidationRules.ValidateOperationId(proof.OperationId, "$.operationId", diagnostics);
        CdcProofValidationRules.ValidateTimestamp(proof.VerifiedAt, nowUtc, "$.verifiedAt", diagnostics);
        ValidateBinding(proof.Binding, diagnostics);
        ValidateVerificationResults(proof.VerificationResults, diagnostics);

        return diagnostics.ToValidationResult();
    }

    private static void ValidateBinding(CdcBinding? binding, CdcDiagnosticCollector diagnostics)
    {
        if (binding is null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MalformedProof,
                "$.binding",
                "CDC adoption proof binding is required."
            );
            return;
        }

        CdcProofValidationRules.ValidateBinding(binding, "$.binding", diagnostics);
        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        foreach (CdcDiagnostic diagnostic in artifactNameResult.Diagnostics)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                $"$.binding{CdcProofValidationRules.TrimRootPath(diagnostic.Path)}",
                "CDC adoption proof binding artifacts must match the deterministic inventory."
            );
        }
    }

    private static void ValidateVerificationResults(
        IReadOnlyList<CdcAdoptionVerificationResult>? verificationResults,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (verificationResults is null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.VerificationIncomplete,
                "$.verificationResults",
                "CDC adoption proof verificationResults are required."
            );
            return;
        }

        HashSet<CdcAdoptionVerificationKind> seenKinds = [];
        for (int index = 0; index < verificationResults.Count; index++)
        {
            CdcAdoptionVerificationResult? result = verificationResults[index];
            string path = $"$.verificationResults[{index}]";
            if (result is null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.VerificationIncomplete,
                    path,
                    "CDC adoption proof verification result is required."
                );
                continue;
            }

            if (!Enum.IsDefined(result.VerificationKind))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.VerificationIncomplete,
                    $"{path}.verificationKind",
                    "CDC adoption proof contains an unsupported verificationKind."
                );
            }
            else if (!seenKinds.Add(result.VerificationKind))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.VerificationIncomplete,
                    $"{path}.verificationKind",
                    "CDC adoption proof contains a duplicate verificationKind."
                );
            }

            if (!Enum.IsDefined(result.State) || result.State != CdcAdoptionVerificationState.ExactMatch)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.VerificationIncomplete,
                    $"{path}.state",
                    "CDC adoption proof verification state must be exactMatch."
                );
            }

            CdcProofValidationRules.ValidateEvidenceSummary(
                result.EvidenceSummary,
                $"{path}.evidenceSummary",
                diagnostics
            );
        }

        foreach (CdcAdoptionVerificationKind requiredKind in RequiredVerificationKinds)
        {
            if (!seenKinds.Contains(requiredKind))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.VerificationIncomplete,
                    "$.verificationResults",
                    $"CDC adoption proof is missing `{CdcProofValidationRules.ToLowerCamel(requiredKind)}` verification."
                );
            }
        }
    }
}

public static class CdcCleanupProofValidator
{
    public static CdcContractValidationResult ValidateStructure(CdcCleanupProof proof, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(proof);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(proof, nowUtc, diagnostics);

        return diagnostics.ToValidationResult();
    }

    public static CdcContractValidationResult Validate(
        CdcCleanupProof proof,
        CdcBinding binding,
        DateTimeOffset nowUtc
    )
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(binding);

        CdcDiagnosticCollector diagnostics = new();

        ValidateStructure(proof, nowUtc, diagnostics);
        ValidateBindingIdentityMatchesBinding(proof.BindingIdentity, binding, diagnostics);

        CdcArtifactNameResult artifactNameResult = CdcArtifactNameGenerator.RecoverFromBinding(binding);
        foreach (CdcDiagnostic diagnostic in artifactNameResult.Diagnostics)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                diagnostic.Path,
                "CDC cleanup proof binding artifacts must match the deterministic inventory."
            );
        }

        if (artifactNameResult.Inventory is not null)
        {
            ValidateGovernedArtifactCoverage(
                proof.GovernedArtifacts,
                artifactNameResult.Inventory.GovernedArtifacts,
                diagnostics
            );
        }

        return diagnostics.ToValidationResult();
    }

    private static void ValidateStructure(
        CdcCleanupProof proof,
        DateTimeOffset nowUtc,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcProofValidationRules.ValidateContractVersion(
            proof.ContractVersion,
            "$.contractVersion",
            diagnostics
        );
        CdcProofValidationRules.ValidateOperationId(proof.OperationId, "$.operationId", diagnostics);
        CdcProofValidationRules.ValidateTimestamp(proof.VerifiedAt, nowUtc, "$.verifiedAt", diagnostics);
        CdcProofValidationRules.ValidateCompleteBindingIdentity(
            proof.BindingIdentity,
            "$.bindingIdentity",
            diagnostics
        );
        ValidateCleanupMode(proof.CleanupMode, diagnostics);
        ValidateGovernedArtifactShape(proof.GovernedArtifacts, diagnostics);
    }

    private static void ValidateCleanupMode(CdcCleanupMode cleanupMode, CdcDiagnosticCollector diagnostics)
    {
        if (!Enum.IsDefined(cleanupMode) || cleanupMode != CdcCleanupMode.RetireBindingGeneration)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MalformedProof,
                "$.cleanupMode",
                "CDC cleanup proof cleanupMode must be retireBindingGeneration."
            );
        }
    }

    private static void ValidateBindingIdentityMatchesBinding(
        CdcCompleteBindingIdentity? bindingIdentity,
        CdcBinding binding,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (bindingIdentity is null)
        {
            return;
        }

        if (bindingIdentity != binding.ToCompleteBindingIdentity())
        {
            diagnostics.Add(
                CdcDiagnosticCategory.BindingIdentityMismatch,
                "$.bindingIdentity",
                "CDC cleanup proof bindingIdentity must match the persisted binding."
            );
        }
    }

    private static void ValidateGovernedArtifactShape(
        IReadOnlyList<CdcGovernedArtifact>? governedArtifacts,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (governedArtifacts is null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InventoryIncomplete,
                "$.governedArtifacts",
                "CDC cleanup proof governedArtifacts are required."
            );
            return;
        }

        HashSet<CdcGovernedArtifactKind> seenKinds = [];
        for (int index = 0; index < governedArtifacts.Count; index++)
        {
            CdcGovernedArtifact? artifact = governedArtifacts[index];
            string path = $"$.governedArtifacts[{index}]";
            if (artifact is null)
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InventoryIncomplete,
                    path,
                    "CDC cleanup proof governed artifact is required."
                );
                continue;
            }

            if (!Enum.IsDefined(artifact.ArtifactKind))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.UnexpectedArtifact,
                    $"{path}.artifactKind",
                    "CDC cleanup proof contains an unsupported artifactKind."
                );
            }
            else if (!seenKinds.Add(artifact.ArtifactKind))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.DuplicateArtifact,
                    $"{path}.artifactKind",
                    "CDC cleanup proof contains a duplicate governed artifact kind."
                );
            }

            CdcProofValidationRules.ValidateArtifactName(
                artifact.ArtifactName,
                $"{path}.artifactName",
                "artifactName",
                diagnostics
            );

            if (
                !Enum.IsDefined(artifact.CleanupState)
                || artifact.CleanupState is not (CdcCleanupState.Deleted or CdcCleanupState.NotFound)
            )
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.ArtifactNotRemoved,
                    $"{path}.cleanupState",
                    "CDC cleanup proof cleanupState must be deleted or notFound."
                );
            }

            CdcProofValidationRules.ValidateEvidenceSummary(
                artifact.EvidenceSummary,
                $"{path}.evidenceSummary",
                diagnostics
            );
        }
    }

    private static void ValidateGovernedArtifactCoverage(
        IReadOnlyList<CdcGovernedArtifact>? governedArtifacts,
        IReadOnlyList<CdcGovernedArtifactName> expectedArtifacts,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (governedArtifacts is null)
        {
            return;
        }

        Dictionary<CdcGovernedArtifactKind, CdcGovernedArtifact> actualByKind = [];
        foreach (CdcGovernedArtifact? artifact in governedArtifacts)
        {
            if (artifact is null || !Enum.IsDefined(artifact.ArtifactKind))
            {
                continue;
            }

            actualByKind.TryAdd(artifact.ArtifactKind, artifact);
        }

        Dictionary<CdcGovernedArtifactKind, CdcGovernedArtifactName> expectedByKind =
            expectedArtifacts.ToDictionary(artifact => artifact.Kind);

        foreach ((CdcGovernedArtifactKind actualKind, CdcGovernedArtifact actualArtifact) in actualByKind)
        {
            if (!expectedByKind.ContainsKey(actualKind))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.UnexpectedArtifact,
                    "$.governedArtifacts",
                    $"CDC cleanup proof contains provider-inapplicable artifact `{CdcProofValidationRules.ToLowerCamel(actualKind)}`."
                );
                continue;
            }

            string expectedName = expectedByKind[actualKind].Name;
            if (!string.Equals(actualArtifact.ArtifactName, expectedName, StringComparison.Ordinal))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.ArtifactNameMismatch,
                    "$.governedArtifacts",
                    $"CDC cleanup proof artifact `{CdcProofValidationRules.ToLowerCamel(actualKind)}` name must match the binding-derived inventory."
                );
            }
        }

        foreach ((CdcGovernedArtifactKind expectedKind, CdcGovernedArtifactName _) in expectedByKind)
        {
            if (!actualByKind.ContainsKey(expectedKind))
            {
                diagnostics.Add(
                    CdcDiagnosticCategory.InventoryIncomplete,
                    "$.governedArtifacts",
                    $"CDC cleanup proof is missing `{CdcProofValidationRules.ToLowerCamel(expectedKind)}`."
                );
            }
        }
    }
}

internal static class CdcProofValidationRules
{
    private const int MaximumOperationIdLength = 128;
    private const int MaximumFingerprintLength = 71;
    private const string Sha256Prefix = "sha256:";

    public static void ValidateContractVersion(
        int contractVersion,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (contractVersion != CdcJsonContract.CurrentContractVersion)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidContractVersion,
                path,
                $"CDC proof contract version `{contractVersion}` is not supported. Expected `{CdcJsonContract.CurrentContractVersion}`."
            );
        }
    }

    public static void ValidateOperationId(
        string? operationId,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            operationId is null
            || operationId.Length == 0
            || operationId.Length > MaximumOperationIdLength
            || !CdcKafkaSafeTokenValidator.IsValid(operationId)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidOperationId,
                path,
                "CDC proof operationId must be a non-empty safe operation token."
            );
        }
    }

    public static void ValidateTimestamp(
        DateTimeOffset timestamp,
        DateTimeOffset nowUtc,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        DateTimeOffset normalizedNowUtc = nowUtc.ToUniversalTime();
        if (timestamp.Offset != TimeSpan.Zero || timestamp > normalizedNowUtc)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidTimestamp,
                path,
                "CDC proof timestamp must be UTC and must not be in the future."
            );
        }
    }

    public static void ValidateBinding(CdcBinding binding, string path, CdcDiagnosticCollector diagnostics)
    {
        ValidateContractVersion(binding.ContractVersion, $"{path}.contractVersion", diagnostics);
        if (binding.Version != CdcJsonContract.CurrentContractVersion)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.InvalidContractVersion,
                $"{path}.version",
                $"CDC binding version `{binding.Version}` is not supported. Expected `{CdcJsonContract.CurrentContractVersion}`."
            );
        }

        ValidateBindingIdentityFields(binding.ToCompleteBindingIdentity(), path, diagnostics);
        ValidatePositive(binding.PartitionCount, $"{path}.partitionCount", "partitionCount", diagnostics);
        ValidatePartitionerAlgorithm(
            binding.PartitionerAlgorithm,
            $"{path}.partitionerAlgorithm",
            diagnostics
        );
    }

    public static void ValidateCompleteBindingIdentity(
        CdcCompleteBindingIdentity? bindingIdentity,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (bindingIdentity is null)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.BindingIdentityMismatch,
                path,
                "CDC proof bindingIdentity is required."
            );
            return;
        }

        ValidateBindingIdentityFields(bindingIdentity, path, diagnostics);
        CdcArtifactNameResult artifactNameResult =
            CdcArtifactNameGenerator.RecoverFromCompleteBindingIdentity(bindingIdentity);
        foreach (CdcDiagnostic diagnostic in artifactNameResult.Diagnostics)
        {
            diagnostics.Add(
                CdcDiagnosticCategory.ArtifactNameMismatch,
                $"{path}{TrimRootPath(diagnostic.Path)}",
                "CDC proof bindingIdentity artifacts must match the deterministic inventory."
            );
        }
    }

    public static void ValidateArtifactName(
        string? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            value is null
            || value.Length == 0
            || value.Length > CdcArtifactNameGenerator.MaximumKafkaOrConnectNameLength
            || !CdcKafkaSafeTokenValidator.IsValid(value)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MalformedProof,
                path,
                $"CDC proof {fieldName} must be a non-empty safe artifact name."
            );
        }
    }

    public static void ValidateEvidenceSummary(
        string? evidenceSummary,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!CdcContractText.IsValidEvidenceText(evidenceSummary))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.UnsafeEvidence,
                path,
                "CDC proof evidenceSummary must be bounded sanitized evidence."
            );
        }
    }

    public static string TrimRootPath(string path)
    {
        if (path == "$")
        {
            return string.Empty;
        }

        return path.StartsWith("$.", StringComparison.Ordinal) ? path[1..] : path;
    }

    public static string ToLowerCamel<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        string name = value.ToString();
        return name.Length == 0 ? name : $"{char.ToLowerInvariant(name[0])}{name[1..]}";
    }

    private static void ValidateBindingIdentityFields(
        CdcCompleteBindingIdentity bindingIdentity,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        ValidateSafeToken(
            bindingIdentity.DeploymentKey,
            $"{path}.deploymentKey",
            "deploymentKey",
            diagnostics
        );
        ValidateSafeToken(bindingIdentity.TenantKey, $"{path}.tenantKey", "tenantKey", diagnostics);
        ValidateDataStoreId(bindingIdentity.DataStoreId, $"{path}.dataStoreId", diagnostics);
        ValidateSafeToken(bindingIdentity.InstanceKey, $"{path}.instanceKey", "instanceKey", diagnostics);
        ValidatePositive(bindingIdentity.Generation, $"{path}.generation", "generation", diagnostics);
        ValidateProvider(bindingIdentity.Provider, $"{path}.provider", diagnostics);
        ValidateSha256(
            bindingIdentity.PhysicalSourceFingerprint,
            $"{path}.physicalSourceFingerprint",
            "physicalSourceFingerprint",
            diagnostics
        );
        ValidateArtifactName(
            bindingIdentity.ConnectorName,
            $"{path}.connectorName",
            "connectorName",
            diagnostics
        );
        ValidateArtifactName(bindingIdentity.TopicName, $"{path}.topicName", "topicName", diagnostics);
    }

    private static void ValidateSafeToken(
        string? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (!CdcKafkaSafeTokenValidator.IsValid(value))
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MalformedProof,
                path,
                $"CDC proof {fieldName} must be a non-empty safe token."
            );
        }
    }

    private static void ValidateDataStoreId(
        string? dataStoreId,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            dataStoreId is null
            || dataStoreId.Length == 0
            || dataStoreId.Any(character => character is < '0' or > '9')
            || (dataStoreId.Length > 1 && dataStoreId[0] == '0')
            || !long.TryParse(dataStoreId, out long dataStoreIdValue)
            || dataStoreIdValue <= 0
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MalformedProof,
                path,
                "CDC proof dataStoreId must be the invariant-culture decimal string of a positive DataStoreId."
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
            diagnostics.Add(
                CdcDiagnosticCategory.MalformedProof,
                path,
                $"CDC proof {fieldName} must be positive."
            );
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
            diagnostics.Add(
                CdcDiagnosticCategory.MalformedProof,
                path,
                "CDC proof provider must be postgresql or sqlServer."
            );
        }
    }

    private static void ValidateSha256(
        string? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            value is null
            || value.Length != MaximumFingerprintLength
            || !value.StartsWith(Sha256Prefix, StringComparison.Ordinal)
            || !value[Sha256Prefix.Length..].All(IsLowercaseHex)
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MalformedProof,
                path,
                $"CDC proof {fieldName} must be `sha256:` plus 64 lowercase hex characters."
            );
        }
    }

    private static bool IsLowercaseHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static void ValidatePartitionerAlgorithm(
        string? partitionerAlgorithm,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (
            !string.Equals(
                partitionerAlgorithm,
                CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm,
                StringComparison.Ordinal
            )
        )
        {
            diagnostics.Add(
                CdcDiagnosticCategory.MalformedProof,
                path,
                $"CDC proof partitionerAlgorithm must be {CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm}."
            );
        }
    }
}
