// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.SchemaTools.Provisioning;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.SchemaTools.Restamping;

public static class SchemaRestampValidator
{
    public static void ValidateOrThrow(
        SchemaRestampSnapshot stored,
        EffectiveSchemaInfo target,
        ILogger logger
    )
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(logger);

        if (stored.Fingerprints.Count != 1)
        {
            throw Invalid("dms.EffectiveSchema singleton row count must be exactly one.");
        }

        var fingerprint = stored.Fingerprints[0];
        if (
            fingerprint.ApiSchemaFormatVersion is null
            || fingerprint.EffectiveSchemaHash is null
            || fingerprint.ResourceKeySeedHash is null
        )
        {
            throw Invalid("dms.EffectiveSchema fingerprint fields are missing.");
        }

        var storedIssues = EffectiveSchemaFingerprintContract.GetStoredValidationIssues(
            fingerprint.SingletonId,
            fingerprint.ApiSchemaFormatVersion,
            fingerprint.EffectiveSchemaHash,
            fingerprint.ResourceKeyCount,
            fingerprint.ResourceKeySeedHash
        );
        if (storedIssues.Count > 0)
        {
            throw Invalid($"dms.EffectiveSchema invalid fields: {FieldCategories(storedIssues)}.");
        }

        var expectedIssues = EffectiveSchemaFingerprintContract.GetExpectedValidationIssues(target);
        if (expectedIssues.Count > 0)
        {
            throw Invalid($"Target dms.EffectiveSchema invalid fields: {FieldCategories(expectedIssues)}.");
        }

        if (
            !string.Equals(
                fingerprint.ApiSchemaFormatVersion,
                target.ApiSchemaFormatVersion,
                StringComparison.Ordinal
            )
        )
        {
            throw Invalid("dms.EffectiveSchema.ApiSchemaFormatVersion differs from the target.");
        }

        if (fingerprint.ResourceKeyCount != target.ResourceKeyCount)
        {
            throw Invalid("dms.EffectiveSchema.ResourceKeyCount differs from the target.");
        }

        if (!fingerprint.ResourceKeySeedHash.AsSpan().SequenceEqual(target.ResourceKeySeedHash))
        {
            throw Invalid("dms.EffectiveSchema.ResourceKeySeedHash differs from the target.");
        }

        if (
            stored.SchemaComponents.Any(component =>
                !string.Equals(
                    component.EffectiveSchemaHash,
                    fingerprint.EffectiveSchemaHash,
                    StringComparison.Ordinal
                )
            )
        )
        {
            throw Invalid("dms.SchemaComponent.EffectiveSchemaHash differs from the singleton.");
        }

        try
        {
            SeedValidator.ValidateResourceKeysOrThrow(
                stored.ResourceKeys,
                target.ResourceKeysInIdOrder,
                logger
            );
        }
        catch (InvalidOperationException)
        {
            throw Invalid(
                "dms.ResourceKey rows differ in ResourceKeyId, ProjectName, ResourceName, or ResourceVersion."
            );
        }

        try
        {
            SeedValidator.ValidateSchemaComponentsOrThrow(
                stored.SchemaComponents.Select(component => component.Payload).ToArray(),
                target.SchemaComponentsInEndpointOrder,
                logger
            );
        }
        catch (InvalidOperationException)
        {
            throw Invalid(
                "dms.SchemaComponent rows differ in ProjectEndpointName, ProjectName, ProjectVersion, or IsExtensionProject."
            );
        }
    }

    private static SchemaRestampException Invalid(string safeMessage) =>
        new(SchemaRestampFailure.Validation, safeMessage);

    private static string FieldCategories(IEnumerable<string> issues)
    {
        string[] fields =
        [
            "EffectiveSchemaSingletonId",
            "ApiSchemaFormatVersion",
            "EffectiveSchemaHash",
            "ResourceKeyCount",
            "ResourceKeySeedHash",
        ];
        var categories = fields
            .Where(field => issues.Any(issue => issue.Contains(field, StringComparison.Ordinal)))
            .ToArray();
        return categories.Length > 0 ? string.Join(", ", categories) : "fingerprint metadata";
    }
}
