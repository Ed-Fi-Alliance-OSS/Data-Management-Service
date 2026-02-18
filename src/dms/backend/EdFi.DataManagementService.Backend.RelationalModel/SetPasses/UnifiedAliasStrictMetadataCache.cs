// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Shared strict unified-alias metadata cache for set-level passes.
/// </summary>
internal static class UnifiedAliasStrictMetadataCache
{
    private static readonly UnifiedAliasStorageResolver.PresenceGateMetadataOptions _strictOptions = new(
        ThrowIfPresenceColumnMissing: true,
        ThrowIfInvalidStrictSyntheticCandidate: true
    );

    /// <summary>
    /// Validates unified-alias metadata for known tables and caches strict metadata.
    /// </summary>
    public static void ValidateAndCache(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var table in EnumerateTables(context))
        {
            _ = GetOrBuild(context, table);
        }
    }

    /// <summary>
    /// Resolves strict unified-alias metadata for one table, building and caching when missing.
    /// </summary>
    public static UnifiedAliasStorageResolver.TableMetadata GetOrBuild(
        RelationalModelSetBuilderContext context,
        DbTableModel table
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(table);

        if (context.TryGetStrictUnifiedAliasTableMetadata(table, out var cachedMetadata))
        {
            return cachedMetadata;
        }

        var metadata = UnifiedAliasStorageResolver.BuildTableMetadata(table, _strictOptions);
        context.SetStrictUnifiedAliasTableMetadata(table, metadata);
        return metadata;
    }

    /// <summary>
    /// Enumerates all schema-derived tables that participate in strict unified-alias metadata validation.
    /// </summary>
    private static IEnumerable<DbTableModel> EnumerateTables(RelationalModelSetBuilderContext context)
    {
        HashSet<DbTableName> seenTables = [];

        foreach (var concreteResource in context.ConcreteResourcesInNameOrder)
        {
            if (concreteResource.StorageKind == ResourceStorageKind.SharedDescriptorTable)
            {
                continue;
            }

            foreach (var table in concreteResource.RelationalModel.TablesInDependencyOrder)
            {
                if (seenTables.Add(table.Table))
                {
                    yield return table;
                }
            }
        }

        foreach (var abstractTable in context.AbstractIdentityTablesInNameOrder)
        {
            if (seenTables.Add(abstractTable.TableModel.Table))
            {
                yield return abstractTable.TableModel;
            }
        }
    }
}
