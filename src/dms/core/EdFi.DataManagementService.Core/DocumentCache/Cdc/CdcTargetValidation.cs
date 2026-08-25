// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics.CodeAnalysis;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

public sealed record CdcTargetInput(
    string? DeploymentKey,
    string? TenantKey,
    string? DataStoreId,
    string? InstanceKey,
    CdcProvider Provider,
    string? TopicPrefix,
    long Generation,
    int PartitionCount,
    string? PartitionerAlgorithm
);

public sealed record CdcValidatedTarget(
    string DeploymentKey,
    string TenantKey,
    string DataStoreId,
    string InstanceKey,
    CdcProvider Provider,
    string TopicPrefix,
    long Generation,
    int PartitionCount,
    string PartitionerAlgorithm
)
{
    public CdcTargetIdentity ToTargetIdentity() =>
        new(DeploymentKey, TenantKey, DataStoreId, InstanceKey, Generation, Provider);

    public CdcBindingIdentity ToBindingIdentity() =>
        new(DeploymentKey, TenantKey, DataStoreId, InstanceKey, Generation);
}

public sealed record CdcTargetValidationResult
{
    private CdcTargetValidationResult(CdcValidatedTarget? target, IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        Target = target;
        Diagnostics = diagnostics;
    }

    public CdcValidatedTarget? Target { get; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    public bool Succeeded => Target is not null && Diagnostics.Count == 0;

    public static CdcTargetValidationResult Success(CdcValidatedTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new(target, []);
    }

    public static CdcTargetValidationResult Failure(IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new(null, diagnostics);
    }
}

public static class CdcTargetValidator
{
    public const string DefaultBindingTenantKey = "default";
    public const string KafkaMurmur2V1PartitionerAlgorithm = "kafka-murmur2-v1";

    public static CdcTargetValidationResult Validate(CdcTargetInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        CdcDiagnosticCollector diagnostics = new();

        string? deploymentKey = CdcKafkaSafeTokenValidator.Validate(
            input.DeploymentKey,
            "$.deploymentKey",
            "deploymentKey",
            diagnostics
        );
        string? tenantKey = ValidateTenantKey(input.TenantKey, diagnostics);
        string? dataStoreId = ValidateDataStoreId(input.DataStoreId, "$.dataStoreId", diagnostics);
        string? instanceKey = CdcKafkaSafeTokenValidator.Validate(
            input.InstanceKey,
            "$.instanceKey",
            "instanceKey",
            diagnostics
        );
        string? topicPrefix = CdcKafkaSafeTokenValidator.Validate(
            input.TopicPrefix,
            "$.topicPrefix",
            "topicPrefix",
            diagnostics
        );

        ValidateProvider(input.Provider, "$.provider", diagnostics);
        ValidatePositive(input.Generation, "$.generation", "generation", diagnostics);
        ValidatePositive(input.PartitionCount, "$.partitionCount", "partitionCount", diagnostics);
        ValidatePartitionerAlgorithm(input.PartitionerAlgorithm, diagnostics);

        if (
            deploymentKey is not null
            && instanceKey is not null
            && topicPrefix is not null
            && input.Generation > 0
            && Enum.IsDefined(input.Provider)
        )
        {
            ValidateRenderedArtifactNames(
                deploymentKey,
                topicPrefix,
                instanceKey,
                input.Generation,
                input.Provider,
                diagnostics
            );
        }

        if (
            diagnostics.HasDiagnostics
            || deploymentKey is null
            || tenantKey is null
            || dataStoreId is null
            || instanceKey is null
            || topicPrefix is null
            || input.PartitionerAlgorithm is null
        )
        {
            return CdcTargetValidationResult.Failure(diagnostics.Diagnostics);
        }

        return CdcTargetValidationResult.Success(
            new(
                deploymentKey,
                tenantKey,
                dataStoreId,
                instanceKey,
                input.Provider,
                topicPrefix,
                input.Generation,
                input.PartitionCount,
                input.PartitionerAlgorithm
            )
        );
    }

    public static CdcContractValidationResult ValidateBindingIdentity(CdcBindingIdentity bindingIdentity)
    {
        ArgumentNullException.ThrowIfNull(bindingIdentity);

        CdcDiagnosticCollector diagnostics = new();

        CdcKafkaSafeTokenValidator.Validate(
            bindingIdentity.DeploymentKey,
            "$.deploymentKey",
            "deploymentKey",
            diagnostics
        );
        ValidateTenantKey(bindingIdentity.TenantKey, diagnostics);
        ValidateDataStoreId(bindingIdentity.DataStoreId, "$.dataStoreId", diagnostics);
        CdcKafkaSafeTokenValidator.Validate(
            bindingIdentity.InstanceKey,
            "$.instanceKey",
            "instanceKey",
            diagnostics
        );
        ValidatePositive(bindingIdentity.Generation, "$.generation", "generation", diagnostics);

        return diagnostics.ToValidationResult();
    }

    public static string MapE18TenantKeyToBindingTenantKey(string? tenantKey) =>
        string.IsNullOrEmpty(tenantKey) ? DefaultBindingTenantKey : tenantKey;

    private static string? ValidateTenantKey(string? tenantKey, CdcDiagnosticCollector diagnostics)
    {
        string normalizedTenantKey = MapE18TenantKeyToBindingTenantKey(tenantKey);
        return CdcKafkaSafeTokenValidator.Validate(
            normalizedTenantKey,
            "$.tenantKey",
            "tenantKey",
            diagnostics
        );
    }

