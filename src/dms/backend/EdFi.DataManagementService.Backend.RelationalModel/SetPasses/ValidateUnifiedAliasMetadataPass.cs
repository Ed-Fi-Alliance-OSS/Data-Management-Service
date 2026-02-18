// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Validates and warms strict unified-alias metadata after key unification and abstract identity derivation.
///
/// This pass exists to:
/// - fail fast on invalid unified-alias storage metadata (missing/invalid presence gates, invalid strict synthetic candidates),
/// - cache strict table metadata for downstream passes that resolve storage columns (constraints, index/trigger inventories),
/// - avoid stale metadata when earlier passes replace table models (e.g., key unification / derivation passes).
/// </summary>
public sealed class ValidateUnifiedAliasMetadataPass : IRelationalModelSetPass
{
    /// <summary>
    /// Clears any previously cached strict metadata, then validates and caches strict unified-alias metadata for all
    /// schema-derived tables (concrete resource tables and derived abstract identity tables).
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.ClearStrictUnifiedAliasTableMetadataCache();
        UnifiedAliasStrictMetadataCache.ValidateAndCache(context);
    }
}
