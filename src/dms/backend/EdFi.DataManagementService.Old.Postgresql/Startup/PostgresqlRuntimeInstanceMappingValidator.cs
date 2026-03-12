// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace EdFi.DataManagementService.Old.Postgresql.Startup;

internal sealed class PostgresqlRuntimeInstanceMappingValidator(
    IDmsInstanceProvider dmsInstanceProvider,
    IPostgresqlRuntimeDatabaseMetadataReader databaseMetadataReader,
    PostgresqlValidatedResourceKeyMapCache validatedResourceKeyMapCache,
    ILogger<PostgresqlRuntimeInstanceMappingValidator> logger
)
{
    private const int MaxRowsPerDiffSection = 20;

    public async Task<PostgresqlRuntimeInstanceMappingValidationSummary> ValidateLoadedInstancesAsync(
        MappingSet mappingSet,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        var validatedDatabaseCount = 0;
        var reusedValidationCount = 0;
        var instanceCount = 0;
        var failures = new List<string>();

        foreach (var tenantKey in dmsInstanceProvider.GetLoadedTenantKeys())
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? tenant = string.IsNullOrEmpty(tenantKey) ? null : tenantKey;

            foreach (var instance in dmsInstanceProvider.GetAll(tenant))
            {
                cancellationToken.ThrowIfCancellationRequested();
                instanceCount++;

                try
                {
                    var wasCacheHit = await ValidateInstanceAsync(
                        mappingSet,
                        instance,
                        tenant,
                        cancellationToken
                    );

                    if (wasCacheHit)
                    {
                        reusedValidationCount++;
                    }
                    else
                    {
                        validatedDatabaseCount++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "PostgreSQL runtime mapping startup validation failed");
                    failures.Add(ex.Message);
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "PostgreSQL runtime mapping startup validation failed:"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, failures)
            );
        }

        return new PostgresqlRuntimeInstanceMappingValidationSummary(
            InstanceCount: instanceCount,
            ValidatedDatabaseCount: validatedDatabaseCount,
            ReusedValidationCount: reusedValidationCount
        );
    }

    private async Task<bool> ValidateInstanceAsync(
        MappingSet mappingSet,
        DmsInstance instance,
        string? tenant,
        CancellationToken cancellationToken
    )
    {
        var connectionString = instance.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{CreateContextPrefix(instance, tenant, mappingSet.Key)} has no connection string configured."
            );
        }

        if (
            validatedResourceKeyMapCache.TryGet(connectionString, out var cachedMaps)
            && cachedMaps.MappingSetKey == mappingSet.Key
        )
        {
            logger.LogDebug(
                "Reused cached PostgreSQL runtime validation for DMS instance {InstanceId}",
                instance.Id
            );

            return true;
        }

        var fingerprint = await ReadFingerprintOrThrowAsync(
            connectionString,
            instance,
            tenant,
            mappingSet.Key,
            cancellationToken
        );

        ValidateEffectiveSchemaHashOrThrow(fingerprint, mappingSet.Key, instance, tenant);

        var expectedEffectiveSchema = mappingSet.Model.EffectiveSchema;
        var expectedResourceKeyCount = expectedEffectiveSchema.ResourceKeyCount;
        var expectedResourceKeySeedHash = expectedEffectiveSchema.ResourceKeySeedHash;

        if (
            fingerprint.ResourceKeyCount != expectedResourceKeyCount
            || !fingerprint.ResourceKeySeedHash.AsSpan().SequenceEqual(expectedResourceKeySeedHash)
        )
        {
            await ValidateResourceKeysSlowPathOrThrowAsync(
                connectionString,
                fingerprint,
                mappingSet,
                instance,
                tenant,
                cancellationToken
            );
        }

        validatedResourceKeyMapCache.Set(connectionString, mappingSet);

        logger.LogInformation(
            "Validated PostgreSQL runtime mapping fingerprint for DMS instance {InstanceId}",
            instance.Id
        );

        return false;
    }

    private async Task<PostgresqlDatabaseFingerprint> ReadFingerprintOrThrowAsync(
        string connectionString,
        DmsInstance instance,
        string? tenant,
        MappingSetKey mappingSetKey,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await databaseMetadataReader
                .ReadFingerprintAsync(connectionString, cancellationToken)
                .ConfigureAwait(false);

            return result switch
            {
                PostgresqlDatabaseFingerprintReadResult.Success success => success.Fingerprint,
                PostgresqlDatabaseFingerprintReadResult.MissingEffectiveSchemaTable =>
                    throw new InvalidOperationException(
                        $"{CreateContextPrefix(instance, tenant, mappingSetKey)} is missing required table dms.\"EffectiveSchema\". Provision the database before startup."
                    ),
                PostgresqlDatabaseFingerprintReadResult.MissingEffectiveSchemaRow =>
                    throw new InvalidOperationException(
                        $"{CreateContextPrefix(instance, tenant, mappingSetKey)} has an empty dms.\"EffectiveSchema\" table. The singleton fingerprint row is missing."
                    ),
                PostgresqlDatabaseFingerprintReadResult.InvalidEffectiveSchemaSingleton invalidSingleton =>
                    throw new InvalidOperationException(
                        $"{CreateContextPrefix(instance, tenant, mappingSetKey)} has {invalidSingleton.RowCount} rows in dms.\"EffectiveSchema\". Expected exactly 1 singleton row."
                    ),
                _ => throw new InvalidOperationException(
                    $"{CreateContextPrefix(instance, tenant, mappingSetKey)} returned an unknown database fingerprint read result."
                ),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"{CreateContextPrefix(instance, tenant, mappingSetKey)} could not read dms.\"EffectiveSchema\": {ex.Message}",
                ex
            );
        }
    }

    private static void ValidateEffectiveSchemaHashOrThrow(
        PostgresqlDatabaseFingerprint fingerprint,
        MappingSetKey mappingSetKey,
        DmsInstance instance,
        string? tenant
    )
    {
        if (
            string.Equals(
                fingerprint.EffectiveSchemaHash,
                mappingSetKey.EffectiveSchemaHash,
                StringComparison.Ordinal
            )
        )
        {
            return;
        }

        throw new InvalidOperationException(
            $"{CreateContextPrefix(instance, tenant, mappingSetKey)} failed database fingerprint validation: "
                + $"stored EffectiveSchemaHash '{Sanitize(fingerprint.EffectiveSchemaHash)}' "
                + $"does not match expected '{Sanitize(mappingSetKey.EffectiveSchemaHash)}'."
        );
    }

    private async Task ValidateResourceKeysSlowPathOrThrowAsync(
        string connectionString,
        PostgresqlDatabaseFingerprint fingerprint,
        MappingSet mappingSet,
        DmsInstance instance,
        string? tenant,
        CancellationToken cancellationToken
    )
    {
        PostgresqlResourceKeyReadResult readResult;

        try
        {
            readResult = await databaseMetadataReader
                .ReadResourceKeysAsync(connectionString, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"{CreateContextPrefix(instance, tenant, mappingSet.Key)} could not read dms.\"ResourceKey\" after fingerprint mismatch: {ex.Message}",
                ex
            );
        }

        var fingerprintIssues = BuildFingerprintIssueList(fingerprint, mappingSet.Model.EffectiveSchema);

        switch (readResult)
        {
            case PostgresqlResourceKeyReadResult.MissingResourceKeyTable:
                throw new InvalidOperationException(
                    $"{CreateContextPrefix(instance, tenant, mappingSet.Key)} failed database fingerprint validation: "
                        + $"{fingerprintIssues} Slow-path validation could not continue because dms.\"ResourceKey\" is missing."
                );

            case PostgresqlResourceKeyReadResult.Success success:
                var resourceKeyDiff = BuildResourceKeyDiffReport(
                    success.Rows,
                    mappingSet.Model.EffectiveSchema.ResourceKeysInIdOrder
                );

                if (resourceKeyDiff is null)
                {
                    throw new InvalidOperationException(
                        $"{CreateContextPrefix(instance, tenant, mappingSet.Key)} failed database fingerprint validation: "
                            + $"{fingerprintIssues} Slow-path dms.\"ResourceKey\" validation matched the compiled seed rows, so only dms.\"EffectiveSchema\" is inconsistent."
                    );
                }

                throw new InvalidOperationException(
                    $"{CreateContextPrefix(instance, tenant, mappingSet.Key)} failed database fingerprint validation: "
                        + $"{fingerprintIssues} Slow-path dms.\"ResourceKey\" validation also failed. {resourceKeyDiff}"
                );

            default:
                throw new InvalidOperationException(
                    $"{CreateContextPrefix(instance, tenant, mappingSet.Key)} returned an unknown dms.\"ResourceKey\" read result."
                );
        }
    }

    private static string BuildFingerprintIssueList(
        PostgresqlDatabaseFingerprint fingerprint,
        EffectiveSchemaInfo expectedEffectiveSchema
    )
    {
        var issues = new List<string>();
        var expectedResourceKeyCount = expectedEffectiveSchema.ResourceKeyCount;

        if (fingerprint.ResourceKeyCount != expectedResourceKeyCount)
        {
            issues.Add(
                $"ResourceKeyCount stored={fingerprint.ResourceKeyCount} expected={expectedResourceKeyCount}."
            );
        }

        if (
            !fingerprint
                .ResourceKeySeedHash.AsSpan()
                .SequenceEqual(expectedEffectiveSchema.ResourceKeySeedHash)
        )
        {
            issues.Add(
                $"ResourceKeySeedHash stored={Convert.ToHexString(fingerprint.ResourceKeySeedHash)} "
                    + $"expected={Convert.ToHexString(expectedEffectiveSchema.ResourceKeySeedHash)}."
            );
        }

        return string.Join(" ", issues);
    }

    private static string? BuildResourceKeyDiffReport(
        IReadOnlyList<PostgresqlResourceKeyRow> actualRows,
        IReadOnlyList<ResourceKeyEntry> expectedRows
    )
    {
        try
        {
            var actualById = actualRows.ToDictionary(row => row.ResourceKeyId);
            var expectedById = expectedRows.ToDictionary(row => row.ResourceKeyId);

            var missingIds = new List<string>();
            var unexpectedIds = new List<string>();
            var modifiedRows = new List<string>();

            foreach (var (resourceKeyId, expectedRow) in expectedById.OrderBy(entry => entry.Key))
            {
                if (!actualById.TryGetValue(resourceKeyId, out var actualRow))
                {
                    missingIds.Add(resourceKeyId.ToString());
                    continue;
                }

                var rowDiff = BuildResourceKeyRowDiff(expectedRow, actualRow);

                if (rowDiff is not null)
                {
                    modifiedRows.Add($"ResourceKeyId={resourceKeyId}: {rowDiff}");
                }
            }

            foreach (var unexpectedId in actualById.Keys.Except(expectedById.Keys).OrderBy(id => id))
            {
                unexpectedIds.Add(unexpectedId.ToString());
            }

            if (missingIds.Count == 0 && unexpectedIds.Count == 0 && modifiedRows.Count == 0)
            {
                return null;
            }

            var builder = new StringBuilder("Seed data mismatch in dms.ResourceKey:");

            if (missingIds.Count > 0)
            {
                builder.Append(" Missing rows [");
                builder.Append(string.Join(", ", missingIds.Take(MaxRowsPerDiffSection)));
                builder.Append("].");

                if (missingIds.Count > MaxRowsPerDiffSection)
                {
                    builder.Append($" (+{missingIds.Count - MaxRowsPerDiffSection} more).");
                }
            }

            if (unexpectedIds.Count > 0)
            {
                builder.Append(" Unexpected rows [");
                builder.Append(string.Join(", ", unexpectedIds.Take(MaxRowsPerDiffSection)));
                builder.Append("].");

                if (unexpectedIds.Count > MaxRowsPerDiffSection)
                {
                    builder.Append($" (+{unexpectedIds.Count - MaxRowsPerDiffSection} more).");
                }
            }

            if (modifiedRows.Count > 0)
            {
                builder.Append(" Modified rows: ");
                builder.Append(string.Join("; ", modifiedRows.Take(MaxRowsPerDiffSection)));

                if (modifiedRows.Count > MaxRowsPerDiffSection)
                {
                    builder.Append($" (+{modifiedRows.Count - MaxRowsPerDiffSection} more).");
                }
            }

            return builder.ToString();
        }
        catch (ArgumentException ex)
        {
            return $"Seed data mismatch in dms.ResourceKey: duplicate ResourceKeyId detected during diff. {ex.Message}";
        }
    }

    private static string? BuildResourceKeyRowDiff(
        ResourceKeyEntry expectedRow,
        PostgresqlResourceKeyRow actualRow
    )
    {
        var differences = new List<string>();

        if (!string.Equals(expectedRow.Resource.ProjectName, actualRow.ProjectName, StringComparison.Ordinal))
        {
            differences.Add(
                $"ProjectName expected='{Sanitize(expectedRow.Resource.ProjectName)}' actual='{Sanitize(actualRow.ProjectName)}'"
            );
        }

        if (
            !string.Equals(
                expectedRow.Resource.ResourceName,
                actualRow.ResourceName,
                StringComparison.Ordinal
            )
        )
        {
            differences.Add(
                $"ResourceName expected='{Sanitize(expectedRow.Resource.ResourceName)}' actual='{Sanitize(actualRow.ResourceName)}'"
            );
        }

        if (!string.Equals(expectedRow.ResourceVersion, actualRow.ResourceVersion, StringComparison.Ordinal))
        {
            differences.Add(
                $"ResourceVersion expected='{Sanitize(expectedRow.ResourceVersion)}' actual='{Sanitize(actualRow.ResourceVersion)}'"
            );
        }

        return differences.Count == 0 ? null : string.Join(", ", differences);
    }

    private static string CreateContextPrefix(
        DmsInstance instance,
        string? tenant,
        MappingSetKey mappingSetKey
    )
    {
        var tenantLabel = tenant is null ? "(default)" : Sanitize(tenant);

        return $"DMS instance '{Sanitize(instance.InstanceName)}' (ID {instance.Id}, tenant '{tenantLabel}') "
            + $"expected mapping {FormatMappingSetKey(mappingSetKey)}";
    }

    private static string FormatMappingSetKey(MappingSetKey mappingSetKey)
    {
        return $"[EffectiveSchemaHash='{Sanitize(mappingSetKey.EffectiveSchemaHash)}', Dialect='{mappingSetKey.Dialect}', RelationalMappingVersion='{Sanitize(mappingSetKey.RelationalMappingVersion)}']";
    }

    private static string Sanitize(string? value) =>
        LoggingSanitizer.SanitizeForLogging(value).Replace("\r", string.Empty).Replace("\n", string.Empty);
}

internal sealed record PostgresqlRuntimeInstanceMappingValidationSummary(
    int InstanceCount,
    int ValidatedDatabaseCount,
    int ReusedValidationCount
);
