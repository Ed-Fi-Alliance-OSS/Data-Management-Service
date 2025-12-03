// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DmsConfigurationService.Backend.Services;

/// <summary>
/// Extension methods for TenantContext to support SQL query building.
/// </summary>
public static class TenantContextExtensions
{
    /// <summary>
    /// Gets the SQL WHERE clause condition for tenant filtering.
    /// Returns "(TenantId = @TenantId OR TenantId IS NULL)" when multi-tenant mode is active and includeShared is true,
    /// returns "TenantId = @TenantId" when multi-tenant mode is active and includeShared is false,
    /// or "TenantId IS NULL" when in non-multi-tenant mode.
    /// </summary>
    /// <param name="tenantContext">The tenant context.</param>
    /// <param name="tableAlias">Optional table alias to prefix the TenantId column (e.g., "v" produces "v.TenantId").</param>
    /// <param name="includeShared">When true and in multi-tenant mode, includes records with TenantId IS NULL as shared/global records.</param>
    /// <returns>The SQL condition string for tenant filtering.</returns>
    public static string TenantWhereClause(
        this TenantContext tenantContext,
        string? tableAlias = null,
        bool includeShared = false
    )
    {
        var column = string.IsNullOrEmpty(tableAlias) ? "TenantId" : $"{tableAlias}.TenantId";
        return tenantContext switch
        {
            TenantContext.Multitenant when includeShared => $"({column} = @TenantId OR {column} IS NULL)",
            TenantContext.Multitenant => $"{column} = @TenantId",
            TenantContext.NotMultitenant => $"{column} IS NULL",
            _ => throw new InvalidOperationException(
                $"Unexpected tenant context type: {tenantContext.GetType().Name}"
            ),
        };
    }
}
