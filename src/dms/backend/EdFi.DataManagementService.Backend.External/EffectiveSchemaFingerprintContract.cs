// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.External;

/// <summary>
/// Shared contract validation for the <c>dms.EffectiveSchema</c> fingerprint values
/// used by both runtime reads and provisioning-time validation.
/// </summary>
public static class EffectiveSchemaFingerprintContract
{
    public const short ExpectedSingletonId = 1;
    public const int EffectiveSchemaHashLength = 64;
    public const int ResourceKeySeedHashLength = 32;

    public static IReadOnlyList<string> GetStoredValidationIssues(
        short effectiveSchemaSingletonId,
        string apiSchemaFormatVersion,
        string effectiveSchemaHash,
        int resourceKeyCount,
        byte[] resourceKeySeedHash
    )
    {
        ArgumentNullException.ThrowIfNull(apiSchemaFormatVersion);
        ArgumentNullException.ThrowIfNull(effectiveSchemaHash);
        ArgumentNullException.ThrowIfNull(resourceKeySeedHash);

        List<string> issues = [];

        if (effectiveSchemaSingletonId != ExpectedSingletonId)
        {
            issues.Add(
                $"{EffectiveSchemaTableDefinition.TableDisplayName} must contain a singleton row with "
                    + $"EffectiveSchemaSingletonId = {ExpectedSingletonId}, but found {effectiveSchemaSingletonId}."
            );
        }

        AddSharedFieldIssues(
            issues,
            apiSchemaFormatVersion,
            effectiveSchemaHash,
            resourceKeyCount,
            resourceKeySeedHash
        );

        return issues;
    }

    public static IReadOnlyList<string> GetExpectedValidationIssues(EffectiveSchemaInfo effectiveSchema)
    {
        ArgumentNullException.ThrowIfNull(effectiveSchema);
        ArgumentNullException.ThrowIfNull(effectiveSchema.ResourceKeySeedHash);

        List<string> issues = [];

        AddSharedFieldIssues(
            issues,
            effectiveSchema.ApiSchemaFormatVersion,
            effectiveSchema.EffectiveSchemaHash,
            effectiveSchema.ResourceKeyCount,
            effectiveSchema.ResourceKeySeedHash
        );

        return issues;
    }

    public static string CreateMultipleRowsMessage() =>
        $"{EffectiveSchemaTableDefinition.TableDisplayName} must contain exactly one singleton row, but multiple rows were found.";

    private static void AddSharedFieldIssues(
        ICollection<string> issues,
        string apiSchemaFormatVersion,
        string effectiveSchemaHash,
        int resourceKeyCount,
        byte[] resourceKeySeedHash
    )
    {
        if (string.IsNullOrWhiteSpace(apiSchemaFormatVersion))
        {
            issues.Add(
                $"{EffectiveSchemaTableDefinition.TableDisplayName}.{EffectiveSchemaTableDefinition.ApiSchemaFormatVersion.Value} must not be empty."
            );
        }

        if (!IsValidLowercaseHex(effectiveSchemaHash, EffectiveSchemaHashLength))
        {
            issues.Add(
                $"{EffectiveSchemaTableDefinition.TableDisplayName}.{EffectiveSchemaTableDefinition.EffectiveSchemaHash.Value} must be {EffectiveSchemaHashLength} lowercase hex characters."
            );
        }

        if (resourceKeyCount < 0)
        {
            issues.Add(
                $"{EffectiveSchemaTableDefinition.TableDisplayName}.{EffectiveSchemaTableDefinition.ResourceKeyCount.Value} must be non-negative, but found {resourceKeyCount}."
            );
        }

        if (resourceKeySeedHash.Length != ResourceKeySeedHashLength)
        {
            issues.Add(
                $"{EffectiveSchemaTableDefinition.TableDisplayName}.{EffectiveSchemaTableDefinition.ResourceKeySeedHash.Value} must be exactly {ResourceKeySeedHashLength} bytes, but found {resourceKeySeedHash.Length}."
            );
        }
    }

    private static bool IsValidLowercaseHex(string value, int expectedLength)
    {
        if (value.Length != expectedLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c) && (c < 'a' || c > 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
