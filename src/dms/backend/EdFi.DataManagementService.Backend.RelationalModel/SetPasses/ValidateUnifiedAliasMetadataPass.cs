// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.SetPasses;

/// <summary>
/// Validates strict unified-alias metadata once after key unification.
/// </summary>
public sealed class ValidateUnifiedAliasMetadataPass : IRelationalModelSetPass
{
    /// <summary>
    /// Validates and caches strict unified-alias metadata for downstream passes.
    /// </summary>
    public void Execute(RelationalModelSetBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.ClearStrictUnifiedAliasTableMetadataCache();
        UnifiedAliasStrictMetadataCache.ValidateAndCache(context);
    }
}