    private static string? ValidateDataStoreId(
        string? dataStoreId,
        string path,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (dataStoreId is null || dataStoreId.Length == 0)
        {
            diagnostics.MissingRequiredField(path, "dataStoreId");
            return null;
        }

        if (dataStoreId.Length > 1 && dataStoreId[0] == '0')
        {
            diagnostics.MalformedPayload(path, "CDC dataStoreId must not contain leading zero padding.");
            return null;
        }

        if (dataStoreId.Any(character => character is < '0' or > '9'))
        {
            diagnostics.MalformedPayload(
                path,
                "CDC dataStoreId must be the invariant-culture decimal string of a positive DataStoreId."
            );
            return null;
        }

        if (!long.TryParse(dataStoreId, out long value) || value <= 0)
        {
            diagnostics.MalformedPayload(
                path,
                "CDC dataStoreId must be the invariant-culture decimal string of a positive DataStoreId."
            );
            return null;
        }

        return dataStoreId;
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

    private static void ValidatePartitionerAlgorithm(
        string? partitionerAlgorithm,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (partitionerAlgorithm is null || partitionerAlgorithm.Length == 0)
        {
            diagnostics.MissingRequiredField("$.partitionerAlgorithm", "partitionerAlgorithm");
            return;
        }

        if (
            !string.Equals(partitionerAlgorithm, KafkaMurmur2V1PartitionerAlgorithm, StringComparison.Ordinal)
        )
        {
            diagnostics.InvalidEnumValue(
                "$.partitionerAlgorithm",
                $"CDC partitionerAlgorithm must be `{KafkaMurmur2V1PartitionerAlgorithm}`."
            );
        }
    }

    private static void ValidateRenderedArtifactNames(
        string deploymentKey,
        string topicPrefix,
        string instanceKey,
        long generation,
        CdcProvider provider,
        CdcDiagnosticCollector diagnostics
    )
    {
        CdcArtifactNameResult result = CdcArtifactNameGenerator.Render(
            new(deploymentKey, topicPrefix, instanceKey, generation, provider)
        );
        foreach (CdcDiagnostic diagnostic in result.Diagnostics)
        {
            diagnostics.Add(diagnostic);
        }
    }
}

public static class CdcKafkaSafeTokenValidator
{
    public static string? Validate(
        string? value,
        string path,
        string fieldName,
        CdcDiagnosticCollector diagnostics
    )
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (value is null || value.Length == 0)
        {
            diagnostics.MissingRequiredField(path, fieldName);
            return null;
        }

        if (value is "." or "..")
        {
            diagnostics.MalformedPayload(path, $"CDC {fieldName} must not be a path traversal token.");
            return null;
        }

        if (IsSeparator(value[0]) || IsSeparator(value[^1]))
        {
            diagnostics.MalformedPayload(path, $"CDC {fieldName} must not start or end with a separator.");
            return null;
        }

        bool previousWasSeparator = false;
        foreach (char character in value)
        {
            if (!IsAllowed(character))
            {
                diagnostics.MalformedPayload(
                    path,
                    $"CDC {fieldName} may contain only lowercase ASCII letters, digits, dot, underscore, and hyphen."
                );
                return null;
            }

            bool currentIsSeparator = IsSeparator(character);
            if (previousWasSeparator && currentIsSeparator)
            {
                diagnostics.MalformedPayload(
                    path,
                    $"CDC {fieldName} must not contain consecutive separators."
                );
                return null;
            }

            previousWasSeparator = currentIsSeparator;
        }

        return value;
    }

    public static bool IsValid(string? value)
    {
        CdcDiagnosticCollector diagnostics = new();
        return Validate(value, "$", "token", diagnostics) is not null && !diagnostics.HasDiagnostics;
    }

    private static bool IsAllowed(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';

    private static bool IsSeparator(char character) => character is '.' or '_' or '-';
}

public static class CdcProviderToken
{
    public static bool TryToRelationalProviderToken(
        CdcProvider provider,
        [NotNullWhen(true)] out RelationalProviderToken? providerToken
    )
    {
        providerToken = provider switch
        {
            CdcProvider.Postgresql => RelationalProviderToken.Postgresql,
            CdcProvider.SqlServer => RelationalProviderToken.SqlServer,
            _ => null,
        };

        return providerToken is not null;
    }
}

public sealed record CdcPhysicalSourceFingerprintResult
{
    private CdcPhysicalSourceFingerprintResult(string? fingerprint, IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        Fingerprint = fingerprint;
        Diagnostics = diagnostics;
    }

    public string? Fingerprint { get; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    public bool Succeeded => Fingerprint is not null && Diagnostics.Count == 0;

    public static CdcPhysicalSourceFingerprintResult Success(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        return new(fingerprint, []);
    }

    public static CdcPhysicalSourceFingerprintResult Failure(IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new(null, diagnostics);
    }
}

public static class CdcPhysicalSourceFingerprintCalculator
{
    public static CdcPhysicalSourceFingerprintResult Compute(CdcProvider provider, Guid sourceIdentity)
    {
        CdcDiagnosticCollector diagnostics = new();

        if (
            !CdcProviderToken.TryToRelationalProviderToken(
                provider,
                out RelationalProviderToken? providerToken
            )
        )
        {
            diagnostics.InvalidEnumValue("$.provider", "CDC provider must be `postgresql` or `sqlServer`.");
        }

        if (sourceIdentity == Guid.Empty)
        {
            diagnostics.MalformedPayload("$.sourceIdentity", "CDC sourceIdentity must not be the zero UUID.");
        }

        if (diagnostics.HasDiagnostics || providerToken is null)
        {
            return CdcPhysicalSourceFingerprintResult.Failure(diagnostics.Diagnostics);
        }

        DocumentCachePhysicalSourceFingerprint fingerprint =
            DocumentCachePhysicalSourceFingerprintCalculator.Compute(providerToken, sourceIdentity);
        return CdcPhysicalSourceFingerprintResult.Success(fingerprint.Value);
    }
}
